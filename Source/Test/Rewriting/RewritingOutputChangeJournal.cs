// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Coyote.IO;

namespace Microsoft.Coyote.Rewriting
{
    /// <summary>
    /// Records only output files changed by a mirror attempt and can restore them in reverse order.
    /// </summary>
    internal sealed class RewritingOutputChangeJournal
    {
        private const int SchemaVersion = 4;

        private const int FingerprintedPublicationSchemaVersion = 3;

        private const int PublicationSchemaVersion = 2;

        private const int LegacySchemaVersion = 1;

        private const string JournalFileName = "journal.json";

        private const string ActiveState = "active";

        private const string RestoringState = "restoring";

        private const string RestoredState = "restored";

        private const string CleanupState = "cleanup";

        internal sealed class Change
        {
            public string TargetPath { get; set; }

            public string BackupPath { get; set; }

            public bool Existed { get; set; }

            public long BackupLength { get; set; }

            public string BackupFingerprint { get; set; }
        }

        internal sealed class JournalManifest
        {
            public int Version { get; set; }

            public string OutputDirectory { get; set; }

            public long CreatedUtcTicks { get; set; }

            public string State { get; set; }

            public List<Change> Changes { get; set; }

            public List<string> CreatedDirectories { get; set; }

            public List<PendingPublication> PendingPublications { get; set; }
        }

        internal sealed class PendingPublication
        {
            public string TargetPath { get; set; }

            public bool ExpectedToExist { get; set; }

            public long ExpectedLength { get; set; }

            public string ExpectedFingerprint { get; set; }

            public string ReplacementBackupPath { get; set; }

            public long StagedLength { get; set; }

            public string StagedFingerprint { get; set; }
        }

        private readonly IFileSystem FileSystem;
        private readonly string OutputDirectory;
        private readonly List<Change> Changes;
        private readonly HashSet<string> CapturedPaths;

        private readonly List<string> CreatedDirectories;

        private readonly HashSet<string> CapturedDirectories;

        private readonly List<PendingPublication> PendingPublications;

        private long CreatedUtcTicks;

        private readonly int Version;

        private string State;

        private bool IsMutationStateAmbiguous;

        internal RewritingOutputChangeJournal(IFileSystem fileSystem, string outputDirectory)
        {
            this.FileSystem = fileSystem;
            this.OutputDirectory = RewritingCacheValidator.NormalizeDirectory(outputDirectory);
            this.BackupDirectory = this.OutputDirectory + ".mirror-backup-" + Guid.NewGuid().ToString("N");
            this.Changes = new List<Change>();
            this.CapturedPaths = new HashSet<string>(fileSystem.IsCaseInsensitive(outputDirectory) ?
                StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            this.CreatedDirectories = new List<string>();
            this.CapturedDirectories = new HashSet<string>(this.CapturedPaths.Comparer);
            this.PendingPublications = new List<PendingPublication>();
            this.CreatedUtcTicks = DateTime.UtcNow.Ticks;
            this.Version = SchemaVersion;
            this.State = ActiveState;
            this.FileSystem.CreateDirectory(this.BackupDirectory);
            this.SaveManifest();
        }

        internal string BackupDirectory { get; }

        /// <summary>
        /// Publishes one staged file without a verification-to-write race.
        /// </summary>
        internal PendingPublication Publish(string sourcePath, string targetPath, MirroredFile? expected)
        {
            this.EnsureActiveForMutation();
            string source = RewritingCacheValidator.NormalizeFile(sourcePath);
            string target = RewritingCacheValidator.NormalizeFile(targetPath);
            var pending = new PendingPublication()
            {
                TargetPath = target,
                ExpectedToExist = expected.HasValue,
                ExpectedLength = expected?.Length ?? 0,
                ExpectedFingerprint = expected?.Fingerprint,
                StagedLength = this.FileSystem.GetFile(source).Length,
                StagedFingerprint = RewritingCacheValidator.ComputeFileFingerprint(
                    this.FileSystem, source)
            };

            if (expected.HasValue)
            {
                string relative = target.Substring(this.OutputDirectory.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                pending.ReplacementBackupPath = Path.Combine(
                    this.BackupDirectory, relative + ".publish-" + Guid.NewGuid().ToString("N") + ".bak");
                this.FileSystem.CreateDirectory(Path.GetDirectoryName(pending.ReplacementBackupPath));
            }

            this.PendingPublications.Add(pending);
            this.SaveManifest();

            if (!expected.HasValue)
            {
                try
                {
                    this.FileSystem.MoveFile(source, target);
                }
                catch
                {
                    this.RemovePendingPublication(pending);
                    throw new IOException(
                        $"The source directory '{this.OutputDirectory}' changed after its rewrite snapshot was created.");
                }

                var change = new Change() { TargetPath = target, Existed = false };
                this.Changes.Add(change);
                this.CapturedPaths.Add(target);
                this.SaveManifest();
                return pending;
            }

            MirroredFile baseline = expected.Value;
            try
            {
                if (!this.ContainsBytes(target, baseline.Length, baseline.Fingerprint))
                {
                    throw new IOException(
                        $"The source directory '{this.OutputDirectory}' changed after its rewrite snapshot was created.");
                }

                this.FileSystem.ReplaceFile(source, target, pending.ReplacementBackupPath);
            }
            catch (Exception replacementFailure)
            {
                bool tookEffect = this.FileSystem.FileExists(pending.ReplacementBackupPath) &&
                    this.ContainsBytes(target, pending.StagedLength, pending.StagedFingerprint);
                if (!tookEffect)
                {
                    if (!this.FileSystem.FileExists(pending.ReplacementBackupPath))
                    {
                        this.RemovePendingPublication(pending);
                    }

                    throw;
                }

                // ReplaceFile can report an error after the atomic rename took effect. The durable
                // pending record plus the captured backup make that state unambiguous, so finish
                // publication instead of exposing a false failure to the caller.
                _ = replacementFailure;
            }

            IFileEntry captured = this.FileSystem.GetFile(pending.ReplacementBackupPath);
            string capturedFingerprint = captured.Exists ?
                RewritingCacheValidator.ComputeFileFingerprint(
                    this.FileSystem, pending.ReplacementBackupPath) : null;
            if (!captured.Exists || captured.Length != baseline.Length ||
                !string.Equals(capturedFingerprint, baseline.Fingerprint, StringComparison.Ordinal))
            {
                // A pre-replace race was captured atomically in the backup. Only put those actual
                // bytes back while the target still contains the bytes owned by this journal.
                if (captured.Exists &&
                    this.ContainsBytes(target, pending.StagedLength, pending.StagedFingerprint))
                {
                    this.FileSystem.ReplaceFile(pending.ReplacementBackupPath, target, null);
                    this.RemovePendingPublication(pending);
                }

                throw new IOException(
                    $"The source directory '{this.OutputDirectory}' changed after its rewrite snapshot was created.");
            }

            var publishedChange = new Change()
            {
                TargetPath = target,
                BackupPath = pending.ReplacementBackupPath,
                Existed = true,
                BackupLength = captured.Length,
                BackupFingerprint = capturedFingerprint
            };
            this.Changes.Add(publishedChange);
            this.CapturedPaths.Add(target);
            this.SaveManifest();
            return pending;
        }

        private bool ContainsBytes(string path, long length, string fingerprint)
        {
            IFileEntry file = this.FileSystem.GetFile(path);
            return file.Exists && file.Length == length &&
                string.Equals(RewritingCacheValidator.ComputeFileFingerprint(
                    this.FileSystem, path), fingerprint, StringComparison.Ordinal);
        }

        private void RemovePendingPublication(PendingPublication pending)
        {
            this.PendingPublications.Remove(pending);
            this.SaveManifest();
        }

        internal void Capture(string targetPath)
        {
            this.Capture(targetPath, null);
        }

        /// <summary>
        /// Captures a target through an already-held exclusive stream when publication owns one.
        /// </summary>
        private void Capture(string targetPath, Stream lockedTargetStream)
        {
            this.EnsureActiveForMutation();
            string normalized = RewritingCacheValidator.NormalizeFile(targetPath);
            if (this.CapturedPaths.Contains(normalized))
            {
                return;
            }

            bool existed = this.FileSystem.FileExists(normalized);
            var change = new Change()
            {
                TargetPath = normalized,
                Existed = existed
            };
            JournalManifest prior = this.CreateManifest();

            try
            {
                if (existed)
                {
                    string relative = normalized.Substring(this.OutputDirectory.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    change.BackupPath = Path.Combine(this.BackupDirectory, relative);
                    this.FileSystem.CreateDirectory(Path.GetDirectoryName(change.BackupPath));
                    if (lockedTargetStream is null)
                    {
                        this.FileSystem.CopyFile(normalized, change.BackupPath, false);
                        change.BackupLength = this.FileSystem.GetFile(change.BackupPath).Length;
                        change.BackupFingerprint = RewritingCacheValidator.ComputeFileFingerprint(
                            this.FileSystem, change.BackupPath);
                    }
                    else
                    {
                        using Stream backupStream = this.FileSystem.OpenWriteNewExclusive(change.BackupPath);
                        lockedTargetStream.Position = 0;
                        lockedTargetStream.CopyTo(backupStream);
                        this.FileSystem.FlushWrite(backupStream);
                        change.BackupLength = backupStream.Length;
                        change.BackupFingerprint =
                            RewritingCacheValidator.ComputeStreamFingerprint(backupStream);
                    }
                }

                this.FileSystem.CreateDirectory(this.BackupDirectory);
                var proposedChanges = new List<Change>(this.Changes) { change };
                JournalManifest proposed = this.CreateManifest(proposedChanges);
                try
                {
                    this.SaveManifest(proposed);
                }
                catch (Exception saveFailure)
                {
                    try
                    {
                        if (this.ReconcileCaptureFailure(prior, proposed))
                        {
                            this.ApplyManifest(proposed);
                        }
                        else if (!string.IsNullOrEmpty(change.BackupPath) &&
                            this.FileSystem.FileExists(change.BackupPath))
                        {
                            this.FileSystem.DeleteFile(change.BackupPath);
                        }
                    }
                    catch (Exception reconciliationFailure)
                    {
                        this.IsMutationStateAmbiguous = true;
                        throw new IOException(
                            "The rewrite journal manifest has an ambiguous state after capture failed.",
                            new AggregateException(saveFailure, reconciliationFailure));
                    }

                    throw;
                }

                this.ApplyManifest(proposed);
            }
            catch
            {
                throw;
            }
        }

        internal void CaptureDirectory(string directory)
        {
            this.EnsureActiveForMutation();
            string normalized = RewritingCacheValidator.NormalizeDirectory(directory);
            var comparer = new FileSystemPathComparer(this.FileSystem);
            if (!RewritingOutputMirror.IsWithin(normalized, this.OutputDirectory, comparer))
            {
                throw new ArgumentException(
                    $"The directory '{normalized}' is outside the rewrite output '{this.OutputDirectory}'.",
                    nameof(directory));
            }

            var missing = new List<string>();
            for (string current = normalized;
                RewritingOutputMirror.IsWithin(current, this.OutputDirectory, comparer) &&
                !this.FileSystem.DirectoryExists(current);
                current = Path.GetDirectoryName(current))
            {
                missing.Add(current);
                if (comparer.Equals(current, this.OutputDirectory))
                {
                    break;
                }
            }

            var added = new List<string>();
            foreach (string current in Enumerable.Reverse(missing))
            {
                if (this.CapturedDirectories.Add(current))
                {
                    this.CreatedDirectories.Add(current);
                    added.Add(current);
                }
            }

            if (added.Count is 0)
            {
                return;
            }

            try
            {
                this.SaveManifest();
            }
            catch
            {
                this.CreatedDirectories.RemoveRange(
                    this.CreatedDirectories.Count - added.Count, added.Count);
                foreach (string current in added)
                {
                    this.CapturedDirectories.Remove(current);
                }

                throw;
            }
        }

        private void EnsureActiveForMutation()
        {
            if (this.IsMutationStateAmbiguous)
            {
                throw new InvalidOperationException(
                    "Cannot capture output changes while the rewrite journal state is ambiguous.");
            }

            if (this.State is ActiveState)
            {
                return;
            }

            if (this.State is not RestoredState)
            {
                throw new InvalidOperationException(
                    $"Cannot capture output changes while the rewrite journal is '{this.State}'.");
            }

            // Restore atomically consumes backups. A retry is therefore a new transaction in the
            // same journal directory and must recapture every target instead of reusing the stale
            // change records from the completed rollback.
            JournalManifest rearmed = this.CreateManifest(new List<Change>());
            rearmed.State = ActiveState;
            rearmed.CreatedDirectories.Clear();
            rearmed.PendingPublications.Clear();
            this.SaveManifest(rearmed);
            this.ApplyManifest(rearmed);
        }

        internal void Restore()
        {
            this.State = RestoringState;
            this.SaveManifest();
            Exception failure = null;
            var comparer = new FileSystemPathComparer(this.FileSystem);
            foreach (PendingPublication pending in this.PendingPublications)
            {
                Change change = this.Changes.LastOrDefault(candidate =>
                    comparer.Equals(candidate.TargetPath, pending.TargetPath));
                if (change != null)
                {
                    continue;
                }

                try
                {
                    if (!pending.ExpectedToExist)
                    {
                        if (!this.FileSystem.FileExists(pending.TargetPath))
                        {
                            continue;
                        }

                        if (!this.ContainsBytes(
                            pending.TargetPath, pending.StagedLength, pending.StagedFingerprint))
                        {
                            throw new IOException(
                                $"Pending publication target '{pending.TargetPath}' no longer contains journal-owned bytes.");
                        }

                        this.FileSystem.DeleteFile(pending.TargetPath);
                        continue;
                    }

                    bool containsExpectedBytes = this.Version >= SchemaVersion &&
                        this.ContainsBytes(pending.TargetPath,
                            pending.ExpectedLength, pending.ExpectedFingerprint);
                    bool hasReplacementBackup = this.Version >= SchemaVersion &&
                        !string.IsNullOrEmpty(pending.ReplacementBackupPath) &&
                        this.FileSystem.FileExists(pending.ReplacementBackupPath);
                    if (!hasReplacementBackup)
                    {
                        // Either replacement never happened, or an earlier recovery already
                        // restored the target and atomically consumed the backup.
                        if (containsExpectedBytes || this.Version < SchemaVersion)
                        {
                            continue;
                        }

                        throw new IOException(
                            $"Pending publication backup for '{pending.TargetPath}' is missing or corrupt.");
                    }

                    if (!this.ContainsBytes(
                        pending.TargetPath, pending.StagedLength, pending.StagedFingerprint))
                    {
                        // Never overwrite a target changed after publication.
                        throw new IOException(
                            $"Pending publication target '{pending.TargetPath}' no longer contains journal-owned bytes.");
                    }

                    // Restore the bytes atomically captured by ReplaceFile. They can intentionally
                    // differ from ExpectedFingerprint when the target raced immediately before the
                    // replacement; restoring those actual bytes is still the only lossless action.
                    this.FileSystem.ReplaceFile(
                        pending.ReplacementBackupPath, pending.TargetPath, null);

                    if (!this.ContainsBytes(pending.TargetPath,
                        pending.ExpectedLength, pending.ExpectedFingerprint))
                    {
                        throw new IOException(
                            $"Pending publication target '{pending.TargetPath}' raced before replacement; " +
                            "the atomically captured bytes were restored.");
                    }
                }
                catch (Exception ex)
                {
                    failure = failure is null ? ex : new AggregateException(failure, ex);
                }
            }

            foreach (Change change in Enumerable.Reverse(this.Changes))
            {
                try
                {
                    PendingPublication pending = this.PendingPublications.LastOrDefault(candidate =>
                        comparer.Equals(candidate.TargetPath, change.TargetPath));
                    if (pending != null)
                    {
                        bool exists = this.FileSystem.FileExists(change.TargetPath);
                        string currentFingerprint = exists ?
                            RewritingCacheValidator.ComputeFileFingerprint(
                                this.FileSystem, change.TargetPath) : null;
                        bool containsStagedBytes = exists &&
                            (this.Version < SchemaVersion ||
                             this.FileSystem.GetFile(change.TargetPath).Length == pending.StagedLength) &&
                            string.Equals(currentFingerprint, pending.StagedFingerprint,
                                StringComparison.Ordinal);
                        bool containsOriginalBytes = change.Existed && exists &&
                            this.FileSystem.GetFile(change.TargetPath).Length == change.BackupLength &&
                            string.Equals(currentFingerprint, change.BackupFingerprint,
                                StringComparison.Ordinal);
                        bool alreadyRestored = containsOriginalBytes || (!change.Existed && !exists);
                        if (alreadyRestored)
                        {
                            continue;
                        }

                        if (!containsStagedBytes)
                        {
                            throw new IOException(
                                $"Published target '{change.TargetPath}' no longer contains journal-owned bytes.");
                        }
                    }

                    if (change.Existed)
                    {
                        bool exists = this.FileSystem.FileExists(change.TargetPath);
                        IFileEntry backup = this.FileSystem.GetFile(change.BackupPath);
                        if (!backup.Exists || backup.Length != change.BackupLength ||
                            !string.Equals(RewritingCacheValidator.ComputeFileFingerprint(
                                this.FileSystem, change.BackupPath), change.BackupFingerprint,
                                StringComparison.Ordinal))
                        {
                            throw new IOException(
                                $"Recovery backup '{change.BackupPath}' is missing or corrupt.");
                        }

                        this.FileSystem.CreateDirectory(Path.GetDirectoryName(change.TargetPath));
                        if (exists)
                        {
                            this.FileSystem.ReplaceFile(change.BackupPath, change.TargetPath, null);
                        }
                        else
                        {
                            this.FileSystem.MoveFile(change.BackupPath, change.TargetPath);
                        }
                    }
                    else
                    {
                        this.FileSystem.DeleteFile(change.TargetPath);
                    }
                }
                catch (Exception ex)
                {
                    failure = failure is null ? ex : new AggregateException(failure, ex);
                }
            }

            foreach (string directory in Enumerable.Reverse(this.CreatedDirectories))
            {
                try
                {
                    if (this.FileSystem.DirectoryExists(directory))
                    {
                        this.FileSystem.DeleteDirectory(directory, false);
                    }
                }
                catch (Exception ex)
                {
                    failure = failure is null ? ex : new AggregateException(failure, ex);
                }
            }

            if (failure != null)
            {
                throw new IOException("Unable to restore one or more mirrored output files.", failure);
            }

            this.PendingPublications.Clear();
            this.State = RestoredState;
            this.SaveManifest();
        }

        /// <summary>
        /// Verifies that every successfully published output still contains the staged bytes.
        /// </summary>
        internal void ValidatePublishedOutputs()
        {
            foreach (PendingPublication publication in this.PendingPublications)
            {
                IFileEntry current = this.FileSystem.GetFile(publication.TargetPath);
                if (!current.Exists || current.Length != publication.StagedLength ||
                    !string.Equals(RewritingCacheValidator.ComputeFileFingerprint(
                        this.FileSystem, publication.TargetPath), publication.StagedFingerprint,
                        StringComparison.Ordinal))
                {
                    throw new IOException(
                        $"Published output '{publication.TargetPath}' changed before the rewrite transaction completed.");
                }
            }
        }

        internal void Complete()
        {
            if (!this.FileSystem.DirectoryExists(this.BackupDirectory))
            {
                return;
            }

            if (this.State is not CleanupState)
            {
                string previousState = this.State;
                this.State = CleanupState;
                try
                {
                    this.SaveManifest();
                }
                catch
                {
                    this.State = previousState;
                    throw;
                }
            }

            string manifestPath = Path.Combine(this.BackupDirectory, JournalFileName);
            this.DeleteDirectoryContents(this.BackupDirectory, manifestPath);
            if (this.FileSystem.FileExists(manifestPath))
            {
                this.FileSystem.DeleteFile(manifestPath);
            }

            if (this.FileSystem.DirectoryExists(this.BackupDirectory))
            {
                this.FileSystem.DeleteDirectory(this.BackupDirectory, false);
            }
        }

        internal static IReadOnlyList<string> FindJournals(IFileSystem fileSystem, string outputDirectory)
        {
            string normalized = RewritingCacheValidator.NormalizeDirectory(outputDirectory);
            string parent = Path.GetDirectoryName(normalized);
            string pattern = Path.GetFileName(normalized) + ".mirror-backup-*";
            return fileSystem.DirectoryExists(parent) ?
                fileSystem.GetDirectories(parent, pattern, false) : Array.Empty<string>();
        }

        internal static void RecoverAll(IFileSystem fileSystem, string outputDirectory)
        {
            string normalized = RewritingCacheValidator.NormalizeDirectory(outputDirectory);
            var journals = new List<RewritingOutputChangeJournal>();
            foreach (string directory in FindJournals(fileSystem, normalized))
            {
                string manifestPath = Path.Combine(directory, JournalFileName);
                if (!fileSystem.FileExists(manifestPath))
                {
                    if (fileSystem.GetFiles(directory, "*").Length is 0 &&
                        fileSystem.GetDirectories(directory, "*", false).Length is 0)
                    {
                        fileSystem.DeleteDirectory(directory, false);
                        continue;
                    }

                    if (TryDiscardInterruptedInitialization(
                        fileSystem, normalized, directory, manifestPath + ".tmp"))
                    {
                        continue;
                    }

                    throw new IOException(
                        $"The legacy rewrite recovery journal '{directory}' cannot be recovered automatically.");
                }

                JournalManifest manifest;
                try
                {
                    manifest = JsonSerializer.Deserialize<JournalManifest>(
                        fileSystem.ReadAllText(manifestPath));
                }
                catch (Exception ex)
                {
                    throw new IOException($"The rewrite recovery journal '{directory}' is unreadable.", ex);
                }

                if (manifest is null ||
                    (manifest.Version != SchemaVersion &&
                     manifest.Version != FingerprintedPublicationSchemaVersion &&
                     manifest.Version != PublicationSchemaVersion &&
                     manifest.Version != LegacySchemaVersion) ||
                    !string.Equals(RewritingCacheValidator.NormalizeDirectory(manifest.OutputDirectory),
                        normalized, fileSystem.IsCaseInsensitive(normalized) ?
                            StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    throw new IOException($"The rewrite recovery journal '{directory}' does not describe '{normalized}'.");
                }

                ValidateManifest(fileSystem, normalized, directory, manifest);

                journals.Add(new RewritingOutputChangeJournal(
                    fileSystem, normalized, directory, manifest));
            }

            if (journals.Count > 1)
            {
                throw new IOException(
                    $"Multiple rewrite recovery journals describe '{normalized}' and cannot be recovered automatically.");
            }

            foreach (RewritingOutputChangeJournal journal in journals)
            {
                if (journal.State is ActiveState or RestoringState)
                {
                    journal.Restore();
                }

                journal.Complete();
            }
        }

        private static bool TryDiscardInterruptedInitialization(IFileSystem fileSystem,
            string outputDirectory, string backupDirectory, string temporaryManifestPath)
        {
            string[] files = fileSystem.GetFiles(backupDirectory, "*");
            if (files.Length is not 1 ||
                fileSystem.GetDirectories(backupDirectory, "*", false).Length is not 0 ||
                !new FileSystemPathComparer(fileSystem).Equals(files[0], temporaryManifestPath))
            {
                return false;
            }

            try
            {
                JournalManifest manifest = JsonSerializer.Deserialize<JournalManifest>(
                    fileSystem.ReadAllText(temporaryManifestPath));
                if (manifest is null ||
                    (manifest.Version != SchemaVersion &&
                     manifest.Version != FingerprintedPublicationSchemaVersion &&
                     manifest.Version != PublicationSchemaVersion &&
                     manifest.Version != LegacySchemaVersion) ||
                    manifest.State != ActiveState || manifest.Changes is null ||
                    manifest.Changes.Count is not 0 || manifest.CreatedDirectories is null ||
                    manifest.CreatedDirectories.Count is not 0 ||
                    (manifest.PendingPublications != null &&
                     manifest.PendingPublications.Count is not 0) ||
                    !string.Equals(RewritingCacheValidator.NormalizeDirectory(manifest.OutputDirectory),
                        outputDirectory, fileSystem.IsCaseInsensitive(outputDirectory) ?
                            StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    return false;
                }

                ValidateManifest(fileSystem, outputDirectory, backupDirectory, manifest);
            }
            catch
            {
                return false;
            }

            fileSystem.DeleteFile(temporaryManifestPath);
            fileSystem.DeleteDirectory(backupDirectory, false);
            return true;
        }

        private static void ValidateManifest(IFileSystem fileSystem, string outputDirectory,
            string backupDirectory, JournalManifest manifest)
        {
            var comparer = new FileSystemPathComparer(fileSystem);
            if (manifest.CreatedUtcTicks <= 0 ||
                (manifest.State != ActiveState && manifest.State != RestoringState &&
                 manifest.State != RestoredState && manifest.State != CleanupState) ||
                 manifest.Changes is null ||
                 manifest.CreatedDirectories is null ||
                 (manifest.Version >= PublicationSchemaVersion && manifest.PendingPublications is null))
            {
                throw new IOException($"The rewrite recovery journal '{backupDirectory}' is incomplete.");
            }

            var targets = new HashSet<string>(comparer);
            foreach (Change change in manifest.Changes)
            {
                if (change is null || string.IsNullOrEmpty(change.TargetPath) ||
                    !RewritingOutputMirror.IsWithin(change.TargetPath, outputDirectory, comparer) ||
                    !targets.Add(change.TargetPath) || (change.Existed &&
                    (string.IsNullOrEmpty(change.BackupPath) || change.BackupLength < 0 ||
                     string.IsNullOrEmpty(change.BackupFingerprint) ||
                     !RewritingOutputMirror.IsWithin(change.BackupPath, backupDirectory, comparer))) ||
                    (!change.Existed && !string.IsNullOrEmpty(change.BackupPath)))
                {
                    throw new IOException(
                        $"The rewrite recovery journal '{backupDirectory}' contains an invalid file change.");
                }
            }

            var pendingTargets = new HashSet<string>(comparer);
            foreach (PendingPublication pending in
                manifest.PendingPublications ?? Enumerable.Empty<PendingPublication>())
            {
                if (manifest.Version < PublicationSchemaVersion || pending is null ||
                    string.IsNullOrEmpty(pending.TargetPath) ||
                    string.IsNullOrEmpty(pending.StagedFingerprint) ||
                    (manifest.Version >= FingerprintedPublicationSchemaVersion && pending.StagedLength < 0) ||
                    (manifest.Version >= SchemaVersion && pending.ExpectedToExist &&
                     (pending.ExpectedLength < 0 || string.IsNullOrEmpty(pending.ExpectedFingerprint) ||
                      string.IsNullOrEmpty(pending.ReplacementBackupPath) ||
                      !RewritingOutputMirror.IsWithin(
                          pending.ReplacementBackupPath, backupDirectory, comparer))) ||
                    (manifest.Version >= SchemaVersion && !pending.ExpectedToExist &&
                     (!string.IsNullOrEmpty(pending.ExpectedFingerprint) ||
                      !string.IsNullOrEmpty(pending.ReplacementBackupPath))) ||
                    !RewritingOutputMirror.IsWithin(pending.TargetPath, outputDirectory, comparer) ||
                    !pendingTargets.Add(pending.TargetPath))
                {
                    throw new IOException(
                        $"The rewrite recovery journal '{backupDirectory}' contains an invalid pending publication.");
                }
            }

            var directories = new HashSet<string>(comparer);
            foreach (string directory in manifest.CreatedDirectories)
            {
                if (string.IsNullOrEmpty(directory) ||
                    !RewritingOutputMirror.IsWithin(directory, outputDirectory, comparer) ||
                    !directories.Add(directory))
                {
                    throw new IOException(
                        $"The rewrite recovery journal '{backupDirectory}' contains an invalid directory change.");
                }
            }
        }

        private RewritingOutputChangeJournal(IFileSystem fileSystem, string outputDirectory,
            string backupDirectory, JournalManifest manifest)
        {
            this.FileSystem = fileSystem;
            this.OutputDirectory = outputDirectory;
            this.BackupDirectory = backupDirectory;
            this.Changes = manifest.Changes ?? new List<Change>();
            this.CreatedDirectories = manifest.CreatedDirectories ?? new List<string>();
            this.PendingPublications = manifest.PendingPublications ?? new List<PendingPublication>();
            this.CreatedUtcTicks = manifest.CreatedUtcTicks;
            this.Version = manifest.Version;
            this.State = manifest.State ?? ActiveState;
            var comparer = fileSystem.IsCaseInsensitive(outputDirectory) ?
                StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            this.CapturedPaths = new HashSet<string>(this.Changes.Select(change => change.TargetPath), comparer);
            this.CapturedDirectories = new HashSet<string>(this.CreatedDirectories, comparer);
        }

        private void SaveManifest()
        {
            this.SaveManifest(this.CreateManifest());
        }

        private JournalManifest CreateManifest(List<Change> changes = null) => new JournalManifest()
        {
            Version = this.Version,
            OutputDirectory = this.OutputDirectory,
            CreatedUtcTicks = this.CreatedUtcTicks,
            State = this.State,
            Changes = changes ?? new List<Change>(this.Changes),
            CreatedDirectories = new List<string>(this.CreatedDirectories),
            PendingPublications = new List<PendingPublication>(this.PendingPublications)
        };

        private void SaveManifest(JournalManifest manifest)
        {
            string manifestPath = Path.Combine(this.BackupDirectory, JournalFileName);
            string temporaryPath = manifestPath + ".tmp";
            string json = JsonSerializer.Serialize(
                manifest, new JsonSerializerOptions() { WriteIndented = true });
            this.FileSystem.WriteAllText(temporaryPath, json);
            if (this.FileSystem.FileExists(manifestPath))
            {
                this.FileSystem.ReplaceFile(temporaryPath, manifestPath, null);
            }
            else
            {
                this.FileSystem.MoveFile(temporaryPath, manifestPath);
            }
        }

        private bool ReconcileCaptureFailure(JournalManifest prior, JournalManifest proposed)
        {
            string manifestPath = Path.Combine(this.BackupDirectory, JournalFileName);
            JournalManifest durable = JsonSerializer.Deserialize<JournalManifest>(
                this.FileSystem.ReadAllText(manifestPath));
            if (durable is null || durable.Version != SchemaVersion ||
                !string.Equals(RewritingCacheValidator.NormalizeDirectory(durable.OutputDirectory),
                    this.OutputDirectory, this.FileSystem.IsCaseInsensitive(this.OutputDirectory) ?
                        StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new IOException("The durable rewrite journal manifest is invalid.");
            }

            ValidateManifest(this.FileSystem, this.OutputDirectory, this.BackupDirectory, durable);
            if (this.DescribesSameManifest(durable, proposed))
            {
                return true;
            }

            if (this.DescribesSameManifest(durable, prior))
            {
                return false;
            }

            throw new IOException("The durable rewrite journal manifest matches neither capture state.");
        }

        private bool DescribesSameManifest(JournalManifest left, JournalManifest right)
        {
            var comparer = new FileSystemPathComparer(this.FileSystem);
            return left.Version == right.Version &&
                left.CreatedUtcTicks == right.CreatedUtcTicks &&
                left.State == right.State &&
                comparer.Equals(left.OutputDirectory, right.OutputDirectory) &&
                left.Changes.Count == right.Changes.Count &&
                left.Changes.Zip(right.Changes, (first, second) =>
                    first.Existed == second.Existed &&
                    first.BackupLength == second.BackupLength &&
                    string.Equals(first.BackupFingerprint, second.BackupFingerprint,
                        StringComparison.Ordinal) &&
                    comparer.Equals(first.TargetPath, second.TargetPath) &&
                    ((!first.Existed && string.IsNullOrEmpty(first.BackupPath) &&
                        string.IsNullOrEmpty(second.BackupPath)) ||
                     (first.Existed && comparer.Equals(first.BackupPath, second.BackupPath))))
                .All(isSame => isSame) &&
                (left.PendingPublications ?? new List<PendingPublication>()).Count ==
                    (right.PendingPublications ?? new List<PendingPublication>()).Count &&
                (left.PendingPublications ?? new List<PendingPublication>()).Zip(
                    right.PendingPublications ?? new List<PendingPublication>(), (first, second) =>
                        first.ExpectedToExist == second.ExpectedToExist &&
                        first.ExpectedLength == second.ExpectedLength &&
                        first.StagedLength == second.StagedLength &&
                        comparer.Equals(first.TargetPath, second.TargetPath) &&
                        string.Equals(first.ExpectedFingerprint, second.ExpectedFingerprint,
                            StringComparison.Ordinal) &&
                        ((!first.ExpectedToExist && string.IsNullOrEmpty(first.ReplacementBackupPath) &&
                          string.IsNullOrEmpty(second.ReplacementBackupPath)) ||
                         (first.ExpectedToExist && comparer.Equals(
                             first.ReplacementBackupPath, second.ReplacementBackupPath))) &&
                        string.Equals(first.StagedFingerprint, second.StagedFingerprint,
                            StringComparison.Ordinal)).All(isSame => isSame) &&
                left.CreatedDirectories.Count == right.CreatedDirectories.Count &&
                left.CreatedDirectories.Zip(right.CreatedDirectories, comparer.Equals)
                    .All(isSame => isSame);
        }

        private void ApplyManifest(JournalManifest manifest)
        {
            this.Changes.Clear();
            this.Changes.AddRange(manifest.Changes);
            this.CreatedDirectories.Clear();
            this.CreatedDirectories.AddRange(manifest.CreatedDirectories);
            this.PendingPublications.Clear();
            this.PendingPublications.AddRange(
                manifest.PendingPublications ?? Enumerable.Empty<PendingPublication>());
            this.State = manifest.State;
            this.CapturedPaths.Clear();
            this.CapturedPaths.UnionWith(this.Changes.Select(change => change.TargetPath));
            this.CapturedDirectories.Clear();
            this.CapturedDirectories.UnionWith(this.CreatedDirectories);
        }

        private void DeleteDirectoryContents(string directory, string retainedManifestPath)
        {
            var comparer = new FileSystemPathComparer(this.FileSystem);
            foreach (string file in this.FileSystem.GetFiles(directory, "*"))
            {
                if (!comparer.Equals(file, retainedManifestPath))
                {
                    this.FileSystem.DeleteFile(file);
                }
            }

            foreach (string child in this.FileSystem.GetDirectories(directory, "*", false))
            {
                this.DeleteDirectoryContents(child, retainedManifestPath);
                if (this.FileSystem.DirectoryExists(child))
                {
                    this.FileSystem.DeleteDirectory(child, false);
                }
            }
        }
    }
}

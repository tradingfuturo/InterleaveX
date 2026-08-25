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
        private const int SchemaVersion = 6;

        private const int CapturedOriginalSchemaVersion = 6;

        private const int NoReplacePublicationSchemaVersion = 5;

        private const int CurrentPublicationSchemaVersion = 4;

        private const int FingerprintedPublicationSchemaVersion = 3;

        private const int PublicationSchemaVersion = 2;

        private const int LegacySchemaVersion = 1;

        private const string JournalFileName = "journal.json";

        private const string ActiveState = "active";

        private const string RestoringState = "restoring";

        private const string RestoredState = "restored";

        private const string CleanupState = "cleanup";

        private const string CapturedOriginalState = "captured";

        private const string RestoringOriginalState = "restoring";

        private const string RestoredOriginalState = "restored";

        internal sealed class Change
        {
            public string TargetPath { get; set; }

            public string BackupPath { get; set; }

            public bool Existed { get; set; }

            public long BackupLength { get; set; }

            public string BackupFingerprint { get; set; }

            public long CapturedOriginalLength { get; set; }

            public string CapturedOriginalFingerprint { get; set; }

            public string CapturedOriginalState { get; set; }
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

            public string StagedSourcePath { get; set; }

            public bool ExpectedToExist { get; set; }

            public long ExpectedLength { get; set; }

            public string ExpectedFingerprint { get; set; }

            public string ReplacementBackupPath { get; set; }

            public long StagedLength { get; set; }

            public string StagedFingerprint { get; set; }

            public MoveFileNoReplaceState MoveResult { get; set; } =
                MoveFileNoReplaceState.Unknown;

            public long CapturedOriginalLength { get; set; }

            public string CapturedOriginalFingerprint { get; set; }

            public string CapturedOriginalState { get; set; }
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
                StagedSourcePath = source,
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
                MoveFileNoReplaceResult moveResult = this.FileSystem.MoveFileNoReplace(source, target);
                pending.MoveResult = moveResult.State;
                try
                {
                    this.SaveManifest();
                }
                catch (Exception saveFailure)
                {
                    this.IsMutationStateAmbiguous = true;
                    throw new IOException(
                        "The rewrite journal cannot durably record a new output publication result.",
                        moveResult.Exception is null ? saveFailure :
                        new AggregateException(moveResult.Exception, saveFailure));
                }

                if (moveResult.State is not MoveFileNoReplaceState.Transferred)
                {
                    throw new IOException(
                        $"The source directory '{this.OutputDirectory}' changed after its rewrite snapshot was created.",
                        moveResult.Exception);
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

            this.CapturePendingOriginal(pending);
            if (pending.CapturedOriginalLength != baseline.Length ||
                !string.Equals(pending.CapturedOriginalFingerprint, baseline.Fingerprint,
                    StringComparison.Ordinal))
            {
                // A pre-replace race was captured atomically in the backup. Only put those actual
                // bytes back while the target still contains the bytes owned by this journal.
                this.RestoreCapturedPendingPublication(pending);
                this.RemovePendingPublication(pending);

                throw new IOException(
                    $"The source directory '{this.OutputDirectory}' changed after its rewrite snapshot was created.");
            }

            var publishedChange = new Change()
            {
                TargetPath = target,
                BackupPath = pending.ReplacementBackupPath,
                Existed = true,
                BackupLength = pending.CapturedOriginalLength,
                BackupFingerprint = pending.CapturedOriginalFingerprint,
                CapturedOriginalLength = pending.CapturedOriginalLength,
                CapturedOriginalFingerprint = pending.CapturedOriginalFingerprint,
                CapturedOriginalState = RewritingOutputChangeJournal.CapturedOriginalState
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

        /// <summary>
        /// Captures the actual pre-replace bytes that <see cref="IFileSystem.ReplaceFile"/> moved to
        /// the publication backup. They can differ from the earlier mirror baseline when another
        /// writer won immediately before the atomic replacement.
        /// </summary>
        private void CapturePendingOriginal(PendingPublication pending)
        {
            IFileEntry captured = this.FileSystem.GetFile(pending.ReplacementBackupPath);
            string fingerprint = captured.Exists ? RewritingCacheValidator.ComputeFileFingerprint(
                this.FileSystem, pending.ReplacementBackupPath) : null;
            if (!captured.Exists || string.IsNullOrEmpty(fingerprint))
            {
                throw new IOException(
                    $"Publication backup '{pending.ReplacementBackupPath}' is missing or corrupt.");
            }

            pending.CapturedOriginalLength = captured.Length;
            pending.CapturedOriginalFingerprint = fingerprint;
            pending.CapturedOriginalState = CapturedOriginalState;
            this.SaveManifest();
        }

        /// <summary>
        /// Restores a replacement backup only after its actual identity is durable, and records each
        /// consumption boundary so that a restart never has to guess whether the backup was moved.
        /// </summary>
        private void RestoreCapturedPendingPublication(PendingPublication pending)
        {
            if (pending.CapturedOriginalState is RestoredOriginalState)
            {
                if (!this.ContainsBytes(pending.TargetPath, pending.CapturedOriginalLength,
                    pending.CapturedOriginalFingerprint))
                {
                    throw new IOException(
                        $"Pending publication target '{pending.TargetPath}' no longer contains its captured original bytes.");
                }

                return;
            }

            bool restored = this.ContainsBytes(pending.TargetPath, pending.CapturedOriginalLength,
                pending.CapturedOriginalFingerprint);
            if (restored)
            {
                pending.CapturedOriginalState = RestoredOriginalState;
                this.SaveManifest();
                return;
            }

            if (!this.ContainsBytes(pending.TargetPath, pending.StagedLength, pending.StagedFingerprint))
            {
                throw new IOException(
                    $"Pending publication target '{pending.TargetPath}' no longer contains journal-owned bytes.");
            }

            if (!this.FileSystem.FileExists(pending.ReplacementBackupPath))
            {
                throw new IOException(
                    $"Pending publication backup for '{pending.TargetPath}' was consumed without a restored target.");
            }

            if (pending.CapturedOriginalState is not RestoringOriginalState)
            {
                pending.CapturedOriginalState = RestoringOriginalState;
                this.SaveManifest();
            }

            try
            {
                this.FileSystem.ReplaceFile(pending.ReplacementBackupPath, pending.TargetPath, null);
            }
            catch (Exception transferFailure)
            {
                bool transferRestored;
                try
                {
                    transferRestored = this.ContainsBytes(pending.TargetPath,
                        pending.CapturedOriginalLength, pending.CapturedOriginalFingerprint);
                }
                catch (Exception reconciliationFailure)
                {
                    throw new IOException(
                        $"Unable to determine whether recovery restored '{pending.TargetPath}'.",
                        new AggregateException(transferFailure, reconciliationFailure));
                }

                if (!transferRestored)
                {
                    throw;
                }
            }

            if (!this.ContainsBytes(pending.TargetPath, pending.CapturedOriginalLength,
                pending.CapturedOriginalFingerprint))
            {
                throw new IOException(
                    $"Pending publication target '{pending.TargetPath}' did not restore its captured original bytes.");
            }

            pending.CapturedOriginalState = RestoredOriginalState;
            this.SaveManifest();
        }

        /// <summary>
        /// Restores a captured existing target with an explicit durable consume state.
        /// </summary>
        private void RestoreCapturedChange(Change change)
        {
            if (change.CapturedOriginalState is RestoredOriginalState)
            {
                if (!this.ContainsBytes(change.TargetPath, change.CapturedOriginalLength,
                    change.CapturedOriginalFingerprint))
                {
                    throw new IOException(
                        $"Restored target '{change.TargetPath}' no longer contains its captured original bytes.");
                }

                return;
            }

            if (this.ContainsBytes(change.TargetPath, change.CapturedOriginalLength,
                change.CapturedOriginalFingerprint))
            {
                change.CapturedOriginalState = RestoredOriginalState;
                this.SaveManifest();
                return;
            }

            IFileEntry backup = this.FileSystem.GetFile(change.BackupPath);
            if (!backup.Exists || backup.Length != change.CapturedOriginalLength ||
                !string.Equals(RewritingCacheValidator.ComputeFileFingerprint(
                    this.FileSystem, change.BackupPath), change.CapturedOriginalFingerprint,
                    StringComparison.Ordinal))
            {
                throw new IOException(
                    $"Recovery backup '{change.BackupPath}' is missing or corrupt.");
            }

            if (change.CapturedOriginalState is not RestoringOriginalState)
            {
                change.CapturedOriginalState = RestoringOriginalState;
                this.SaveManifest();
            }

            this.FileSystem.CreateDirectory(Path.GetDirectoryName(change.TargetPath));
            Exception transferFailure = null;
            try
            {
                if (this.FileSystem.FileExists(change.TargetPath))
                {
                    this.FileSystem.ReplaceFile(change.BackupPath, change.TargetPath, null);
                }
                else
                {
                    this.FileSystem.MoveFile(change.BackupPath, change.TargetPath);
                }
            }
            catch (Exception ex)
            {
                transferFailure = ex;
            }

            if (!this.ContainsBytes(change.TargetPath, change.CapturedOriginalLength,
                change.CapturedOriginalFingerprint))
            {
                if (transferFailure != null)
                {
                    throw transferFailure;
                }

                throw new IOException(
                    $"Restored target '{change.TargetPath}' does not contain its captured original bytes.");
            }

            change.CapturedOriginalState = RestoredOriginalState;
            this.SaveManifest();
            if (transferFailure != null)
            {
                throw transferFailure;
            }
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
                        change.CapturedOriginalLength = change.BackupLength;
                        change.CapturedOriginalFingerprint = change.BackupFingerprint;
                        change.CapturedOriginalState = CapturedOriginalState;
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
                        change.CapturedOriginalLength = change.BackupLength;
                        change.CapturedOriginalFingerprint = change.BackupFingerprint;
                        change.CapturedOriginalState = CapturedOriginalState;
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

        internal void Restore(bool removeCreatedDirectories = true)
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
                        if (this.Version < NoReplacePublicationSchemaVersion)
                        {
                            throw new IOException(
                                $"Legacy pending publication '{pending.TargetPath}' has no durable transfer outcome.");
                        }

                        if (pending.MoveResult is MoveFileNoReplaceState.Unknown)
                        {
                            throw new IOException(
                                $"Pending publication '{pending.TargetPath}' has an unknown no-replace move outcome.");
                        }

                        if (pending.MoveResult is MoveFileNoReplaceState.NotTransferred ||
                            !this.FileSystem.FileExists(pending.TargetPath))
                        {
                            // The file system proved that this move did not transfer the staged
                            // source. Do not infer journal ownership from destination bytes: an
                            // external writer can legitimately create the same bytes.
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

                    if (this.Version < CapturedOriginalSchemaVersion)
                    {
                        throw new IOException(
                            $"Legacy pending publication '{pending.TargetPath}' has no durable captured-original identity.");
                    }

                    if (string.IsNullOrEmpty(pending.CapturedOriginalState))
                    {
                        this.CapturePendingOriginal(pending);
                    }

                    this.RestoreCapturedPendingPublication(pending);
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
                        if (!pending.ExpectedToExist && this.Version >= NoReplacePublicationSchemaVersion &&
                            pending.MoveResult is not MoveFileNoReplaceState.Transferred)
                        {
                            throw new IOException(
                                $"Published target '{change.TargetPath}' has no transferred no-replace move result.");
                        }

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
                            if (this.Version >= CapturedOriginalSchemaVersion && change.Existed &&
                                change.CapturedOriginalState is not RestoredOriginalState)
                            {
                                change.CapturedOriginalState = RestoredOriginalState;
                                this.SaveManifest();
                            }

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
                        if (this.Version >= CapturedOriginalSchemaVersion)
                        {
                            this.RestoreCapturedChange(change);
                            continue;
                        }

                        bool exists = this.FileSystem.FileExists(change.TargetPath);

                        // A previous restore can have completed the atomic transfer before the
                        // filesystem reported its failure. Treat the recorded original bytes as
                        // the durable success condition, even when the backup has been consumed.
                        if (exists && this.ContainsBytes(
                            change.TargetPath, change.BackupLength, change.BackupFingerprint))
                        {
                            continue;
                        }

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
                        try
                        {
                            if (exists)
                            {
                                this.FileSystem.ReplaceFile(change.BackupPath, change.TargetPath, null);
                            }
                            else
                            {
                                this.FileSystem.MoveFile(change.BackupPath, change.TargetPath);
                            }
                        }
                        catch (Exception transferFailure)
                        {
                            bool restored;
                            try
                            {
                                restored = this.ContainsBytes(
                                    change.TargetPath, change.BackupLength, change.BackupFingerprint);
                            }
                            catch (Exception reconciliationFailure)
                            {
                                throw new IOException(
                                    $"Unable to determine whether recovery restored '{change.TargetPath}'.",
                                    new AggregateException(transferFailure, reconciliationFailure));
                            }

                            if (!restored)
                            {
                                throw;
                            }

                            // Keep the journal in restoring state for a retry even though the
                            // target now proves that the transfer took effect.
                            throw;
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

            if (!removeCreatedDirectories)
            {
                if (failure != null)
                {
                    throw new IOException("Unable to restore one or more mirrored output files.", failure);
                }

                // A replacement rewrite keeps its staged sources in a directory the journal created.
                // The caller must not discard that evidence until every publication outcome has been
                // reconciled, then calls Restore again after staged cleanup to remove the directory.
                return;
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

                if (manifest is null || !IsSupportedVersion(manifest.Version) ||
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
                if (manifest is null || !IsSupportedVersion(manifest.Version) ||
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
                     (manifest.Version >= CapturedOriginalSchemaVersion && change.Existed &&
                      (change.CapturedOriginalLength != change.BackupLength ||
                       !string.Equals(change.CapturedOriginalFingerprint, change.BackupFingerprint,
                           StringComparison.Ordinal) ||
                       (change.CapturedOriginalState != CapturedOriginalState &&
                        change.CapturedOriginalState != RestoringOriginalState &&
                        change.CapturedOriginalState != RestoredOriginalState))) ||
                     (manifest.Version >= CapturedOriginalSchemaVersion && !change.Existed &&
                      (change.CapturedOriginalLength != 0 ||
                       !string.IsNullOrEmpty(change.CapturedOriginalFingerprint) ||
                       !string.IsNullOrEmpty(change.CapturedOriginalState))) ||
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
                    (manifest.Version >= CurrentPublicationSchemaVersion && pending.ExpectedToExist &&
                      (pending.ExpectedLength < 0 || string.IsNullOrEmpty(pending.ExpectedFingerprint) ||
                       string.IsNullOrEmpty(pending.ReplacementBackupPath) ||
                       !RewritingOutputMirror.IsWithin(
                           pending.ReplacementBackupPath, backupDirectory, comparer))) ||
                    (manifest.Version >= CurrentPublicationSchemaVersion && !pending.ExpectedToExist &&
                      (!string.IsNullOrEmpty(pending.ExpectedFingerprint) ||
                       !string.IsNullOrEmpty(pending.ReplacementBackupPath))) ||
                    (manifest.Version >= NoReplacePublicationSchemaVersion &&
                     (string.IsNullOrEmpty(pending.StagedSourcePath) || pending.StagedLength < 0 ||
                      !RewritingOutputMirror.IsWithin(
                          pending.StagedSourcePath, outputDirectory, comparer) ||
                      !Enum.IsDefined(typeof(MoveFileNoReplaceState), pending.MoveResult))) ||
                    (manifest.Version >= CapturedOriginalSchemaVersion && pending.ExpectedToExist &&
                     ((string.IsNullOrEmpty(pending.CapturedOriginalState) &&
                       (pending.CapturedOriginalLength != 0 ||
                        !string.IsNullOrEmpty(pending.CapturedOriginalFingerprint))) ||
                      (!string.IsNullOrEmpty(pending.CapturedOriginalState) &&
                       ((pending.CapturedOriginalState != CapturedOriginalState &&
                         pending.CapturedOriginalState != RestoringOriginalState &&
                         pending.CapturedOriginalState != RestoredOriginalState) ||
                        pending.CapturedOriginalLength < 0 ||
                        string.IsNullOrEmpty(pending.CapturedOriginalFingerprint))))) ||
                    (manifest.Version >= CapturedOriginalSchemaVersion && !pending.ExpectedToExist &&
                     (!string.IsNullOrEmpty(pending.CapturedOriginalState) ||
                      pending.CapturedOriginalLength != 0 ||
                      !string.IsNullOrEmpty(pending.CapturedOriginalFingerprint))) ||
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

        private static bool IsSupportedVersion(int version) => version == SchemaVersion ||
            version == CapturedOriginalSchemaVersion - 1 ||
            version == CurrentPublicationSchemaVersion ||
            version == FingerprintedPublicationSchemaVersion ||
            version == PublicationSchemaVersion || version == LegacySchemaVersion;

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
                    first.CapturedOriginalLength == second.CapturedOriginalLength &&
                    string.Equals(first.BackupFingerprint, second.BackupFingerprint,
                        StringComparison.Ordinal) &&
                    string.Equals(first.CapturedOriginalFingerprint,
                        second.CapturedOriginalFingerprint, StringComparison.Ordinal) &&
                    string.Equals(first.CapturedOriginalState, second.CapturedOriginalState,
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
                        first.MoveResult == second.MoveResult &&
                        first.CapturedOriginalLength == second.CapturedOriginalLength &&
                        comparer.Equals(first.TargetPath, second.TargetPath) &&
                        comparer.Equals(first.StagedSourcePath, second.StagedSourcePath) &&
                        string.Equals(first.ExpectedFingerprint, second.ExpectedFingerprint,
                            StringComparison.Ordinal) &&
                        string.Equals(first.CapturedOriginalFingerprint,
                            second.CapturedOriginalFingerprint, StringComparison.Ordinal) &&
                        string.Equals(first.CapturedOriginalState, second.CapturedOriginalState,
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

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
        private const int SchemaVersion = 1;

        private const string JournalFileName = "journal.json";

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
        }

        private readonly IFileSystem FileSystem;
        private readonly string OutputDirectory;
        private readonly List<Change> Changes;
        private readonly HashSet<string> CapturedPaths;

        private readonly List<string> CreatedDirectories;

        private readonly HashSet<string> CapturedDirectories;

        private long CreatedUtcTicks;

        private string State;

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
            this.CreatedUtcTicks = DateTime.UtcNow.Ticks;
            this.State = "active";
            this.FileSystem.CreateDirectory(this.BackupDirectory);
            this.SaveManifest();
        }

        internal string BackupDirectory { get; }

        internal void Capture(string targetPath)
        {
            this.EnsureActiveForMutation();
            string normalized = RewritingCacheValidator.NormalizeFile(targetPath);
            if (!this.CapturedPaths.Add(normalized))
            {
                return;
            }

            bool existed = this.FileSystem.FileExists(normalized);
            var change = new Change()
            {
                TargetPath = normalized,
                Existed = existed
            };

            try
            {
                if (existed)
                {
                    string relative = normalized.Substring(this.OutputDirectory.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    change.BackupPath = Path.Combine(this.BackupDirectory, relative);
                    this.FileSystem.CreateDirectory(Path.GetDirectoryName(change.BackupPath));
                    this.FileSystem.CopyFile(normalized, change.BackupPath, false);
                    change.BackupLength = this.FileSystem.GetFile(change.BackupPath).Length;
                    change.BackupFingerprint = RewritingCacheValidator.ComputeFileFingerprint(
                        this.FileSystem, change.BackupPath);
                }

                this.FileSystem.CreateDirectory(this.BackupDirectory);
                this.Changes.Add(change);
                this.SaveManifest();
            }
            catch
            {
                this.CapturedPaths.Remove(normalized);
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
            if (this.State is "active")
            {
                return;
            }

            if (this.State is not "restored")
            {
                throw new InvalidOperationException(
                    $"Cannot capture output changes while the rewrite journal is '{this.State}'.");
            }

            string previousState = this.State;
            this.State = "active";
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

        internal void Restore()
        {
            this.State = "restoring";
            this.SaveManifest();
            Exception failure = null;
            foreach (Change change in Enumerable.Reverse(this.Changes))
            {
                try
                {
                    if (change.Existed)
                    {
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
                        this.FileSystem.CopyFile(change.BackupPath, change.TargetPath, true);
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

            this.State = "restored";
            this.SaveManifest();
        }

        internal void Complete()
        {
            if (this.FileSystem.DirectoryExists(this.BackupDirectory))
            {
                this.FileSystem.DeleteDirectory(this.BackupDirectory, true);
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

                if (manifest is null || manifest.Version != SchemaVersion ||
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

            // A later transaction backed up the output left by an earlier one. Undo the newest
            // transaction first so each older backup is restored onto the state it captured.
            foreach (RewritingOutputChangeJournal journal in journals
                .OrderByDescending(item => item.CreatedUtcTicks)
                .ThenByDescending(item => item.BackupDirectory, StringComparer.Ordinal))
            {
                if (journal.State != "restored")
                {
                    journal.Restore();
                }

                journal.Complete();
            }
        }

        private static void ValidateManifest(IFileSystem fileSystem, string outputDirectory,
            string backupDirectory, JournalManifest manifest)
        {
            var comparer = new FileSystemPathComparer(fileSystem);
            if (manifest.CreatedUtcTicks <= 0 ||
                (manifest.State != "active" && manifest.State != "restoring" &&
                 manifest.State != "restored") || manifest.Changes is null ||
                manifest.CreatedDirectories is null)
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
            this.CreatedUtcTicks = manifest.CreatedUtcTicks;
            this.State = manifest.State ?? "active";
            var comparer = fileSystem.IsCaseInsensitive(outputDirectory) ?
                StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            this.CapturedPaths = new HashSet<string>(this.Changes.Select(change => change.TargetPath), comparer);
            this.CapturedDirectories = new HashSet<string>(this.CreatedDirectories, comparer);
        }

        private void SaveManifest()
        {
            string manifestPath = Path.Combine(this.BackupDirectory, JournalFileName);
            string temporaryPath = manifestPath + ".tmp";
            string json = JsonSerializer.Serialize(new JournalManifest()
            {
                Version = SchemaVersion,
                OutputDirectory = this.OutputDirectory,
                CreatedUtcTicks = this.CreatedUtcTicks,
                State = this.State,
                Changes = this.Changes,
                CreatedDirectories = this.CreatedDirectories
            }, new JsonSerializerOptions() { WriteIndented = true });
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
    }
}

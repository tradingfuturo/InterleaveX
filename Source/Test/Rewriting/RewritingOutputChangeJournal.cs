// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Coyote.IO;

namespace Microsoft.Coyote.Rewriting
{
    /// <summary>
    /// Records only output files changed by a mirror attempt and can restore them in reverse order.
    /// </summary>
    internal sealed class RewritingOutputChangeJournal
    {
        private sealed class Change
        {
            internal string TargetPath { get; set; }

            internal string BackupPath { get; set; }

            internal bool Existed { get; set; }
        }

        private readonly IFileSystem FileSystem;
        private readonly string OutputDirectory;
        private readonly List<Change> Changes;
        private readonly HashSet<string> CapturedPaths;

        internal RewritingOutputChangeJournal(IFileSystem fileSystem, string outputDirectory)
        {
            this.FileSystem = fileSystem;
            this.OutputDirectory = RewritingCacheValidator.NormalizeDirectory(outputDirectory);
            this.BackupDirectory = this.OutputDirectory + ".mirror-backup-" + Guid.NewGuid().ToString("N");
            this.Changes = new List<Change>();
            this.CapturedPaths = new HashSet<string>(fileSystem.IsCaseInsensitive(outputDirectory) ?
                StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        }

        internal string BackupDirectory { get; }

        internal void Capture(string targetPath)
        {
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
                }

                this.FileSystem.CreateDirectory(this.BackupDirectory);
                this.Changes.Add(change);
            }
            catch
            {
                this.CapturedPaths.Remove(normalized);
                throw;
            }
        }

        internal void Restore()
        {
            Exception failure = null;
            foreach (Change change in Enumerable.Reverse(this.Changes))
            {
                try
                {
                    if (change.Existed)
                    {
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

            if (failure != null)
            {
                throw new IOException("Unable to restore one or more mirrored output files.", failure);
            }
        }

        internal void Complete()
        {
            if (this.FileSystem.DirectoryExists(this.BackupDirectory))
            {
                this.FileSystem.DeleteDirectory(this.BackupDirectory, true);
            }
        }
    }
}

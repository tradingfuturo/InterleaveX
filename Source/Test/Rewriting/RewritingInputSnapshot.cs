// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Coyote.IO;
using Microsoft.Coyote.Logging;

namespace Microsoft.Coyote.Rewriting
{
    /// <summary>
    /// A verified, immutable-for-the-run copy of the assembly input tree.
    /// </summary>
    internal sealed class RewritingInputSnapshot : IDisposable
    {
        internal const string DirectoryMarker = ".rewrite-snapshot-";

        private readonly IFileSystem FileSystem;
        private readonly RewritingOutputMirror Mirror;
        private readonly FileSystemPathComparer PathComparer;
        private IReadOnlyDictionary<string, MirroredFile> BaselineFiles;
        private IReadOnlyCollection<string> ExcludedDirectories;
        private IReadOnlyCollection<string> ExcludedFiles;
        private bool IsDisposed;

        private RewritingInputSnapshot(IFileSystem fileSystem, string sourceDirectory,
            string snapshotDirectory, LogWriter logWriter)
        {
            this.FileSystem = fileSystem;
            this.SourceDirectory = RewritingCacheValidator.NormalizeDirectory(sourceDirectory);
            this.SnapshotDirectory = RewritingCacheValidator.NormalizeDirectory(snapshotDirectory);
            this.Mirror = new RewritingOutputMirror(fileSystem, logWriter);
            this.PathComparer = new FileSystemPathComparer(fileSystem);
        }

        internal string SourceDirectory { get; }

        internal string SnapshotDirectory { get; }

        internal static RewritingInputSnapshot Create(IFileSystem fileSystem, LogWriter logWriter,
            string sourceDirectory, string outputDirectory, IEnumerable<string> excludedDirectories = null,
            IEnumerable<string> excludedFiles = null)
        {
            string normalizedOutput = RewritingCacheValidator.NormalizeDirectory(outputDirectory);
            string snapshotDirectory = normalizedOutput + DirectoryMarker + Guid.NewGuid().ToString("N");
            var snapshot = new RewritingInputSnapshot(
                fileSystem, sourceDirectory, snapshotDirectory, logWriter);
            var mirror = snapshot.Mirror;
            var pathComparer = new FileSystemPathComparer(fileSystem);
            var excluded = new HashSet<string>(excludedDirectories ?? Enumerable.Empty<string>(),
                pathComparer) { snapshotDirectory };
            string normalizedSource = RewritingCacheValidator.NormalizeDirectory(sourceDirectory);
            if (!pathComparer.Equals(normalizedSource, normalizedOutput))
            {
                excluded.Add(normalizedOutput);
            }

            try
            {
                string parent = Path.GetDirectoryName(normalizedOutput);
                string pattern = Path.GetFileName(normalizedOutput) + DirectoryMarker + "*";
                if (fileSystem.DirectoryExists(parent))
                {
                    foreach (string stale in fileSystem.GetDirectories(parent, pattern, false))
                    {
                        fileSystem.DeleteDirectory(stale, true);
                    }
                }

                Exception lastError = null;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    if (fileSystem.DirectoryExists(snapshotDirectory))
                    {
                        fileSystem.DeleteDirectory(snapshotDirectory, true);
                    }

                    fileSystem.CreateDirectory(snapshotDirectory);
                    try
                    {
                        var before = mirror.GetMirroredFiles(sourceDirectory, snapshotDirectory,
                            includeFingerprints: false, excludedDirectories: excluded,
                            excludedFiles: excludedFiles);
                        mirror.Mirror(sourceDirectory, snapshotDirectory, new HashSet<string>(), before.Keys);
                        var after = mirror.GetMirroredFiles(sourceDirectory, snapshotDirectory,
                            includeFingerprints: false, excludedDirectories: excluded,
                            excludedFiles: excludedFiles);
                        if (mirror.DescribeSameFiles(sourceDirectory, snapshotDirectory, before, after))
                        {
                            snapshot.BaselineFiles = mirror.GetMirroredFiles(
                                snapshotDirectory, sourceDirectory, includeFingerprints: true);
                            snapshot.ExcludedDirectories = excluded.ToArray();
                            snapshot.ExcludedFiles = (excludedFiles ?? Enumerable.Empty<string>()).ToArray();
                            return snapshot;
                        }

                        lastError = new IOException(
                            $"The source directory '{sourceDirectory}' changed while its rewrite snapshot was created.");
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        lastError = ex;
                    }
                }

                throw new IOException(
                    $"Unable to create a stable rewrite snapshot of '{sourceDirectory}'.", lastError);
            }
            catch
            {
                snapshot.Dispose();
                throw;
            }
        }

        internal string ToReadPath(string logicalPath) =>
            this.Translate(logicalPath, this.SourceDirectory, this.SnapshotDirectory,
                allowFileSystemComparison: true);

        internal string ToLogicalPath(string readPath) =>
            this.Translate(readPath, this.SnapshotDirectory, this.SourceDirectory,
                allowFileSystemComparison: false);

        /// <summary>
        /// Verifies that the source tree still has exactly the names and bytes captured by this snapshot.
        /// </summary>
        internal void VerifyUnchanged()
        {
            var current = this.Mirror.GetMirroredFiles(
                this.SourceDirectory, this.SnapshotDirectory, includeFingerprints: true,
                excludedDirectories: this.ExcludedDirectories, excludedFiles: this.ExcludedFiles);
            if (!RewritingOutputMirror.DescribeSameFiles(this.BaselineFiles, current))
            {
                throw new IOException(
                    $"The source directory '{this.SourceDirectory}' changed after its rewrite snapshot was created.");
            }
        }

        private string Translate(string path, string fromDirectory, string toDirectory,
            bool allowFileSystemComparison)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            // Resolver candidates are formed from already-normalized absolute search directories.
            // Avoid normalizing every one: Cecil can ask about the same reference from thousands of
            // instructions, and Path.GetFullPath dominated large solution rewrites here.
            string normalized = Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
            string trimmed = fromDirectory.TrimEnd('\\', '/');
            bool isWithin = RewritingOutputMirror.IsWithin(
                normalized, trimmed, StringComparison.Ordinal);
            if (!isWithin && allowFileSystemComparison)
            {
                isWithin = RewritingOutputMirror.IsWithin(normalized, trimmed, this.PathComparer);
            }

            if (!isWithin)
            {
                return path;
            }

            string relative = normalized.Substring(trimmed.Length)
                .TrimStart('\\', '/');
            return Path.Combine(toDirectory, relative);
        }

        public void Dispose()
        {
            if (!this.IsDisposed)
            {
                this.IsDisposed = true;
                if (this.FileSystem.DirectoryExists(this.SnapshotDirectory))
                {
                    this.FileSystem.DeleteDirectory(this.SnapshotDirectory, true);
                }
            }
        }
    }
}

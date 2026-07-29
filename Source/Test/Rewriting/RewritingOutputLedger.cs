// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Coyote.IO;
using Microsoft.Coyote.Logging;

namespace Microsoft.Coyote.Rewriting
{
    internal sealed class OutputOwnershipManifest
    {
        public int SchemaVersion { get; set; }

        public string AssembliesDirectory { get; set; }

        public string OutputDirectory { get; set; }

        public List<string> MirroredFiles { get; set; }

        public List<string> ProducedFiles { get; set; }
    }

    /// <summary>
    /// Persists the output paths the rewriter may safely remove on a later run.
    /// </summary>
    internal sealed class RewritingOutputLedger
    {
        internal const string ManifestFileName = "rewriting.outputs.json";
        private const int CurrentSchemaVersion = 1;

        private readonly IFileSystem FileSystem;
        private readonly LogWriter LogWriter;
        private readonly string AssembliesDirectory;
        private readonly string OutputDirectory;
        private readonly string ManifestPath;
        private readonly StringComparer PathComparer;
        private readonly StringComparison PathComparison;
        private readonly HashSet<string> PreviousMirroredFiles;
        private readonly HashSet<string> PreviousProducedFiles;

        internal RewritingOutputLedger(IFileSystem fileSystem, LogWriter logWriter,
            string assembliesDirectory, string outputDirectory)
        {
            this.FileSystem = fileSystem;
            this.LogWriter = logWriter;
            this.AssembliesDirectory = RewritingCacheValidator.NormalizeDirectory(assembliesDirectory);
            this.OutputDirectory = RewritingCacheValidator.NormalizeDirectory(outputDirectory);
            this.ManifestPath = Path.Combine(this.OutputDirectory, ManifestFileName);
            bool isCaseInsensitive = fileSystem.IsCaseInsensitive(outputDirectory);
            this.PathComparer = isCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            this.PathComparison = isCaseInsensitive ?
                StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            this.PreviousMirroredFiles = new HashSet<string>(this.PathComparer);
            this.PreviousProducedFiles = new HashSet<string>(this.PathComparer);

            this.TryLoadCurrentManifest();
        }

        internal void RemoveStaleMirroredFiles(IEnumerable<string> currentMirroredFiles)
        {
            var current = new HashSet<string>(currentMirroredFiles, this.PathComparer);
            this.DeleteOwnedFiles(this.PreviousMirroredFiles.Where(path => !current.Contains(path)));
        }

        internal void Commit(IEnumerable<string> mirroredFiles, IEnumerable<string> producedFiles)
        {
            var mirrored = this.NormalizeCurrentPaths(mirroredFiles);
            var produced = this.NormalizeCurrentPaths(producedFiles);
            this.DeleteOwnedFiles(this.PreviousProducedFiles.Where(path =>
                !produced.Contains(path) && !mirrored.Contains(path)));

            var manifest = new OutputOwnershipManifest()
            {
                SchemaVersion = CurrentSchemaVersion,
                AssembliesDirectory = this.AssembliesDirectory,
                OutputDirectory = this.OutputDirectory,
                MirroredFiles = mirrored.OrderBy(path => path, StringComparer.Ordinal).ToList(),
                ProducedFiles = produced.OrderBy(path => path, StringComparer.Ordinal).ToList()
            };

            string json = JsonSerializer.Serialize(manifest,
                new JsonSerializerOptions() { WriteIndented = true });
            string tempPath = string.Format(CultureInfo.InvariantCulture, "{0}.{1}.tmp",
                this.ManifestPath, Guid.NewGuid().ToString("N"));
            try
            {
                this.FileSystem.WriteAllText(tempPath, json);
                if (this.FileSystem.FileExists(this.ManifestPath))
                {
                    this.FileSystem.ReplaceFile(tempPath, this.ManifestPath, null);
                }
                else
                {
                    this.FileSystem.MoveFile(tempPath, this.ManifestPath);
                }

                tempPath = null;
            }
            finally
            {
                if (tempPath != null)
                {
                    try
                    {
                        this.FileSystem.DeleteFile(tempPath);
                    }
                    catch (Exception)
                    {
                        // Preserve the exception that made the commit fail.
                    }
                }
            }
        }

        internal bool TryGetRelativeOutputPath(string path, out string relativePath) =>
            TryGetRelativePath(this.OutputDirectory, path, this.PathComparison, out relativePath);

        private HashSet<string> NormalizeCurrentPaths(IEnumerable<string> paths)
        {
            var normalized = new HashSet<string>(this.PathComparer);
            foreach (string path in paths ?? Enumerable.Empty<string>())
            {
                if (!TryNormalizeRelativePath(path, out string relative) || !normalized.Add(relative))
                {
                    throw new InvalidDataException($"The output ownership path '{path}' is invalid or duplicated.");
                }
            }

            return normalized;
        }

        private bool TryLoadCurrentManifest()
        {
            if (!this.FileSystem.FileExists(this.ManifestPath))
            {
                return false;
            }

            try
            {
                var manifest = JsonSerializer.Deserialize<OutputOwnershipManifest>(
                    this.FileSystem.ReadAllText(this.ManifestPath));
                if (manifest?.SchemaVersion != CurrentSchemaVersion ||
                    !this.PathComparer.Equals(
                        RewritingCacheValidator.NormalizeDirectory(manifest.AssembliesDirectory),
                        this.AssembliesDirectory) ||
                    !this.PathComparer.Equals(
                        RewritingCacheValidator.NormalizeDirectory(manifest.OutputDirectory),
                        this.OutputDirectory) ||
                    !TryLoadPaths(manifest.MirroredFiles, this.PreviousMirroredFiles) ||
                    !TryLoadPaths(manifest.ProducedFiles, this.PreviousProducedFiles))
                {
                    this.PreviousMirroredFiles.Clear();
                    this.PreviousProducedFiles.Clear();
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                this.LogWriter.LogDebug("..... Ignoring an unusable output ownership ledger: {0}", ex.Message);
                this.PreviousMirroredFiles.Clear();
                this.PreviousProducedFiles.Clear();
                return false;
            }
        }

        private static bool TryLoadPaths(IEnumerable<string> paths, HashSet<string> destination)
        {
            if (paths is null)
            {
                return false;
            }

            foreach (string path in paths)
            {
                if (!TryNormalizeRelativePath(path, out string normalized) ||
                    !destination.Add(normalized))
                {
                    return false;
                }
            }

            return true;
        }

        private void DeleteOwnedFiles(IEnumerable<string> relativePaths)
        {
            var directories = new HashSet<string>(this.PathComparer);
            foreach (string relativePath in relativePaths.ToArray())
            {
                string fullPath = this.ResolveOwnedPath(relativePath);
                this.LogWriter.LogDebug("..... Removing the stale owned output '{0}'", fullPath);
                this.FileSystem.DeleteFile(fullPath);
                for (string directory = Path.GetDirectoryName(fullPath);
                    !string.IsNullOrEmpty(directory) &&
                    !this.PathComparer.Equals(directory, this.OutputDirectory);
                    directory = Path.GetDirectoryName(directory))
                {
                    directories.Add(directory);
                }
            }

            foreach (string directory in directories.OrderByDescending(path => path.Length))
            {
                try
                {
                    this.FileSystem.DeleteDirectory(directory, false);
                }
                catch (IOException)
                {
                    // It contains an unowned path or is still used by the current run.
                }
            }
        }

        private string ResolveOwnedPath(string relativePath)
        {
            if (!TryNormalizeRelativePath(relativePath, out string normalized))
            {
                throw new InvalidDataException($"The output ownership path '{relativePath}' is invalid.");
            }

            string fullPath = Path.GetFullPath(Path.Combine(
                this.OutputDirectory,
                normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!TryGetRelativePath(this.OutputDirectory, fullPath, this.PathComparison, out _))
            {
                throw new InvalidDataException($"The output ownership path '{relativePath}' escapes the output.");
            }

            return fullPath;
        }

        private static bool TryNormalizeRelativePath(string path, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            {
                return false;
            }

            string[] parts = path.Replace('\\', '/').Split('/');
            if (parts.Any(part => string.IsNullOrEmpty(part) || part == "." || part == ".."))
            {
                return false;
            }

            normalized = string.Join("/", parts);
            return normalized != RewritingCache.ManifestFileName && normalized != ManifestFileName;
        }

        private static bool TryGetRelativePath(string directory, string path,
            StringComparison comparison, out string relativePath)
        {
            relativePath = null;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            string root = RewritingCacheValidator.NormalizeDirectory(directory);
            string full = RewritingCacheValidator.NormalizeFile(path);
            if (full.Length <= root.Length || !full.StartsWith(root, comparison) ||
                (full[root.Length] != Path.DirectorySeparatorChar &&
                    full[root.Length] != Path.AltDirectorySeparatorChar))
            {
                return false;
            }

            string relative = full.Substring(root.Length + 1).Replace('\\', '/');
            return TryNormalizeRelativePath(relative, out relativePath);
        }
    }
}

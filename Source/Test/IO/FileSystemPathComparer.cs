// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.IO;

namespace Microsoft.Coyote.IO
{
    /// <summary>
    /// Compares paths the way the file system holding each of them does.
    /// </summary>
    /// <remarks>
    /// One comparer chosen from one directory describes a run whose files all sit on the same volume,
    /// and quietly misdescribes any other. The direction that matters is the unsafe one: a
    /// case-insensitive comparer applied to a case-sensitive dependency tree folds two genuinely
    /// different directories into one, and the rewriting cache then fingerprints one of them and
    /// believes it has covered both. The opposite mistake only costs a second fingerprint of the same
    /// file.
    ///
    /// Decided per containing directory rather than per volume root, because a root is not a volume:
    /// <see cref="Path.GetPathRoot(string)"/> answers "/" for every path on Unix, so a comparer keyed
    /// on it would have exactly one bucket on the platform where the mixed case actually occurs.
    ///
    /// The answers are cached here as well as inside the file system, because this is asked once per
    /// comparison and once per hash, over sets holding every assembly a run resolved.
    /// </remarks>
    internal sealed class FileSystemPathComparer : IEqualityComparer<string>
    {
        private readonly IFileSystem FileSystem;

        /// <summary>
        /// Whether each directory already asked about ignores case.
        /// </summary>
        /// <remarks>
        /// Keyed ordinally on purpose. Two spellings of one directory are two entries holding the
        /// same answer, which costs a probe; using a comparer here to avoid that would mean choosing
        /// the very thing this class exists to decide.
        /// </remarks>
        private readonly Dictionary<string, bool> ProbedDirectories =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        internal FileSystemPathComparer(IFileSystem fileSystem)
        {
            this.FileSystem = fileSystem;
        }

        /// <inheritdoc/>
        public bool Equals(string x, string y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || y is null)
            {
                return false;
            }

            return string.Equals(this.Canonicalize(x), this.Canonicalize(y), StringComparison.Ordinal);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Two paths that <see cref="Equals"/> calls equal must hash alike, so a path in a directory
        /// that ignores case is hashed by its upper-cased form and every other path ordinally. Paths
        /// in different directories may then collide or not; either is correct, because equality
        /// decides the answer and a collision only costs a comparison.
        /// </remarks>
        public int GetHashCode(string obj) => obj is null ? 0 :
            StringComparer.Ordinal.GetHashCode(this.Canonicalize(obj));

        /// <summary>
        /// Canonicalizes each path segment using the case rule of the directory that owns that
        /// segment. A single rule for the final directory folds distinct ancestors when a sensitive
        /// directory contains insensitive child mounts.
        /// </summary>
        private string Canonicalize(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string root = Path.GetPathRoot(fullPath) ?? string.Empty;
            string remainder = fullPath.Substring(root.Length);
            var builder = new System.Text.StringBuilder(root);
            string currentDirectory = root;
            char[] separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
            foreach (string segment in remainder.Split(separators,
                StringSplitOptions.RemoveEmptyEntries))
            {
                bool ignoresCase = this.DirectoryIgnoresCase(currentDirectory);
                builder.Append(ignoresCase ? segment.ToUpperInvariant() : segment);
                builder.Append(Path.DirectorySeparatorChar);
                currentDirectory = Path.Combine(currentDirectory, segment);
            }

            if (builder.Length > 0 && builder[builder.Length - 1] == Path.DirectorySeparatorChar &&
                !string.Equals(currentDirectory, root, StringComparison.Ordinal))
            {
                builder.Length--;
            }

            return builder.ToString();
        }

        private bool DirectoryIgnoresCase(string directory)
        {
            if (string.IsNullOrEmpty(directory))
            {
                return false;
            }

            if (!this.ProbedDirectories.TryGetValue(directory, out bool ignoresCase))
            {
                try
                {
                    ignoresCase = this.FileSystem.IsCaseInsensitive(directory);
                }
                catch (Exception)
                {
                    ignoresCase = false;
                }

                this.ProbedDirectories.Add(directory, ignoresCase);
            }

            return ignoresCase;
        }
    }
}

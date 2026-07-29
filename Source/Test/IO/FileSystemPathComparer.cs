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

            // Ordinal first: it settles every equal pair and almost every unequal one without a probe,
            // and it is the answer whenever the two sit in different directories -- which is where a
            // single probe of one of them would have been the wrong question to ask anyway.
            return string.Equals(x, y, StringComparison.Ordinal) ||
                (this.IgnoresCase(x) && this.IgnoresCase(y) &&
                    string.Equals(x, y, StringComparison.OrdinalIgnoreCase));
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Two paths that <see cref="Equals"/> calls equal must hash alike, so a path in a directory
        /// that ignores case is hashed by its upper-cased form and every other path ordinally. Paths
        /// in different directories may then collide or not; either is correct, because equality
        /// decides the answer and a collision only costs a comparison.
        /// </remarks>
        public int GetHashCode(string obj) =>
            obj is null ? 0 :
            this.IgnoresCase(obj) ? StringComparer.OrdinalIgnoreCase.GetHashCode(obj) :
            StringComparer.Ordinal.GetHashCode(obj);

        /// <summary>
        /// Returns true if the file system holding the specified path ignores case in it.
        /// </summary>
        /// <remarks>
        /// A file system that refuses to answer is treated as case sensitive, which is the answer
        /// that keeps two different paths apart rather than merging them.
        /// </remarks>
        private bool IgnoresCase(string path)
        {
            string directory;
            try
            {
                directory = Path.GetDirectoryName(path);
            }
            catch (Exception)
            {
                return false;
            }

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

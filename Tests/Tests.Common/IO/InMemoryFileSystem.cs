// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Coyote.IO;

namespace Microsoft.Coyote.Tests.Common.IO
{
    /// <summary>
    /// A file system that lives in memory, for testing the decisions built on top of one.
    /// </summary>
    /// <remarks>
    /// Two things make this worth having over a temporary directory. It is faster, which matters
    /// because the tests it replaces each copied a build output directory before they could begin.
    /// And its case sensitivity is chosen rather than inherited: the behaviour that decides whether
    /// a rewritten assembly gets overwritten by the original is a property of the file system a
    /// developer happens to be using, so on a real one only one of the two answers can be tested,
    /// and the test for the other silently does nothing.
    ///
    /// Where an operation fails, it fails the way the real one does, because the code under test
    /// catches those exceptions by type. Where it cannot -- the real file system has more ways to
    /// refuse than are worth reproducing -- the tests that are about the refusals themselves stay
    /// on the real thing. See ParallelTestFilesTests, which is deliberately not written against
    /// this.
    /// </remarks>
    internal sealed class InMemoryFileSystem : IFileSystem
    {
        /// <summary>
        /// A file, and what is known about it.
        /// </summary>
        private sealed class Entry
        {
            internal byte[] Content { get; set; }

            internal DateTime LastWriteTimeUtc { get; set; }

            internal bool IsReadOnly { get; set; }
        }

        private readonly Dictionary<string, Entry> Files;
        private readonly HashSet<string> Directories;
        private readonly bool CaseInsensitive;

        /// <summary>
        /// How many times a single file has been asked to describe itself.
        /// </summary>
        /// <remarks>
        /// Recorded because the cost of a decision is part of what is being tested here, and is
        /// otherwise invisible: describing every file in a directory one at a time gives the same
        /// answer as listing them, so nothing else would tell the two apart until a build over the
        /// shared frameworks got slower.
        /// </remarks>
        internal int GetFileCount { get; private set; }

        /// <summary>
        /// Every read this file system has been asked for, and what it was allowed to read past.
        /// </summary>
        /// <remarks>
        /// Neither sharing choice shows up in the result of the call, so this is what a test asserts
        /// against. It is not a substitute for asking a real file system to enforce it, which only
        /// Windows does -- see the sharing test in Tests.Tools.
        /// </remarks>
        internal IReadOnlyList<(string Path, FileReadSharing Sharing)> Reads => this.ReadLog;

        private readonly List<(string Path, FileReadSharing Sharing)> ReadLog =
            new List<(string, FileReadSharing)>();

        /// <summary>
        /// Invoked immediately before a file is opened, for tests that change it in that interval.
        /// </summary>
        internal Action<string, FileReadSharing> BeforeOpenRead { get; set; }

        /// <summary>
        /// Stamped on the next write, and advanced by a second each time.
        /// </summary>
        /// <remarks>
        /// Counted rather than read from the clock so that a test can tell two writes apart without
        /// waiting for real time to pass, and so that the timestamps are the same on every run. The
        /// starting point is arbitrary and only has to be a time nothing else would produce.
        /// </remarks>
        private DateTime NextWriteTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        internal InMemoryFileSystem(bool isCaseInsensitive = true)
        {
            this.CaseInsensitive = isCaseInsensitive;
            var comparer = isCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            this.Files = new Dictionary<string, Entry>(comparer);
            this.Directories = new HashSet<string>(comparer);
        }

        /// <summary>
        /// Adds a file with the specified text, creating the directories above it.
        /// </summary>
        internal InMemoryFileSystem WithFile(string path, string contents) =>
            this.WithFile(path, Encoding.UTF8.GetBytes(contents ?? string.Empty));

        /// <summary>
        /// Adds a file with the specified content, creating the directories above it.
        /// </summary>
        internal InMemoryFileSystem WithFile(string path, byte[] contents)
        {
            string full = Normalize(path);
            this.AddDirectories(GetParent(full));
            this.Files[full] = new Entry
            {
                Content = contents ?? Array.Empty<byte>(),
                LastWriteTimeUtc = this.Advance()
            };

            return this;
        }

        /// <summary>
        /// Adds a directory, and every directory above it.
        /// </summary>
        internal InMemoryFileSystem WithDirectory(string path)
        {
            this.AddDirectories(Normalize(path));
            return this;
        }

        /// <summary>
        /// Returns the contents of the specified file as text.
        /// </summary>
        internal string GetContents(string path) => this.ReadAllText(path);

        /// <summary>
        /// Returns every file in this file system, in a stable order.
        /// </summary>
        internal IReadOnlyList<string> GetAllPaths() => this.Files.Keys.OrderBy(p => p, StringComparer.Ordinal).ToList();

        /// <summary>
        /// Sets when the specified file was last written to.
        /// </summary>
        internal void Touch(string path, DateTime lastWriteTimeUtc) =>
            this.Require(Normalize(path)).LastWriteTimeUtc = lastWriteTimeUtc;

        /// <summary>
        /// Makes the specified file refuse to be deleted or overwritten.
        /// </summary>
        internal void SetReadOnly(string path) => this.Require(Normalize(path)).IsReadOnly = true;

        /// <inheritdoc/>
        public bool FileExists(string path) => this.Files.ContainsKey(Normalize(path));

        /// <inheritdoc/>
        public bool DirectoryExists(string path)
        {
            string full = Normalize(path);
            return this.Directories.Contains(full);
        }

        /// <inheritdoc/>
        public IFileEntry GetFile(string path)
        {
            this.GetFileCount++;
            string full = Normalize(path);
            return this.Files.TryGetValue(full, out Entry entry) ?
                new InMemoryFileEntry(full, true, entry.Content.Length, entry.LastWriteTimeUtc) :
                new InMemoryFileEntry(full, false, 0, default);
        }

        /// <inheritdoc/>
        public string ReadAllText(string path) => Encoding.UTF8.GetString(this.Require(Normalize(path)).Content);

        /// <inheritdoc/>
        public void WriteAllText(string path, string contents)
        {
            string full = Normalize(path);
            this.RequireDirectory(GetParent(full));
            if (this.Files.TryGetValue(full, out Entry existing) && existing.IsReadOnly)
            {
                throw new UnauthorizedAccessException($"Access to the path '{full}' is denied.");
            }

            this.Files[full] = new Entry
            {
                Content = Encoding.UTF8.GetBytes(contents ?? string.Empty),
                LastWriteTimeUtc = this.Advance()
            };
        }

        /// <inheritdoc/>
        public Stream OpenRead(string path, FileReadSharing sharing)
        {
            string full = Normalize(path);
            this.ReadLog.Add((full, sharing));
            this.BeforeOpenRead?.Invoke(full, sharing);
            var content = this.Require(full).Content;
            return new MemoryStream(content, false);
        }

        /// <inheritdoc/>
        public void CopyFile(string sourcePath, string targetPath, bool overwrite)
        {
            Entry source = this.Require(Normalize(sourcePath));
            string target = Normalize(targetPath);
            this.RequireDirectory(GetParent(target));
            if (this.Files.TryGetValue(target, out Entry existing))
            {
                // In this order, because 'File.Copy' answers in this order: without 'overwrite' it
                // refuses anything already there and never looks at what it is, and only when it was
                // going to write does being unable to matter.
                if (!overwrite)
                {
                    throw new IOException($"The file '{target}' already exists.");
                }

                if (existing.IsReadOnly)
                {
                    throw new UnauthorizedAccessException($"Access to the path '{target}' is denied.");
                }
            }

            // Deliberately not carrying 'IsReadOnly' over from the source: the real 'File.Copy'
            // creates the destination without the attribute, so a copy of a protected file is not
            // itself protected.
            this.Files[target] = new Entry
            {
                Content = (byte[])source.Content.Clone(),
                LastWriteTimeUtc = source.LastWriteTimeUtc
            };
        }

        /// <inheritdoc/>
        public void MoveFile(string sourcePath, string targetPath)
        {
            string source = Normalize(sourcePath);
            string target = Normalize(targetPath);
            Entry entry = this.Require(source);
            if (this.Files.ContainsKey(target))
            {
                throw new IOException($"Cannot create a file when that file already exists: '{target}'.");
            }

            this.RequireDirectory(GetParent(target));
            this.Files.Remove(source);
            this.Files[target] = entry;
        }

        /// <inheritdoc/>
        public void ReplaceFile(string sourcePath, string targetPath, string backupPath)
        {
            string source = Normalize(sourcePath);
            string target = Normalize(targetPath);
            Entry entry = this.Require(source);
            Entry replaced = this.Require(target);
            if (replaced.IsReadOnly)
            {
                // 'File.Replace' refuses a read-only destination the same way 'File.Delete' and a
                // write do. Moving a read-only *source* is allowed, by both this and the real one:
                // the attribute is on the file, and the file survives the move.
                throw new UnauthorizedAccessException($"Access to the path '{target}' is denied.");
            }

            if (!string.IsNullOrEmpty(backupPath))
            {
                this.Files[Normalize(backupPath)] = replaced;
            }

            this.Files.Remove(source);
            this.Files[target] = entry;
        }

        /// <inheritdoc/>
        public void DeleteFile(string path)
        {
            string full = Normalize(path);
            if (this.Files.TryGetValue(full, out Entry entry))
            {
                if (entry.IsReadOnly)
                {
                    throw new UnauthorizedAccessException($"Access to the path '{full}' is denied.");
                }

                this.Files.Remove(full);
            }
        }

        /// <inheritdoc/>
        public void CreateDirectory(string path) => this.AddDirectories(Normalize(path));

        /// <inheritdoc/>
        public void DeleteDirectory(string path, bool recursive)
        {
            string full = Normalize(path);
            if (!this.Directories.Contains(full))
            {
                return;
            }

            var files = this.Files.Keys.Where(p => IsUnder(full, p, this.CaseInsensitive)).ToList();
            var directories = this.Directories.Where(d => IsUnder(full, d, this.CaseInsensitive)).ToList();
            if (!recursive && (files.Count > 0 || directories.Count > 0))
            {
                throw new IOException($"The directory '{full}' is not empty.");
            }

            foreach (string file in files)
            {
                this.Files.Remove(file);
            }

            foreach (string directory in directories)
            {
                this.Directories.Remove(directory);
            }

            this.Directories.Remove(full);
        }

        /// <inheritdoc/>
        public string[] GetFiles(string directory, string searchPattern)
        {
            string full = Normalize(directory);
            this.RequireDirectory(full);
            var pattern = CompilePattern(searchPattern, this.CaseInsensitive);
            return this.Files.Keys
                .Where(p => IsDirectlyUnder(full, p, this.CaseInsensitive) && pattern.IsMatch(GetName(p)))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray();
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Deliberately does not go through <see cref="GetFile"/>: the real one answers this from the
        /// listing, so counting these as calls to it would make the counter that proves as much
        /// report the very thing it exists to catch.
        /// </remarks>
        public IReadOnlyList<IFileEntry> GetFileEntries(string directory, string searchPattern)
        {
            var entries = new List<IFileEntry>();
            foreach (string path in this.GetFiles(directory, searchPattern))
            {
                Entry entry = this.Files[path];
                entries.Add(new InMemoryFileEntry(path, true, entry.Content.Length, entry.LastWriteTimeUtc));
            }

            return entries;
        }

        /// <inheritdoc/>
        public string[] GetDirectories(string directory, string searchPattern, bool recursive)
        {
            string full = Normalize(directory);
            this.RequireDirectory(full);
            var pattern = CompilePattern(searchPattern, this.CaseInsensitive);
            return this.Directories
                .Where(d => recursive ? IsUnder(full, d, this.CaseInsensitive) :
                    IsDirectlyUnder(full, d, this.CaseInsensitive))
                .Where(d => pattern.IsMatch(GetName(d)))
                .OrderBy(d => d, StringComparer.Ordinal)
                .ToArray();
        }

        /// <inheritdoc/>
        public bool IsCaseInsensitive(string directory) => this.CaseInsensitive;

        /// <summary>
        /// Returns the specified file, or throws the way the real file system does if it is absent.
        /// </summary>
        private Entry Require(string full) => this.Files.TryGetValue(full, out Entry entry) ? entry :
            throw new FileNotFoundException($"Could not find file '{full}'.", full);

        /// <summary>
        /// Throws the way the real file system does if the specified directory is absent.
        /// </summary>
        private void RequireDirectory(string full)
        {
            if (!string.IsNullOrEmpty(full) && !this.Directories.Contains(full))
            {
                throw new DirectoryNotFoundException($"Could not find a part of the path '{full}'.");
            }
        }

        /// <summary>
        /// Adds the specified directory and every directory above it.
        /// </summary>
        private void AddDirectories(string full)
        {
            for (string current = full; !string.IsNullOrEmpty(current); current = GetParent(current))
            {
                if (!this.Directories.Add(current))
                {
                    // Everything above one that is already here is already here too.
                    break;
                }
            }
        }

        /// <summary>
        /// Returns the timestamp for the next write.
        /// </summary>
        private DateTime Advance()
        {
            DateTime stamp = this.NextWriteTime;
            this.NextWriteTime = stamp.AddSeconds(1);
            return stamp;
        }

        /// <summary>
        /// Returns the specified path in the one spelling this file system keys on.
        /// </summary>
        /// <remarks>
        /// Done lexically rather than through <see cref="Path.GetFullPath(string)"/>, which resolves
        /// a relative path against the current directory of the process and would make what this
        /// holds depend on where the test runner was started from.
        /// </remarks>
        private static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            string normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            while (normalized.Contains(new string(Path.DirectorySeparatorChar, 2)))
            {
                normalized = normalized.Replace(new string(Path.DirectorySeparatorChar, 2),
                    Path.DirectorySeparatorChar.ToString());
            }

            return normalized.Length > 1 ? normalized.TrimEnd(Path.DirectorySeparatorChar) : normalized;
        }

        /// <summary>
        /// Returns the directory holding the specified path, or an empty string at the root.
        /// </summary>
        private static string GetParent(string full)
        {
            int index = full.LastIndexOf(Path.DirectorySeparatorChar);
            return index <= 0 ? string.Empty : full.Substring(0, index);
        }

        /// <summary>
        /// Returns the last segment of the specified path.
        /// </summary>
        private static string GetName(string full)
        {
            int index = full.LastIndexOf(Path.DirectorySeparatorChar);
            return index < 0 ? full : full.Substring(index + 1);
        }

        /// <summary>
        /// Returns true if the specified path is anywhere below the specified directory.
        /// </summary>
        private static bool IsUnder(string directory, string path, bool ignoreCase) =>
            path.Length > directory.Length + 1 &&
            path[directory.Length] == Path.DirectorySeparatorChar &&
            path.StartsWith(directory, ignoreCase ?
                StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        /// <summary>
        /// Returns true if the specified path is immediately inside the specified directory.
        /// </summary>
        private static bool IsDirectlyUnder(string directory, string path, bool ignoreCase) =>
            IsUnder(directory, path, ignoreCase) &&
            path.IndexOf(Path.DirectorySeparatorChar, directory.Length + 1) < 0;

        /// <summary>
        /// Returns the specified search pattern as an expression matching a single name.
        /// </summary>
        private static Regex CompilePattern(string searchPattern, bool ignoreCase)
        {
            string expression = "^" + Regex.Escape(searchPattern ?? "*")
                .Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return new Regex(expression, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        }

        /// <summary>
        /// A file of this file system, as it was when it was taken.
        /// </summary>
        private sealed class InMemoryFileEntry : IFileEntry
        {
            internal InMemoryFileEntry(string path, bool exists, long length, DateTime lastWriteTimeUtc)
            {
                this.Path = path;
                this.Exists = exists;
                this.Length = length;
                this.LastWriteTimeUtc = lastWriteTimeUtc;
            }

            /// <inheritdoc/>
            public string Path { get; }

            /// <inheritdoc/>
            public bool Exists { get; }

            /// <inheritdoc/>
            public long Length { get; }

            /// <inheritdoc/>
            public DateTime LastWriteTimeUtc { get; }
        }
    }
}

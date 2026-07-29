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
    /// What a read allows another process to be doing to the file at the same time.
    /// </summary>
    /// <remarks>
    /// Stated here rather than by passing a <see cref="FileShare"/> through, for the same reason
    /// <see cref="IFileSystem.GetDirectories"/> takes a flag instead of a <see cref="SearchOption"/>:
    /// this interface exists so that a file system can be supplied that is not the real one, and a
    /// <see cref="System.IO"/> enumeration in its signature describes the real one.
    ///
    /// Which of the two a caller wants follows from what a torn read would cost it. Cache identity
    /// and copy equality both deny writers; permissive reads remain available to callers that only
    /// need a best-effort snapshot.
    /// </remarks>
    internal enum FileReadSharing
    {
        /// <summary>
        /// Read even while another process holds the file open for writing, accepting that the bytes
        /// may be a file half way through being written.
        /// </summary>
        /// <remarks>
        /// Intended only for best-effort reads whose result does not authorize keeping cached output.
        /// </remarks>
        AllowWriters,

        /// <summary>
        /// Refuse to open while another process holds the file open for writing.
        /// </summary>
        /// <remarks>
        /// What comparing two files wants. A torn read here can compare equal, and equal is the
        /// answer that skips the copy: the output directory would keep bytes that were about to be
        /// replaced, which is the failure the comparison exists to prevent. Being refused is
        /// recoverable, and is recovered from by copying.
        /// </remarks>
        DenyWriters
    }

    /// <summary>
    /// The file system operations the rewriting engine and its cache perform.
    /// </summary>
    /// <remarks>
    /// Introduced so that the decisions built on top of these -- above all whether an assembly still
    /// needs rewriting -- can be checked against a file system held in memory. A wrong answer there
    /// is not a slow build but a silent one: the tests run against an assembly that was never
    /// instrumented, and nothing downstream notices. Those decisions used to be reachable only by
    /// staging a copy of a real assembly in a temporary directory and running the whole engine over
    /// it, which is why most of them had no test of their own at all.
    ///
    /// What is deliberately *not* here is anything Mono.Cecil does. Cecil reads and writes
    /// assemblies through its own paths, and a module read from a stream has no
    /// <c>ModuleDefinition.FileName</c>; the cache records exactly those file names to notice when a
    /// resolved dependency changes, so routing Cecil through this interface would empty that record
    /// and break the staleness detection this exists to protect. Reading and writing assemblies
    /// stays on real paths.
    ///
    /// The members of <see cref="System.IO.Path"/> are also absent. They are pure string functions
    /// with no file system behind them, so virtualizing them would double the size of this for
    /// nothing. The single exception is <see cref="Path.GetFullPath(string)"/>, which resolves a
    /// relative path against the current directory of the process -- every path that reaches it in
    /// production is already absolute, and a test using this interface should pass absolute paths
    /// for the same reason.
    /// </remarks>
    internal interface IFileSystem
    {
        /// <summary>
        /// Returns true if the specified file exists.
        /// </summary>
        bool FileExists(string path);

        /// <summary>
        /// Returns true if the specified directory exists.
        /// </summary>
        bool DirectoryExists(string path);

        /// <summary>
        /// Returns the specified file, whether or not it exists.
        /// </summary>
        /// <remarks>
        /// Replaces constructing a <see cref="FileInfo"/>, which is sealed and has no virtual
        /// members. Like that type, the result is a snapshot taken now rather than a live view.
        /// </remarks>
        IFileEntry GetFile(string path);

        /// <summary>
        /// Returns the contents of the specified file as text.
        /// </summary>
        string ReadAllText(string path);

        /// <summary>
        /// Writes the specified text to the specified file, replacing anything already there.
        /// </summary>
        void WriteAllText(string path, string contents);

        /// <summary>
        /// Opens the specified file for reading.
        /// </summary>
        Stream OpenRead(string path, FileReadSharing sharing);

        /// <summary>
        /// Copies the specified file, optionally over one already at the target.
        /// </summary>
        void CopyFile(string sourcePath, string targetPath, bool overwrite);

        /// <summary>
        /// Moves the specified file to a target that does not exist.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="ReplaceFile"/> because the overload of
        /// <see cref="File.Move(string, string)"/> that overwrites does not exist on every framework
        /// this assembly targets, so the callers already have to choose between the two.
        /// </remarks>
        void MoveFile(string sourcePath, string targetPath);

        /// <summary>
        /// Moves the specified file over one that already exists.
        /// </summary>
        void ReplaceFile(string sourcePath, string targetPath, string backupPath);

        /// <summary>
        /// Deletes the specified file if it exists.
        /// </summary>
        void DeleteFile(string path);

        /// <summary>
        /// Creates the specified directory and every missing directory above it.
        /// </summary>
        void CreateDirectory(string path);

        /// <summary>
        /// Deletes the specified directory if it exists.
        /// </summary>
        void DeleteDirectory(string path, bool recursive);

        /// <summary>
        /// Returns the files in the specified directory that match the specified pattern.
        /// </summary>
        string[] GetFiles(string directory, string searchPattern);

        /// <summary>
        /// Returns the files in the specified directory that match the specified pattern, each
        /// already described.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="GetFiles"/> because the length of every file in a directory can
        /// be had from the enumeration that lists it, while asking for the files and then describing
        /// each one costs a call per file. That is the difference between one listing and several
        /// hundred, over directories -- the shared frameworks -- that hold that many assemblies, and
        /// it is paid both when the manifest is checked and when it is written.
        /// </remarks>
        IReadOnlyList<IFileEntry> GetFileEntries(string directory, string searchPattern);

        /// <summary>
        /// Returns the directories under the specified directory that match the specified pattern.
        /// </summary>
        /// <remarks>
        /// Takes a flag rather than a <see cref="SearchOption"/> so that this interface does not put
        /// an enumeration of the real file system into its own signature.
        /// </remarks>
        string[] GetDirectories(string directory, string searchPattern, bool recursive);

        /// <summary>
        /// Returns true if two spellings differing only in case name one file under the specified
        /// directory.
        /// </summary>
        /// <remarks>
        /// A property of the file system rather than of the operating system: macOS ships
        /// case-insensitive but can be formatted otherwise, and Windows can be told to treat a
        /// single directory case-sensitively. It belongs here rather than in the callers because
        /// answering it means asking the real file system, which is exactly what a test cannot do.
        /// </remarks>
        bool IsCaseInsensitive(string directory);
    }

    /// <summary>
    /// A file, whether or not it exists.
    /// </summary>
    internal interface IFileEntry
    {
        /// <summary>
        /// The full path of this file.
        /// </summary>
        string Path { get; }

        /// <summary>
        /// Whether this file existed when this was taken.
        /// </summary>
        bool Exists { get; }

        /// <summary>
        /// The length of this file in bytes, or zero if it does not exist.
        /// </summary>
        long Length { get; }

        /// <summary>
        /// When this file was last written to, in UTC.
        /// </summary>
        DateTime LastWriteTimeUtc { get; }
    }
}

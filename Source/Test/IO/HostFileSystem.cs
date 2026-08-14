// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Microsoft.Coyote.IO
{
    /// <summary>
    /// The file system of the machine this is running on.
    /// </summary>
    internal sealed class HostFileSystem : IFileSystem
    {
        /// <summary>
        /// The instance every caller that has not been given one uses.
        /// </summary>
        internal static readonly IFileSystem Instance = new HostFileSystem();

        /// <summary>
        /// The answer already found for a directory, so that the probe runs once per location.
        /// </summary>
        /// <remarks>
        /// Process wide, and correctly so: what it records is a property of the machine rather than
        /// of any one run. It lives here rather than beside the code that asks because a cache of
        /// real answers sitting inside a class under test would quietly overrule the file system a
        /// test had supplied, and would do so depending on which test happened to run first.
        ///
        /// Keyed ordinally, which is the one comparison that cannot presume the answer. Folding case
        /// here would file 'Foo' and 'foo' together -- and where they are two directories, which is
        /// precisely the case this is asked about, each carries its own flag and the first one probed
        /// would answer for the other. The cost of being wrong is the wrong path comparer, which is
        /// what decides whether a rewritten output is recognised before the original is copied over
        /// it. Where they are one directory, the cost of ordinal keys is a second probe.
        /// </remarks>
        private static readonly Dictionary<string, bool> ProbedDirectories =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        private HostFileSystem()
        {
        }

        /// <inheritdoc/>
        public bool FileExists(string path) => File.Exists(path);

        /// <inheritdoc/>
        public bool DirectoryExists(string path) => Directory.Exists(path);

        /// <inheritdoc/>
        public IFileEntry GetFile(string path) => new HostFileEntry(new FileInfo(path));

        /// <inheritdoc/>
        public string ReadAllText(string path) => File.ReadAllText(path);

        /// <inheritdoc/>
        public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);

        /// <inheritdoc/>
        public Stream OpenRead(string path, FileReadSharing sharing) =>
            // Unbuffered and sequential: the callers hash or compare the stream in chunks of their
            // own, so a buffering stream would copy every byte of every assembly a second time.
            new FileStream(path, FileMode.Open, FileAccess.Read,
                sharing is FileReadSharing.DenyWriters ? FileShare.Read : FileShare.ReadWrite,
                bufferSize: 1, FileOptions.SequentialScan);

        /// <inheritdoc/>
        public Stream OpenWriteExclusive(string path) =>
            new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.SequentialScan);

        /// <inheritdoc/>
        public Stream OpenWriteNewExclusive(string path) =>
            new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.SequentialScan);

        /// <inheritdoc/>
        public void FlushWrite(Stream stream) => ((FileStream)stream).Flush(true);

        /// <inheritdoc/>
        public void CopyFile(string sourcePath, string targetPath, bool overwrite) =>
            File.Copy(sourcePath, targetPath, overwrite);

        /// <inheritdoc/>
        public void MoveFile(string sourcePath, string targetPath) => File.Move(sourcePath, targetPath);

        /// <inheritdoc/>
        public void ReplaceFile(string sourcePath, string targetPath, string backupPath)
        {
            // Windows can briefly refuse to remove the destination while an antivirus/indexer or
            // the test runner's blame monitor releases a handle. Preserve File.Replace's atomic
            // semantics, but give that transient sharing window a bounded chance to close.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Replace(sourcePath, targetPath, backupPath);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(50);
                }
            }
        }

        /// <inheritdoc/>
        public void DeleteFile(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        /// <inheritdoc/>
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        /// <inheritdoc/>
        public void DeleteDirectory(string path, bool recursive)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive);
            }
        }

        /// <inheritdoc/>
        public string[] GetFiles(string directory, string searchPattern) =>
            Directory.GetFiles(directory, searchPattern);

        /// <inheritdoc/>
        public IReadOnlyList<IFileEntry> GetFileEntries(string directory, string searchPattern)
        {
            // 'DirectoryInfo.GetFiles' rather than 'Directory.GetFiles', because the enumeration it
            // walks already carries the length of each file: the 'FileInfo' it hands back answers for
            // it without going to the file system again. Naming the files and then describing them
            // one at a time asks for what is already in hand, once per file.
            var files = new DirectoryInfo(directory).GetFiles(searchPattern);
            var entries = new IFileEntry[files.Length];
            for (int index = 0; index < files.Length; index++)
            {
                entries[index] = new HostFileEntry(files[index]);
            }

            return entries;
        }

        /// <inheritdoc/>
        public string[] GetDirectories(string directory, string searchPattern, bool recursive) =>
            Directory.GetDirectories(directory, searchPattern,
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

        /// <inheritdoc/>
        /// <remarks>
        /// <see cref="Path.GetFullPath(string)"/> resolves relative segments and separators, but
        /// never reaches the file system for the name itself, so it does not canonicalize case. A
        /// path that arrives through a configuration file or an assembly reference keeps whatever
        /// case it was spelled with, while one that arrives through a directory enumeration carries
        /// the case on disk. Where those two spellings name one file, comparing them ordinally would
        /// miss, and the run would decide a rewritten output is not protected and copy the original
        /// over it. Where they name two files, folding case would do the opposite and protect an
        /// output that nothing rewrote.
        ///
        /// So it is asked rather than assumed. Windows and Linux can keep the answer per directory,
        /// while macOS exposes it for a pathname. If a native query is unavailable, a uniquely named
        /// entry is created inside the requested directory and looked up under a different case.
        /// </remarks>
        public bool IsCaseInsensitive(string directory)
        {
            // The directory itself need not exist yet -- the output directory is created after
            // this. Its nearest existing ancestor is where that output will be created and is the
            // only directory whose behavior can be measured before creation.
            var info = new DirectoryInfo(Path.GetFullPath(directory));
            while (info != null && !info.Exists)
            {
                info = info.Parent;
            }

            if (info is null)
            {
                throw new IOException($"No existing ancestor can establish the case behavior of '{directory}'.");
            }

            lock (ProbedDirectories)
            {
                if (ProbedDirectories.TryGetValue(info.FullName, out bool cached))
                {
                    return cached;
                }

                bool isCaseInsensitive = QueryOrProbe(info, QueryNativeCaseBehavior);
                ProbedDirectories.Add(info.FullName, isCaseInsensitive);
                return isCaseInsensitive;
            }
        }

        /// <summary>
        /// Runs the in-directory fallback with an injectable native query for deterministic tests.
        /// </summary>
        internal static bool QueryOrProbeForTesting(string directory, Func<string, bool?> nativeQuery) =>
            QueryOrProbe(new DirectoryInfo(Path.GetFullPath(directory)), nativeQuery);

        /// <summary>
        /// Returns whether the specified existing directory folds case.
        /// </summary>
        private static bool QueryOrProbe(DirectoryInfo info, Func<string, bool?> nativeQuery)
        {
            bool? queried = nativeQuery(info.FullName);
            if (queried.HasValue)
            {
                return queried.Value;
            }

            // A differently-cased lookup that misses proves sensitivity without changing the
            // directory. A hit can be the same entry on an insensitive file system or a distinct
            // colliding entry on a sensitive one, so only a miss is conclusive.
            foreach (string entry in Directory.EnumerateFileSystemEntries(info.FullName))
            {
                string name = Path.GetFileName(entry);
                string flipped = FlipCase(name);
                if (!string.Equals(flipped, name, StringComparison.Ordinal) &&
                    !File.Exists(Path.Combine(info.FullName, flipped)) &&
                    !Directory.Exists(Path.Combine(info.FullName, flipped)))
                {
                    return false;
                }
            }

            // A GUID makes collision negligible and, unlike probing the directory from its parent,
            // measures the directory that was requested. The entry is removed on every path.
            string sentinel = Path.Combine(info.FullName,
                ".coyote-case-probe-" + Guid.NewGuid().ToString("N"));
            string recased = Path.Combine(info.FullName, FlipCase(Path.GetFileName(sentinel)));
            try
            {
                using (new FileStream(sentinel, FileMode.CreateNew, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                }

                return File.Exists(recased);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Unable to establish the case behavior of directory '{info.FullName}'.", ex);
            }
            finally
            {
                if (File.Exists(sentinel))
                {
                    File.Delete(sentinel);
                }
            }
        }

        /// <summary>
        /// Queries the platform-specific case behavior for the requested directory.
        /// </summary>
        private static bool? QueryNativeCaseBehavior(string directory) =>
            TryQueryWindowsCaseSensitiveFlag(directory) ??
            TryQueryMacCaseSensitiveFlag(directory) ??
            TryQueryLinuxCasefoldFlag(directory);

        /// <summary>
        /// Queries only the native platform facility, for capability-gated tests.
        /// </summary>
        internal static bool? QueryNativeCaseBehaviorForTesting(string directory) =>
            QueryNativeCaseBehavior(directory);

        /// <summary>
        /// Returns whether the specified directory folds case according to the flag Windows keeps for
        /// it, or null where there is no such flag to read.
        /// </summary>
        /// <remarks>
        /// Windows keeps case sensitivity per directory rather than per volume, so no question asked
        /// of an enclosing directory can answer for this one. The flag is reachable only through
        /// <c>NtQueryInformationFile</c>; it was added in Windows 10 1803, and older systems refuse
        /// the information class rather than answering, which reads here as "no flag to read" and
        /// falls back -- correctly, because a system that cannot express it does not have it set.
        /// </remarks>
        private static bool? TryQueryWindowsCaseSensitiveFlag(string directory)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return null;
            }

            try
            {
                using var handle = NativeMethods.CreateFile(directory, NativeMethods.FileListDirectory,
                    NativeMethods.FileShareAll, IntPtr.Zero, NativeMethods.OpenExisting,
                    NativeMethods.FileFlagBackupSemantics, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    return null;
                }

                var information = default(NativeMethods.FileCaseSensitiveInformation);
                int status = NativeMethods.NtQueryInformationFile(handle, out _, ref information,
                    (uint)Marshal.SizeOf(typeof(NativeMethods.FileCaseSensitiveInformation)),
                    NativeMethods.FileCaseSensitiveInformationClass);
                if (status != 0)
                {
                    return null;
                }

                return (information.Flags & NativeMethods.CaseSensitiveDirectory) is 0;
            }
            catch (Exception)
            {
                // A missing 'ntdll', a missing entry point, or a refused handle. None of them says
                // anything about the directory, so none of them is an answer.
                return null;
            }
        }

        /// <summary>
        /// Returns whether macOS reports that the pathname is case insensitive.
        /// </summary>
        private static bool? TryQueryMacCaseSensitiveFlag(string directory)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return null;
            }

            try
            {
                long result = NativeMethods.PathConf(directory, NativeMethods.PathConfCaseSensitive);
                return result < 0 ? (bool?)null : result is 0;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Returns whether Linux marks this directory as case-folding.
        /// </summary>
        private static bool? TryQueryLinuxCasefoldFlag(string directory)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return null;
            }

            int descriptor = -1;
            try
            {
                descriptor = NativeMethods.Open(directory, NativeMethods.OpenReadOnly);
                if (descriptor < 0)
                {
                    return null;
                }

                int flags = 0;
                UIntPtr request = new UIntPtr(IntPtr.Size is 8 ?
                    NativeMethods.GetFlags64 : NativeMethods.GetFlags32);
                if (NativeMethods.Ioctl(descriptor, request, ref flags) != 0)
                {
                    return null;
                }

                return (flags & NativeMethods.CasefoldDirectory) != 0;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (descriptor >= 0)
                {
                    NativeMethods.Close(descriptor);
                }
            }
        }

        /// <summary>
        /// Returns the specified name with the case of every letter in it inverted.
        /// </summary>
        private static string FlipCase(string name)
        {
            var builder = new StringBuilder(name.Length);
            foreach (char character in name)
            {
                builder.Append(char.IsUpper(character) ? char.ToLowerInvariant(character) :
                    char.ToUpperInvariant(character));
            }

            return builder.ToString();
        }

        /// <summary>
        /// The Windows calls needed to read a directory's case sensitivity, which no managed API
        /// exposes.
        /// </summary>
        private static class NativeMethods
        {
            internal const int PathConfCaseSensitive = 11;
            internal const int OpenReadOnly = 0;
            internal const uint GetFlags64 = 0x80086601;
            internal const uint GetFlags32 = 0x80046601;
            internal const int CasefoldDirectory = 0x40000000;

            /// <summary>
            /// FileCaseSensitiveInformation, the information class carrying the flag.
            /// </summary>
            internal const int FileCaseSensitiveInformationClass = 71;

            /// <summary>
            /// FILE_CS_FLAG_CASE_SENSITIVE_DIR, set when the directory does not fold case.
            /// </summary>
            internal const uint CaseSensitiveDirectory = 0x00000001;

            internal const uint FileListDirectory = 0x00000001;

            /// <summary>
            /// FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE. Asking to share everything,
            /// because this only reads a property and must not stand in anybody's way.
            /// </summary>
            internal const uint FileShareAll = 0x00000007;

            internal const uint OpenExisting = 3;

            /// <summary>
            /// FILE_FLAG_BACKUP_SEMANTICS, without which a directory cannot be opened at all.
            /// </summary>
            internal const uint FileFlagBackupSemantics = 0x02000000;

            [StructLayout(LayoutKind.Sequential)]
            internal struct IoStatusBlock
            {
                internal IntPtr Status;
                internal IntPtr Information;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct FileCaseSensitiveInformation
            {
                internal uint Flags;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true,
                EntryPoint = "CreateFileW", BestFitMapping = false)]
            internal static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess,
                uint shareMode, IntPtr securityAttributes, uint creationDisposition,
                uint flagsAndAttributes, IntPtr templateFile);

            [DllImport("ntdll.dll", ExactSpelling = true)]
            internal static extern int NtQueryInformationFile(SafeFileHandle fileHandle,
                out IoStatusBlock ioStatusBlock, ref FileCaseSensitiveInformation fileInformation,
                uint length, int fileInformationClass);

            [DllImport("libc", EntryPoint = "pathconf", SetLastError = true)]
            internal static extern long PathConf(string path, int name);

            [DllImport("libc", EntryPoint = "open", SetLastError = true)]
            internal static extern int Open(string path, int flags);

            [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
            internal static extern int Ioctl(int descriptor, UIntPtr request, ref int flags);

            [DllImport("libc", EntryPoint = "close", SetLastError = true)]
            internal static extern int Close(int descriptor);
        }

        /// <summary>
        /// A file of the host file system, as it was when this was taken.
        /// </summary>
        private sealed class HostFileEntry : IFileEntry
        {
            private readonly FileInfo Info;

            internal HostFileEntry(FileInfo info)
            {
                this.Info = info;
            }

            /// <inheritdoc/>
            public string Path => this.Info.FullName;

            /// <inheritdoc/>
            public bool Exists => this.Info.Exists;

            /// <inheritdoc/>
            public long Length => this.Info.Exists ? this.Info.Length : 0;

            /// <inheritdoc/>
            public DateTime LastWriteTimeUtc => this.Info.LastWriteTimeUtc;
        }
    }
}

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
    /// Mirrors the directory holding the assemblies to rewrite into the output directory.
    /// </summary>
    /// <remarks>
    /// The output directory is a copy of the input one with the rewritten assemblies in place of the
    /// originals, so this copy runs on every run -- including one that decided nothing needed
    /// rewriting, because the cache knows nothing about the rest of what is in the directory.
    ///
    /// That is what makes it delicate. On an up-to-date run this copy is walking over the very files
    /// that run just decided were current, so a mistake here puts the original assembly back over the
    /// rewritten one and leaves an uninstrumented output that nothing downstream detects. Both halves
    /// of the guard against that -- the protected set, and the content comparison -- live here, and
    /// neither had a test of its own while they were reachable only by running the whole engine.
    /// </remarks>
    internal sealed class RewritingOutputMirror
    {
        /// <summary>
        /// How much of each file to read at a time when comparing two of them.
        /// </summary>
        private const int BlockSize = 1 << 16;

        private readonly IFileSystem FileSystem;
        private readonly LogWriter LogWriter;

        /// <summary>
        /// The blocks the two sides of a comparison are read into.
        /// </summary>
        /// <remarks>
        /// Held for the lifetime of the mirror rather than allocated per comparison. This runs over
        /// every same-length file in the directory, so a pair of blocks per file is a hundred and
        /// twenty eight kilobytes of garbage each time, for buffers that never outlive the call.
        /// Instance state rather than static, and so safe for two mirrors at once, but a single
        /// mirror compares one pair of files at a time.
        /// </remarks>
        private readonly byte[] LeftBlock = new byte[BlockSize];
        private readonly byte[] RightBlock = new byte[BlockSize];

        internal RewritingOutputMirror(IFileSystem fileSystem, LogWriter logWriter)
        {
            this.FileSystem = fileSystem;
            this.LogWriter = logWriter;
        }

        /// <summary>
        /// Returns the relative paths that the specified source directory contributes to the output.
        /// </summary>
        /// <param name="sourceDirectory">The directory holding the assemblies to rewrite.</param>
        /// <param name="outputDirectory">The directory to mirror it into.</param>
        internal HashSet<string> GetMirroredFiles(string sourceDirectory, string outputDirectory)
        {
            var comparer = this.FileSystem.IsCaseInsensitive(outputDirectory) ?
                StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var files = new HashSet<string>(comparer);
            var comparison = this.FileSystem.IsCaseInsensitive(outputDirectory) ?
                StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            foreach (string filePath in this.FileSystem.GetFiles(sourceDirectory, "*"))
            {
                AddMirroredFile(sourceDirectory, filePath, files);
            }

            foreach (string directoryPath in this.FileSystem.GetDirectories(sourceDirectory, "*", true))
            {
                if (!IsWithin(directoryPath, outputDirectory, comparison))
                {
                    foreach (string filePath in this.FileSystem.GetFiles(directoryPath, "*"))
                    {
                        AddMirroredFile(sourceDirectory, filePath, files);
                    }
                }
            }

            return files;
        }

        internal void Mirror(string sourceDirectory, string outputDirectory,
            HashSet<string> protectedOutputPaths, IEnumerable<string> mirroredFiles)
        {
            foreach (string relativePath in mirroredFiles.OrderBy(path => path, StringComparer.Ordinal))
            {
                string platformPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
                string filePath = Path.Combine(sourceDirectory, platformPath);
                string targetPath = Path.Combine(outputDirectory, platformPath);
                string destination = Path.GetDirectoryName(targetPath);
                this.FileSystem.CreateDirectory(destination);
                this.CopyFileUnlessProtected(filePath, destination, protectedOutputPaths);
            }
        }

        internal void Mirror(string sourceDirectory, string outputDirectory,
            HashSet<string> protectedOutputPaths) =>
            this.Mirror(sourceDirectory, outputDirectory, protectedOutputPaths,
                this.GetMirroredFiles(sourceDirectory, outputDirectory));

        private static void AddMirroredFile(string sourceDirectory, string filePath, HashSet<string> files)
        {
            string name = Path.GetFileName(filePath);
            if (string.Equals(name, RewritingCache.ManifestFileName, StringComparison.Ordinal) ||
                string.Equals(name, RewritingOutputLedger.ManifestFileName, StringComparison.Ordinal))
            {
                return;
            }

            string relative = filePath.Substring(sourceDirectory.TrimEnd('\\', '/').Length)
                .TrimStart('\\', '/').Replace('\\', '/');
            files.Add(relative);
        }

        /// <summary>
        /// Returns true if the specified path is the specified directory or something below it.
        /// </summary>
        /// <remarks>
        /// By path segment rather than by text. A directory belongs to the output when it is the
        /// output or sits under it, which is not the same as its name beginning with the same
        /// letters: with an output directory of 'input/out', a plain prefix test also claims
        /// 'input/output-assets', a perfectly ordinary input subtree, and the copy silently leaves
        /// everything in it out of the mirror.
        /// </remarks>
        internal static bool IsWithin(string path, string directory, StringComparison comparison)
        {
            string trimmed = directory.TrimEnd('\\', '/');
            if (!path.StartsWith(trimmed, comparison))
            {
                return false;
            }

            // Equal, or continuing with a separator. Anything else shares a prefix and nothing more.
            return path.Length == trimmed.Length ||
                path[trimmed.Length] == '\\' || path[trimmed.Length] == '/';
        }

        /// <summary>
        /// Copies the specified file to the destination, unless doing so would overwrite an output
        /// that is already up to date, or the cache manifest itself.
        /// </summary>
        internal void CopyFileUnlessProtected(string filePath, string destination,
            HashSet<string> protectedOutputPaths)
        {
            if (string.Equals(Path.GetFileName(filePath), RewritingCache.ManifestFileName, StringComparison.Ordinal) ||
                string.Equals(Path.GetFileName(filePath), RewritingOutputLedger.ManifestFileName,
                    StringComparison.Ordinal))
            {
                // An input directory that was itself rewritten in place holds a manifest describing
                // that run. Copying it here would leave a manifest in the output directory that
                // describes a different one.
                this.LogWriter.LogDebug("..... Skipping the '{0}' file, which belongs to another run", filePath);
                return;
            }

            string targetPath = Path.Combine(destination, Path.GetFileName(filePath));
            if (protectedOutputPaths.Contains(Path.GetFullPath(targetPath)))
            {
                this.LogWriter.LogDebug("..... Preserving the up-to-date '{0}' file", targetPath);
                return;
            }

            if (this.IsAlreadyCopied(filePath, targetPath))
            {
                // This copy runs even when the whole run is up to date, for the sake of the untracked
                // files in the directory, so it is on the path that exists to do as little as possible.
                // Two stat calls in place of rewriting tens of megabytes of assemblies, symbols and IL
                // dumps that are already byte for byte what would be written over them.
                this.LogWriter.LogDebug("..... Skipping the unchanged '{0}' file", targetPath);
                return;
            }

            this.LogWriter.LogDebug("..... Copying the '{0}' file", filePath);
            this.CopyFile(filePath, destination);
        }

        /// <summary>
        /// Checks whether the destination already holds what copying the source would put there.
        /// </summary>
        /// <remarks>
        /// Decided on content, not on length and last-write time as the MSBuild copy task does. Equal
        /// metadata is not equal bytes: a file restored, checked out or unpacked with its timestamp
        /// preserved keeps the size and time of the one it replaced, and skipping it on that evidence
        /// would leave the previous bytes in the output. That matters most for exactly the file this
        /// is most likely to be asked about -- a dependency that rewriting resolved from its new
        /// source, while what runs beside the rewritten assembly is the old copy left here.
        ///
        /// Compared rather than hashed because both files are in hand: a digest would have to read
        /// both sides too, and would add a collision class in exchange for nothing. This stops at the
        /// first byte that differs, and at the first block for files that differ early, so the whole
        /// read happens only when the answer is that no copy is needed.
        /// </remarks>
        internal bool IsAlreadyCopied(string filePath, string targetPath)
        {
            var source = this.FileSystem.GetFile(filePath);
            var target = this.FileSystem.GetFile(targetPath);
            if (!target.Exists || source.Length != target.Length)
            {
                return false;
            }

            try
            {
                return this.HasSameContent(filePath, targetPath);
            }
            catch (IOException)
            {
                // Unreadable for the moment says nothing about the content, and the copy that follows
                // reports the problem in its own terms if it is still there.
                return false;
            }
        }

        /// <summary>
        /// Checks whether two files of equal length hold the same bytes.
        /// </summary>
        internal bool HasSameContent(string leftPath, string rightPath)
        {
            // Refused rather than read while something else is writing either side. A file caught
            // half way through being written can compare equal to what is about to replace it, and
            // equal is the answer that skips the copy and leaves the old bytes in the output. The
            // caller turns the refusal back into a copy, which is the safe direction.
            using var left = this.FileSystem.OpenRead(leftPath, FileReadSharing.DenyWriters);
            using var right = this.FileSystem.OpenRead(rightPath, FileReadSharing.DenyWriters);
            byte[] leftBlock = this.LeftBlock;
            byte[] rightBlock = this.RightBlock;
            while (true)
            {
                int count = ReadBlock(left, leftBlock);
                if (count != ReadBlock(right, rightBlock))
                {
                    return false;
                }

                if (count is 0)
                {
                    return true;
                }

                // Eight bytes at a time, because this runs over every file in the directory and the
                // files it runs over are assemblies and IL dumps rather than a handful of bytes.
                int whole = count - (count % sizeof(long));
                for (int index = 0; index < whole; index += sizeof(long))
                {
                    if (BitConverter.ToInt64(leftBlock, index) != BitConverter.ToInt64(rightBlock, index))
                    {
                        return false;
                    }
                }

                for (int index = whole; index < count; index++)
                {
                    if (leftBlock[index] != rightBlock[index])
                    {
                        return false;
                    }
                }
            }
        }

        /// <summary>
        /// Copies the specified file to the destination.
        /// </summary>
        internal void CopyFile(string filePath, string destination) =>
            this.FileSystem.CopyFile(filePath, Path.Combine(destination, Path.GetFileName(filePath)), true);

        /// <summary>
        /// Fills the specified block from the stream, returning how much was read. A short read is not
        /// the end of the stream, so this asks again until the block is full or there is no more.
        /// </summary>
        private static int ReadBlock(Stream stream, byte[] block)
        {
            int total = 0;
            while (total < block.Length)
            {
                int read = stream.Read(block, total, block.Length - total);
                if (read is 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }
    }
}

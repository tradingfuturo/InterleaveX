// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Microsoft.Coyote.Rewriting
{
    /// <summary>
    /// Serializes rewriting transactions that target the same output directory.
    /// </summary>
    internal sealed class RewritingOutputLock : IDisposable
    {
        internal const string FileSuffix = ".rewrite.lock";

        private readonly FileStream Stream;

        private RewritingOutputLock(string path, FileStream stream)
        {
            this.Path = path;
            this.Stream = stream;
        }

        internal string Path { get; }

        internal static RewritingOutputLock Acquire(string outputDirectory, TimeSpan timeout)
        {
            string normalized = RewritingCacheValidator.NormalizeDirectory(outputDirectory);
            string path = normalized + FileSuffix;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            var stopwatch = Stopwatch.StartNew();
            IOException lastError = null;
            do
            {
                try
                {
                    // Readers may inspect the owner record, but another writer cannot acquire the
                    // same path while this handle remains open.
                    var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                        FileShare.Read, 4096, FileOptions.WriteThrough);
                    string owner = string.Format(CultureInfo.InvariantCulture,
                        "pid={0}; started={1:O}; output={2}",
                        Process.GetCurrentProcess().Id, DateTime.UtcNow, normalized);
                    byte[] bytes = Encoding.UTF8.GetBytes(owner);
                    stream.SetLength(0);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                    return new RewritingOutputLock(path, stream);
                }
                catch (IOException ex)
                {
                    lastError = ex;
                }

                if (stopwatch.Elapsed < timeout)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(100));
                }
            }
            while (stopwatch.Elapsed < timeout);

            string recordedOwner = ReadOwner(path);
            throw new IOException(
                $"Timed out after {timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds " +
                $"waiting for the active rewrite transaction targeting '{normalized}'. " +
                $"Lock: '{path}'. Recorded owner: '{recordedOwner}'.",
                lastError);
        }

        private static string ReadOwner(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, false);
                return reader.ReadToEnd();
            }
            catch (Exception)
            {
                return "unavailable";
            }
        }

        public void Dispose()
        {
            this.Stream.Dispose();
            // Keep the owner record. Deleting after closing would race a successor that acquired the
            // same path in between: on Unix the delete could unlink the successor's live lock and let
            // a third process create and lock a different inode. Ownership is the open handle, not
            // existence, and the next owner overwrites this record before returning.
        }
    }
}

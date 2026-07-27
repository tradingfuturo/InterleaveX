// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.IO;

namespace Microsoft.Coyote.SystematicTesting
{
    /// <summary>
    /// Best effort file operations used to coordinate the worker processes of a parallel run.
    /// </summary>
    /// <remarks>
    /// Every operation here reports failure rather than throwing, because they all run on paths where
    /// throwing would cost more than the operation is worth: asking the workers to stop and cleaning up
    /// after them both happen in the finally block that also produces the run's merged report, so an
    /// exception escaping one of them would discard a completed run's result and skip the cleanup that
    /// follows. A stop file that cannot be created is not fatal on its own; the workers are killed once
    /// the grace period elapses.
    ///
    /// The exceptions are deliberately not enumerated. <see cref="File.Create(string)"/> alone raises
    /// <see cref="UnauthorizedAccessException"/> when the path names a directory or a read only file,
    /// several distinct <see cref="IOException"/> subtypes, and argument and security exceptions for
    /// paths it cannot make sense of, and the correct response to all of them is the same.
    /// </remarks>
    internal static class ParallelTestFiles
    {
        /// <summary>
        /// Creates the specified file, and returns false if it could not be created.
        /// </summary>
        internal static bool TryCreate(string path)
        {
            try
            {
                File.Create(path).Dispose();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Deletes the specified file if it exists, and returns false if it could not be deleted.
        /// </summary>
        internal static bool TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return true;
            }
            catch (Exception)
            {
                // The file is in use or already gone; either way there is nothing to do.
                return false;
            }
        }

        /// <summary>
        /// Deletes the specified directory and everything below it if it exists, and returns false if
        /// it could not be deleted.
        /// </summary>
        /// <remarks>
        /// Only ever called on a directory the parallel run created itself, under its own run directory.
        /// A worker whose directory could not be emptied still gets a directory to write into; it just
        /// may not be empty, which the report merge and the artifact promotion both tolerate.
        /// </remarks>
        internal static bool TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}

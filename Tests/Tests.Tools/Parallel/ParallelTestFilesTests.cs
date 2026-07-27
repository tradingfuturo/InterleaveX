// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.IO;
using Microsoft.Coyote.SystematicTesting;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Tools.Tests
{
    /// <summary>
    /// Tests the best effort file operations that coordinate the worker processes of a parallel run.
    /// </summary>
    /// <remarks>
    /// These all run in the finally block that also produces the run's merged report, so the property
    /// under test is not that they succeed but that they never throw: an exception escaping one of them
    /// discards the report of a run that already completed and skips the cleanup that follows, which
    /// leaves worker processes behind.
    /// </remarks>
    public class ParallelTestFilesTests : BaseToolsTest
    {
        public ParallelTestFilesTests(ITestOutputHelper output)
            : base(output)
        {
        }

        /// <summary>
        /// Returns a path under a fresh directory that the specified action is run against, and
        /// removes that directory afterwards.
        /// </summary>
        private static void RunInTemporaryDirectory(Action<string> test)
        {
            string directory = Path.Combine(Path.GetTempPath(), $"coyote-parallel-files-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                test(directory);
            }
            finally
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception)
                {
                }
            }
        }

        [Fact(Timeout = 5000)]
        public void TestCreateSucceedsOnAUsablePath()
        {
            RunInTemporaryDirectory(directory =>
            {
                string path = Path.Combine(directory, ".stop");
                Assert.True(ParallelTestFiles.TryCreate(path));
                Assert.True(File.Exists(path));
            });
        }

        [Fact(Timeout = 5000)]
        public void TestCreateFailsWithoutThrowingWhenThePathIsADirectory()
        {
            // The case that escapes a catch of IOException alone: creating a file over an existing
            // directory raises UnauthorizedAccessException.
            RunInTemporaryDirectory(directory =>
            {
                string path = Path.Combine(directory, ".stop");
                Directory.CreateDirectory(path);

                Assert.False(ParallelTestFiles.TryCreate(path));
            });
        }

        [Fact(Timeout = 5000)]
        public void TestCreateFailsWithoutThrowingWhenTheParentIsMissing()
        {
            RunInTemporaryDirectory(directory =>
            {
                string path = Path.Combine(directory, "no-such-directory", ".stop");
                Assert.False(ParallelTestFiles.TryCreate(path));
            });
        }

        [Fact(Timeout = 5000)]
        public void TestCreateFailsWithoutThrowingOnAMalformedPath()
        {
            // Paths that no file system call can make sense of raise argument exceptions rather than
            // IO ones, on a path where the only correct response is still to report failure.
            Assert.False(ParallelTestFiles.TryCreate(string.Empty));
            Assert.False(ParallelTestFiles.TryCreate(null));
        }

        [Fact(Timeout = 5000)]
        public void TestDeleteOfAMissingFileSucceeds()
        {
            RunInTemporaryDirectory(directory =>
            {
                Assert.True(ParallelTestFiles.TryDelete(Path.Combine(directory, "no-such-file")));
            });
        }

        [Fact(Timeout = 5000)]
        public void TestDeleteRemovesAnExistingFile()
        {
            RunInTemporaryDirectory(directory =>
            {
                string path = Path.Combine(directory, ".stop");
                File.Create(path).Dispose();

                Assert.True(ParallelTestFiles.TryDelete(path));
                Assert.False(File.Exists(path));
            });
        }

        [Fact(Timeout = 5000)]
        public void TestDeleteFailsWithoutThrowingWhenThePathIsADirectory()
        {
            RunInTemporaryDirectory(directory =>
            {
                string path = Path.Combine(directory, ".stop");
                Directory.CreateDirectory(path);

                // File.Exists is false for a directory, so this reports success without touching it;
                // what matters is that it does not throw and does not remove the directory.
                ParallelTestFiles.TryDelete(path);
                Assert.True(Directory.Exists(path));
            });
        }

        [Fact(Timeout = 5000)]
        public void TestDeleteDirectoryOfAMissingDirectorySucceeds()
        {
            RunInTemporaryDirectory(directory =>
            {
                Assert.True(ParallelTestFiles.TryDeleteDirectory(Path.Combine(directory, "no-such-directory")));
            });
        }

        [Fact(Timeout = 5000)]
        public void TestDeleteDirectoryRemovesAPopulatedDirectory()
        {
            RunInTemporaryDirectory(directory =>
            {
                string worker = Path.Combine(directory, "w0");
                Directory.CreateDirectory(Path.Combine(worker, "CoyoteOutput"));
                File.Create(Path.Combine(worker, "CoyoteOutput", "App_0.trace")).Dispose();

                Assert.True(ParallelTestFiles.TryDeleteDirectory(worker));
                Assert.False(Directory.Exists(worker));
            });
        }

        [Fact(Timeout = 5000)]
        public void TestDeleteDirectoryDoesNotThrowOnAMalformedPath()
        {
            // Directory.Exists answers false for a path it cannot parse rather than throwing, so
            // there is nothing to delete and nothing to report; what matters is that neither call
            // escapes into the caller's finally block.
            ParallelTestFiles.TryDeleteDirectory(string.Empty);
            ParallelTestFiles.TryDeleteDirectory(null);
        }
    }
}

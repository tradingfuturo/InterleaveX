// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Coyote.IO;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Tools.Tests
{
    /// <summary>
    /// Tests the parts of the real file system seam that only the real file system can answer for.
    /// </summary>
    /// <remarks>
    /// Everything built on top of <see cref="IFileSystem"/> is tested against one held in memory, and
    /// deliberately so. What cannot be is whether the machine actually enforces what the seam asks
    /// it for: the in-memory file system records the request, which shows that the right thing was
    /// asked for, but a request nothing honours is the same as not making it.
    /// </remarks>
    public class HostFileSystemTests : BaseToolsTest
    {
        public HostFileSystemTests(ITestOutputHelper output)
            : base(output)
        {
        }

        /// <summary>
        /// Runs the specified action against a file that exists only for the duration of the action.
        /// </summary>
        private static void WithTemporaryFile(Action<string> action)
        {
            string path = Path.Combine(Path.GetTempPath(),
                "coyote-sharing-" + Guid.NewGuid().ToString("N") + ".bin");
            File.WriteAllText(path, "the bytes already there");
            try
            {
                action(path);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact(Timeout = 10000)]
        public void TestAReadThatDeniesWritersIsRefusedWhileOneHoldsTheFile()
        {
            // The guard the mirror's comparison rests on, at the only level that can enforce it. A
            // file caught half way through being written can hold exactly the bytes already in the
            // output, and equal is the answer that skips the copy -- so the comparison asks not to be
            // given the file at all while anything is writing it, and the copy happens instead.
            //
            // Windows only, and not because the other platforms get it wrong: .NET emulates
            // 'FileShare' on Unix with advisory 'flock', which does not reproduce the conflict
            // between a writer and a reader that refuses writers. Asserting it there would assert
            // nothing. The in-memory tests cover the half that is about which mode is asked for, on
            // every platform.
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            WithTemporaryFile(path =>
            {
                using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

                Assert.Throws<IOException>(() =>
                    HostFileSystem.Instance.OpenRead(path, FileReadSharing.DenyWriters).Dispose());

                using var reader = HostFileSystem.Instance.OpenRead(path, FileReadSharing.AllowWriters);
                Assert.True(reader.ReadByte() >= 0, "the permissive read must go through while the other is refused");
            });
        }

        [Fact(Timeout = 10000)]
        public void TestBothReadsGoThroughWhenNobodyIsWriting()
        {
            // The mode is about what else may be happening to the file, not about whether it can be
            // read: with nothing holding it, both must open. Otherwise the refusal above would be
            // about something else entirely and the comparison would never run at all.
            WithTemporaryFile(path =>
            {
                foreach (var sharing in new[] { FileReadSharing.DenyWriters, FileReadSharing.AllowWriters })
                {
                    using var stream = HostFileSystem.Instance.OpenRead(path, sharing);
                    Assert.True(stream.ReadByte() >= 0, $"'{sharing}' could not read an idle file");
                }
            });
        }

        [Fact(Timeout = 30000)]
        public void TestCaseSensitivityIsReadFromTheDirectoryItIsAskedAbout()
        {
            // Windows keeps this per directory, so no question asked of an enclosing one answers for
            // this one: an insensitive parent can hold a sensitive child, and the probe that flipped
            // the case of the directory's own name was asking the parent all along. A wrong answer
            // here picks the wrong path comparer, and the comparer decides whether a rewritten output
            // is recognised as protected before the original is copied over it.
            //
            // Windows only: elsewhere case folding belongs to the mounted file system, where the
            // enclosing directory does answer for this one, and there is no per-directory flag to
            // set. 'fsutil' needs no elevation for this but can still be refused, in which case
            // there is nothing to compare and the test says so rather than asserting on a directory
            // that was never made sensitive.
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            string parent = Path.Combine(Path.GetTempPath(), "coyote-case-" + Guid.NewGuid().ToString("N"));
            string child = Path.Combine(parent, "sensitive");
            Directory.CreateDirectory(child);
            try
            {
                Assert.True(HostFileSystem.Instance.IsCaseInsensitive(parent),
                    "an ordinary directory under the temporary path folds case");

                if (!TrySetCaseSensitive(child))
                {
                    return;
                }

                Assert.False(HostFileSystem.Instance.IsCaseInsensitive(child),
                    "the flag was set on this directory, so it must not be reported as folding case");
                Assert.True(HostFileSystem.Instance.IsCaseInsensitive(parent),
                    "its parent was not changed and must still be reported as folding case");
            }
            finally
            {
                Directory.Delete(parent, true);
            }
        }

        [Fact(Timeout = 30000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestNativeCaseQueryFailureFallsBackInsideTheRequestedDirectory()
        {
            string directory = Path.Combine(Path.GetTempPath(),
                "coyote-case-fallback-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string lower = Path.Combine(directory, "existing-name");
                File.WriteAllText(lower, string.Empty);
                bool expected = File.Exists(Path.Combine(directory, "EXISTING-NAME"));

                Assert.Equal(expected,
                    HostFileSystem.QueryOrProbeForTesting(directory, _ => null));
                Assert.Empty(Directory.GetFiles(directory, ".coyote-case-probe-*"));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact(Timeout = 30000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestUnixNativeCaseQueryMatchesTheRequestedDirectory()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
                !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                this.TestOutput.WriteLine(
                    "Skipped: the native Unix pathname case query is not available on this platform.");
                return;
            }

            string directory = Path.Combine(Path.GetTempPath(),
                "coyote-native-case-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                bool? native = HostFileSystem.QueryNativeCaseBehaviorForTesting(directory);
                if (!native.HasValue)
                {
                    this.TestOutput.WriteLine(
                        "Skipped: this filesystem refused or does not support the native pathname case query.");
                    return;
                }

                string lower = Path.Combine(directory, "case-query-entry");
                File.WriteAllText(lower, string.Empty);
                bool observed = File.Exists(Path.Combine(directory, "CASE-QUERY-ENTRY"));
                Assert.Equal(observed, native.Value);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        /// <summary>
        /// Turns on per-directory case sensitivity, returning false if this system will not do it.
        /// </summary>
        private static bool TrySetCaseSensitive(string directory)
        {
            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "fsutil.exe",
                    Arguments = $"file setCaseSensitiveInfo \"{directory}\" enable",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });

                if (process is null)
                {
                    return false;
                }

                process.WaitForExit(20000);
                return process.HasExited && process.ExitCode is 0;
            }
            catch (Exception)
            {
                // No 'fsutil', a refusal, or a file system that does not carry the flag. None of
                // them is a failure of the code under test.
                return false;
            }
        }

        [Fact(Timeout = 10000)]
        public void TestFileEntriesDescribeWhatTheListingFound()
        {
            // The batched listing exists so that the length of each file comes from the enumeration
            // rather than from a call per file. What that must not change is the answer.
            string directory = Path.Combine(Path.GetTempPath(), "coyote-entries-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(Path.Combine(directory, "one.dll"), "a");
                File.WriteAllText(Path.Combine(directory, "two.dll"), "bb");
                File.WriteAllText(Path.Combine(directory, "three.pdb"), "ccc");

                var entries = HostFileSystem.Instance.GetFileEntries(directory, "*.dll");
                Assert.Equal(2, entries.Count);
                foreach (var entry in entries)
                {
                    Assert.True(entry.Exists);
                    Assert.Equal(new FileInfo(entry.Path).Length, entry.Length);
                }
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
}

// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.IO;
using System.Linq;
using Microsoft.Coyote.IO;
using Microsoft.Coyote.Tests.Common.IO;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Tools.Tests
{
    /// <summary>
    /// Tests the file system the rewriting cache tests are written against.
    /// </summary>
    /// <remarks>
    /// A fake nobody tests is a fake nobody should trust: every test written against it inherits
    /// whatever it gets wrong, and inherits it silently, because the tests and the thing they rely
    /// on fail together in the same direction. What matters most here is that it refuses in the same
    /// way the real file system does, since the code under test catches those exceptions by type and
    /// treats them as answers.
    /// </remarks>
    public class InMemoryFileSystemTests : BaseToolsTest
    {
        public InMemoryFileSystemTests(ITestOutputHelper output)
            : base(output)
        {
        }

        /// <summary>
        /// An absolute path that names nothing on the machine running this.
        /// </summary>
        private static readonly string Root = Path.DirectorySeparatorChar + "fake";

        private static string At(params string[] parts) =>
            Path.Combine(new[] { Root }.Concat(parts).ToArray());

        [Fact(Timeout = 5000)]
        public void TestFileRoundTrips()
        {
            var fs = new InMemoryFileSystem().WithFile(At("a.txt"), "hello");
            Assert.True(fs.FileExists(At("a.txt")));
            Assert.Equal("hello", fs.ReadAllText(At("a.txt")));
            Assert.True(fs.DirectoryExists(Root), "adding a file must create the directories above it");
        }

        [Fact(Timeout = 5000)]
        public void TestMissingFileThrowsTheSameWayAsTheRealOne()
        {
            var fs = new InMemoryFileSystem();
            Assert.False(fs.FileExists(At("missing.txt")));
            Assert.Throws<FileNotFoundException>(() => fs.ReadAllText(At("missing.txt")));
            Assert.Throws<FileNotFoundException>(() =>
                fs.OpenRead(At("missing.txt"), FileReadSharing.AllowWriters));
        }

        [Fact(Timeout = 5000)]
        public void TestMissingFileIsStillDescribed()
        {
            // The real 'FileInfo' answers for a file that is not there rather than throwing, and the
            // cache relies on that to tell 'absent' from 'changed'.
            var entry = new InMemoryFileSystem().GetFile(At("missing.txt"));
            Assert.False(entry.Exists);
            Assert.Equal(0, entry.Length);
        }

        [Fact(Timeout = 5000)]
        public void TestWritingIntoAMissingDirectoryThrows()
        {
            var fs = new InMemoryFileSystem();
            Assert.Throws<DirectoryNotFoundException>(() => fs.WriteAllText(At("nope", "a.txt"), "x"));
        }

        [Theory(Timeout = 5000)]
        [InlineData(true)]
        [InlineData(false)]
        public void TestCaseSensitivityIsChosenRatherThanInherited(bool isCaseInsensitive)
        {
            // The whole reason this exists. On a real file system only the behaviour of the machine
            // running the test can be checked, so the other half of this is never exercised at all.
            var fs = new InMemoryFileSystem(isCaseInsensitive).WithFile(At("Cased.txt"), "one");
            Assert.Equal(isCaseInsensitive, fs.FileExists(At("cased.txt")));
            Assert.Equal(isCaseInsensitive, fs.IsCaseInsensitive(Root));
            Assert.True(fs.FileExists(At("Cased.txt")), "the spelling it was written under always names it");
        }

        [Fact(Timeout = 5000)]
        public void TestCopyRefusesToOverwriteUnlessAsked()
        {
            var fs = new InMemoryFileSystem().WithFile(At("a.txt"), "one").WithFile(At("b.txt"), "two");
            Assert.Throws<IOException>(() => fs.CopyFile(At("a.txt"), At("b.txt"), false));
            Assert.Equal("two", fs.ReadAllText(At("b.txt")));

            fs.CopyFile(At("a.txt"), At("b.txt"), true);
            Assert.Equal("one", fs.ReadAllText(At("b.txt")));
            Assert.True(fs.FileExists(At("a.txt")), "a copy leaves the source where it was");
        }

        [Fact(Timeout = 5000)]
        public void TestMoveRefusesAnExistingTarget()
        {
            // The overload of 'File.Move' that overwrites does not exist on every framework this
            // targets, which is why the cache reaches for 'File.Replace' instead. Moving onto
            // something must therefore fail here too, or the cache's choice looks unnecessary.
            var fs = new InMemoryFileSystem().WithFile(At("a.txt"), "one").WithFile(At("b.txt"), "two");
            Assert.Throws<IOException>(() => fs.MoveFile(At("a.txt"), At("b.txt")));

            fs.MoveFile(At("a.txt"), At("c.txt"));
            Assert.False(fs.FileExists(At("a.txt")));
            Assert.Equal("one", fs.ReadAllText(At("c.txt")));
        }

        [Fact(Timeout = 5000)]
        public void TestReplaceRequiresSomethingToReplace()
        {
            var fs = new InMemoryFileSystem().WithFile(At("new.txt"), "one");
            Assert.Throws<FileNotFoundException>(() => fs.ReplaceFile(At("new.txt"), At("old.txt"), null));

            fs.WithFile(At("old.txt"), "two");
            fs.ReplaceFile(At("new.txt"), At("old.txt"), null);
            Assert.Equal("one", fs.ReadAllText(At("old.txt")));
            Assert.False(fs.FileExists(At("new.txt")), "the replacement is moved rather than copied");
        }

        [Fact(Timeout = 5000)]
        public void TestReplaceKeepsTheBackupItIsGiven()
        {
            var fs = new InMemoryFileSystem().WithFile(At("new.txt"), "one").WithFile(At("old.txt"), "two");
            fs.ReplaceFile(At("new.txt"), At("old.txt"), At("backup.txt"));
            Assert.Equal("two", fs.ReadAllText(At("backup.txt")));
        }

        [Fact(Timeout = 5000)]
        public void TestSearchPatternsMatchNamesRatherThanPaths()
        {
            var fs = new InMemoryFileSystem()
                .WithFile(At("one.dll"), "a")
                .WithFile(At("two.dll"), "b")
                .WithFile(At("three.pdb"), "c")
                .WithFile(At("nested", "four.dll"), "d");

            Assert.Equal(new[] { At("one.dll"), At("two.dll") }, fs.GetFiles(Root, "*.dll"));
            Assert.Equal(3, fs.GetFiles(Root, "*").Length);
            Assert.Equal(new[] { At("nested", "four.dll") }, fs.GetFiles(At("nested"), "*.dll"));
        }

        [Fact(Timeout = 5000)]
        public void TestDirectoryListingSeparatesImmediateFromRecursive()
        {
            var fs = new InMemoryFileSystem()
                .WithDirectory(At("one"))
                .WithDirectory(At("one", "deep"))
                .WithDirectory(At("two"));

            Assert.Equal(new[] { At("one"), At("two") }, fs.GetDirectories(Root, "*", false));
            Assert.Equal(new[] { At("one"), At("one", "deep"), At("two") }, fs.GetDirectories(Root, "*", true));
        }

        [Fact(Timeout = 5000)]
        public void TestDeletingADirectoryTakesWhatIsInsideOnlyWhenAsked()
        {
            var fs = new InMemoryFileSystem().WithFile(At("nested", "a.txt"), "one");
            Assert.Throws<IOException>(() => fs.DeleteDirectory(At("nested"), false));
            Assert.True(fs.FileExists(At("nested", "a.txt")));

            fs.DeleteDirectory(At("nested"), true);
            Assert.False(fs.DirectoryExists(At("nested")));
            Assert.False(fs.FileExists(At("nested", "a.txt")));
            Assert.True(fs.DirectoryExists(Root), "only the directory named is taken");
        }

        [Fact(Timeout = 5000)]
        public void TestDeletingAMissingFileIsNotAnError()
        {
            // 'ParallelTestFiles' deletes files it is not sure are there, so this has to be quiet
            // the way the real one is.
            var fs = new InMemoryFileSystem().WithDirectory(Root);
            fs.DeleteFile(At("missing.txt"));
            fs.DeleteDirectory(At("missing"), true);
        }

        [Fact(Timeout = 5000)]
        public void TestReadOnlyFilesRefuseToBeDeleted()
        {
            var fs = new InMemoryFileSystem().WithFile(At("a.txt"), "one");
            fs.SetReadOnly(At("a.txt"));
            Assert.Throws<UnauthorizedAccessException>(() => fs.DeleteFile(At("a.txt")));
            Assert.Throws<UnauthorizedAccessException>(() => fs.WriteAllText(At("a.txt"), "two"));
        }

        [Fact(Timeout = 5000)]
        public void TestReadOnlyFilesRefuseToBeCopiedOver()
        {
            // Every other way of writing to a file honoured this and copying did not, which is the
            // one that matters most: the mirror writes over the output directory by copying, so a
            // fake that lets a copy through cannot be used to test what protects a file from it.
            var fs = new InMemoryFileSystem().WithFile(At("a.txt"), "one").WithFile(At("b.txt"), "two");
            fs.SetReadOnly(At("b.txt"));

            Assert.Throws<UnauthorizedAccessException>(() => fs.CopyFile(At("a.txt"), At("b.txt"), true));
            Assert.Equal("two", fs.ReadAllText(At("b.txt")), StringComparer.Ordinal);
        }

        [Fact(Timeout = 5000)]
        public void TestCopyRefusesAnExistingTargetBeforeLookingAtIt()
        {
            // The order 'File.Copy' answers in: without 'overwrite' it refuses anything already there
            // and never asks what it is, so the refusal is the one about the target existing rather
            // than the one about it being protected.
            var fs = new InMemoryFileSystem().WithFile(At("a.txt"), "one").WithFile(At("b.txt"), "two");
            fs.SetReadOnly(At("b.txt"));

            Assert.Throws<IOException>(() => fs.CopyFile(At("a.txt"), At("b.txt"), false));
        }

        [Fact(Timeout = 5000)]
        public void TestACopyOfAReadOnlyFileIsWritable()
        {
            // 'File.Copy' creates the destination without the attribute, so protection does not
            // travel with the bytes.
            var fs = new InMemoryFileSystem().WithFile(At("a.txt"), "one").WithDirectory(Root);
            fs.SetReadOnly(At("a.txt"));

            fs.CopyFile(At("a.txt"), At("b.txt"), false);
            fs.WriteAllText(At("b.txt"), "two");
            Assert.Equal("two", fs.ReadAllText(At("b.txt")), StringComparer.Ordinal);
        }

        [Fact(Timeout = 5000)]
        public void TestReadOnlyFilesRefuseToBeReplaced()
        {
            // 'File.Replace' refuses a read-only destination the same way a delete and a write do.
            // This is how the cache manifest is written, so it is the path a protected manifest would
            // stop.
            var fs = new InMemoryFileSystem().WithFile(At("new.txt"), "one").WithFile(At("old.txt"), "two");
            fs.SetReadOnly(At("old.txt"));

            Assert.Throws<UnauthorizedAccessException>(() => fs.ReplaceFile(At("new.txt"), At("old.txt"), null));
            Assert.Equal("two", fs.ReadAllText(At("old.txt")), StringComparer.Ordinal);
            Assert.True(fs.FileExists(At("new.txt")), "a refused replace leaves the replacement where it was");
        }

        [Fact(Timeout = 5000)]
        public void TestAReadOnlySourceCanStillBeMoved()
        {
            // Deliberately not symmetrical with the above, and matching the real one: the attribute
            // is on the file rather than on its name, and a move leaves the file in existence.
            var fs = new InMemoryFileSystem().WithFile(At("a.txt"), "one");
            fs.SetReadOnly(At("a.txt"));

            fs.MoveFile(At("a.txt"), At("b.txt"));
            Assert.False(fs.FileExists(At("a.txt")));
            Assert.Equal("one", fs.ReadAllText(At("b.txt")), StringComparer.Ordinal);
            Assert.Throws<UnauthorizedAccessException>(() => fs.WriteAllText(At("b.txt"), "two"));
        }

        [Fact(Timeout = 5000)]
        public void TestFileEntriesAgreeWithListingThemOneAtATime()
        {
            // The two ways of asking must give the same answer, since the cache reads directory
            // contents through the batched one precisely so that it does not have to ask per file.
            var fs = new InMemoryFileSystem()
                .WithFile(At("one.dll"), "a")
                .WithFile(At("two.dll"), "bb")
                .WithFile(At("three.pdb"), "ccc");

            var entries = fs.GetFileEntries(Root, "*.dll");
            Assert.Equal(fs.GetFiles(Root, "*.dll"), entries.Select(e => e.Path));
            Assert.All(entries, entry => Assert.True(entry.Exists));
            Assert.Equal(new long[] { 1, 2 }, entries.Select(e => e.Length));
        }

        [Fact(Timeout = 5000)]
        public void TestFileEntriesDoNotCountAsAskingAboutEachFile()
        {
            // The counter is what the cache's test rests on, so it has to be the batched listing that
            // does not move it rather than the counter that does not count.
            var fs = new InMemoryFileSystem().WithFile(At("one.dll"), "a").WithFile(At("two.dll"), "bb");

            int before = fs.GetFileCount;
            fs.GetFileEntries(Root, "*.dll");
            Assert.Equal(before, fs.GetFileCount);

            fs.GetFile(At("one.dll"));
            Assert.Equal(before + 1, fs.GetFileCount);
        }

        [Fact(Timeout = 5000)]
        public void TestEveryReadRecordsWhatItWasAllowedToReadPast()
        {
            // Neither choice shows up in the stream that comes back, and the two callers of this want
            // opposite ones, so recording it is the only way a test can tell which was asked for.
            var fs = new InMemoryFileSystem().WithFile(At("a.txt"), "one");
            fs.OpenRead(At("a.txt"), FileReadSharing.DenyWriters).Dispose();
            fs.OpenRead(At("a.txt"), FileReadSharing.AllowWriters).Dispose();

            Assert.Equal(new[] { FileReadSharing.DenyWriters, FileReadSharing.AllowWriters },
                fs.Reads.Select(read => read.Sharing));
        }

        [Fact(Timeout = 5000)]
        public void TestWriteTimesAdvanceWithoutRealTimePassing()
        {
            // The cache compares content rather than timestamps, but the tests that show it does
            // need two writes to be distinguishable, and waiting for the real clock to tick would
            // put a sleep into every one of them.
            var fs = new InMemoryFileSystem().WithFile(At("a.txt"), "one").WithFile(At("b.txt"), "two");
            Assert.True(fs.GetFile(At("b.txt")).LastWriteTimeUtc > fs.GetFile(At("a.txt")).LastWriteTimeUtc);

            var stamp = new DateTime(2030, 5, 4, 3, 2, 1, DateTimeKind.Utc);
            fs.Touch(At("a.txt"), stamp);
            Assert.Equal(stamp, fs.GetFile(At("a.txt")).LastWriteTimeUtc);
        }

        [Fact(Timeout = 5000)]
        public void TestContentIsNotSharedBetweenCopies()
        {
            var fs = new InMemoryFileSystem().WithFile(At("a.txt"), "one");
            fs.CopyFile(At("a.txt"), At("b.txt"), false);
            fs.WriteAllText(At("a.txt"), "changed");
            Assert.Equal("one", fs.ReadAllText(At("b.txt")));
        }

        [Fact(Timeout = 5000)]
        public void TestEveryFileIsListedInAStableOrder()
        {
            var fs = new InMemoryFileSystem().WithFile(At("b.txt"), "b").WithFile(At("a.txt"), "a");
            Assert.Equal(new[] { At("a.txt"), At("b.txt") }, fs.GetAllPaths());
        }
    }
}

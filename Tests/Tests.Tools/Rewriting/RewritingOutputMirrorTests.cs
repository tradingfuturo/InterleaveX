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
using Microsoft.Coyote.Rewriting;
using Microsoft.Coyote.Tests.Common.IO;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Tools.Tests
{
    /// <summary>
    /// Tests the copy that turns the input directory into the output one.
    /// </summary>
    /// <remarks>
    /// This copy runs on every run, including the one that decided nothing needed rewriting, so it
    /// walks over the very files that run just decided were current. Getting it wrong puts the
    /// original assembly back over the rewritten one and leaves an uninstrumented output that
    /// nothing downstream detects.
    ///
    /// The comparison it rests on reads both files in blocks, compares eight bytes at a time and
    /// then handles whatever is left over, on top of a loop that tolerates short reads. That is four
    /// boundaries to get wrong and, until this file, no test that would notice any of them: the only
    /// coverage was a single end-to-end case that happened to exercise the equal path.
    /// </remarks>
    public class RewritingOutputMirrorTests : BaseToolsTest
    {
        public RewritingOutputMirrorTests(ITestOutputHelper output)
            : base(output)
        {
        }

        /// <summary>
        /// The size of the block the comparison reads at a time.
        /// </summary>
        private const int BlockSize = 1 << 16;

        private static readonly string Root =
            Path.GetFullPath(Path.DirectorySeparatorChar + "coyote-mirror-tests");

        private static string In(params string[] parts) =>
            Path.Combine(new[] { Root, "input" }.Concat(parts).ToArray());

        private static string Out(params string[] parts) =>
            Path.Combine(new[] { Root, "output" }.Concat(parts).ToArray());

        private static RewritingOutputMirror CreateMirror(InMemoryFileSystem fileSystem) =>
            new RewritingOutputMirror(fileSystem, new MemoryLogWriter(Configuration.Create()));

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestReplacementModeSnapshotIncludesNestedInputsAndRemovesStaleSnapshots()
        {
            string stale = In() + RewritingInputSnapshot.DirectoryMarker + "stale";
            var fileSystem = new InMemoryFileSystem()
                .WithFile(In("root.txt"), "root")
                .WithFile(In("nested", "asset.txt"), "nested")
                .WithFile(Path.Combine(stale, "old.txt"), "stale");

            var snapshot = RewritingInputSnapshot.Create(fileSystem,
                new MemoryLogWriter(Configuration.Create()), In(), In());
            try
            {
                fileSystem.WriteAllText(In("root.txt"), "changed after confirmation");
                fileSystem.WriteAllText(In("nested", "asset.txt"), "changed after confirmation");
                Assert.False(fileSystem.DirectoryExists(stale));
                Assert.Equal("root", fileSystem.GetContents(
                    Path.Combine(snapshot.SnapshotDirectory, "root.txt")));
                Assert.Equal("nested", fileSystem.GetContents(
                    Path.Combine(snapshot.SnapshotDirectory, "nested", "asset.txt")));
                Assert.Equal(In("nested", "asset.txt"), snapshot.ToLogicalPath(
                    Path.Combine(snapshot.SnapshotDirectory, "nested", "asset.txt")));
            }
            finally
            {
                string snapshotDirectory = snapshot.SnapshotDirectory;
                snapshot.Dispose();
                Assert.False(fileSystem.DirectoryExists(snapshotDirectory));
            }
        }

        /// <summary>
        /// Returns bytes that are the same on every run and do not repeat over a block boundary.
        /// </summary>
        private static byte[] CreateContent(int length, int seed = 0)
        {
            byte[] content = new byte[length];
            for (int index = 0; index < length; index++)
            {
                content[index] = (byte)(((index * 31) + seed) % 251);
            }

            return content;
        }

        [Theory(Timeout = 5000)]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(9)]
        [InlineData(BlockSize - 1)]
        [InlineData(BlockSize)]
        [InlineData(BlockSize + 1)]
        [InlineData((2 * BlockSize) + 5)]
        public void TestIdenticalFilesOfEveryAwkwardLength(int length)
        {
            // The comparison reads a block at a time, compares whole eight byte words and then the
            // remainder, so every one of those boundaries is a place to drop bytes or read past the
            // end. A length that is not a multiple of either is the case that catches both.
            byte[] content = CreateContent(length);
            var fileSystem = new InMemoryFileSystem()
                .WithFile(In("a.bin"), content)
                .WithFile(Out("a.bin"), content);

            Assert.True(CreateMirror(fileSystem).HasSameContent(In("a.bin"), Out("a.bin")),
                $"two identical files of {length} bytes were reported as differing");
        }

        [Theory(Timeout = 5000)]
        [InlineData(0)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(BlockSize - 1)]
        [InlineData(BlockSize)]
        [InlineData(BlockSize + 3)]
        public void TestFilesDifferingAtOneByte(int position)
        {
            // Same length, one byte apart. The word-at-a-time comparison and the remainder loop each
            // own part of the range, and a difference in the part the other one covers is exactly
            // what a boundary mistake lets through.
            int length = (2 * BlockSize) + 16;
            byte[] left = CreateContent(length);
            byte[] right = CreateContent(length);
            right[position] ^= 0xFF;

            var fileSystem = new InMemoryFileSystem()
                .WithFile(In("a.bin"), left)
                .WithFile(Out("a.bin"), right);

            Assert.False(CreateMirror(fileSystem).HasSameContent(In("a.bin"), Out("a.bin")),
                $"a difference at byte {position} was not noticed");
        }

        [Fact(Timeout = 5000)]
        public void TestFilesDifferingOnlyInTheFinalRemainder()
        {
            // The bytes after the last whole eight byte word. A comparison that stopped at the last
            // word would call these two files equal, and the copy would be skipped.
            int length = (3 * 8) + 5;
            byte[] left = CreateContent(length);
            byte[] right = CreateContent(length);
            right[length - 1] ^= 0xFF;

            var fileSystem = new InMemoryFileSystem()
                .WithFile(In("a.bin"), left)
                .WithFile(Out("a.bin"), right);

            Assert.False(CreateMirror(fileSystem).HasSameContent(In("a.bin"), Out("a.bin")));
        }

        [Fact(Timeout = 5000)]
        public void TestUnchangedFileIsNotCopiedAgain()
        {
            byte[] content = CreateContent(1024);
            var fileSystem = new InMemoryFileSystem()
                .WithFile(In("a.dll"), content)
                .WithFile(Out("a.dll"), content);

            Assert.True(CreateMirror(fileSystem).IsAlreadyCopied(In("a.dll"), Out("a.dll")));
        }

        [Fact(Timeout = 5000)]
        public void TestASiblingSharingTheOutputNamePrefixIsStillMirrored()
        {
            // The output directory is skipped so that the copy does not walk into itself. Deciding
            // that by text rather than by path segment also skips every sibling whose name merely
            // starts the same way -- 'out' and 'output-assets' being an entirely ordinary pair --
            // and those directories then never reach the output at all.
            var fileSystem = new InMemoryFileSystem()
                .WithFile(Path.Combine(Root, "input", "out", "rewritten.dll"), "the rewritten assembly")
                .WithFile(Path.Combine(Root, "input", "output-assets", "asset.dll"), "an ordinary input")
                .WithFile(Path.Combine(Root, "input", "outer", "other.dll"), "another ordinary input");

            string output = Path.Combine(Root, "input", "out");
            CreateMirror(fileSystem).Mirror(Path.Combine(Root, "input"), output, new HashSet<string>());

            Assert.True(fileSystem.FileExists(Path.Combine(output, "output-assets", "asset.dll")),
                "a sibling sharing the output's name prefix was left out of the mirror");
            Assert.True(fileSystem.FileExists(Path.Combine(output, "outer", "other.dll")),
                "a sibling sharing the output's name prefix was left out of the mirror");
        }

        [Fact(Timeout = 5000)]
        public void TestTheOutputDirectoryIsNotCopiedIntoItself()
        {
            // The other half of the same decision, and the reason it exists at all.
            var fileSystem = new InMemoryFileSystem()
                .WithFile(Path.Combine(Root, "input", "App.dll"), "the original assembly")
                .WithFile(Path.Combine(Root, "input", "out", "App.dll"), "the rewritten assembly");

            string output = Path.Combine(Root, "input", "out");
            CreateMirror(fileSystem).Mirror(Path.Combine(Root, "input"), output, new HashSet<string>());

            Assert.False(fileSystem.DirectoryExists(Path.Combine(output, "out")),
                "the output directory was copied into itself");
        }

        [Theory(Timeout = 5000)]
        [InlineData(true)]
        [InlineData(false)]
        public void TestTheOutputIsRecognizedUnderTheFileSystemsCaseRules(bool isCaseInsensitive)
        {
            // Whether 'OUT' and 'out' name one directory is the file system's to say, and only one of
            // the two answers can be observed on any real machine. Comparing ordinally gets the
            // case-insensitive one wrong, and gets it wrong in the direction that copies the output
            // into itself.
            string output = Path.Combine(Root, "input", "out");
            var fileSystem = new InMemoryFileSystem(isCaseInsensitive)
                .WithFile(Path.Combine(Root, "input", "App.dll"), "the original assembly")
                .WithFile(Path.Combine(Root, "input", "OUT", "App.dll"), "the rewritten assembly")

                // The engine creates the output directory before mirroring into it. Under folded
                // case this is the directory above; under ordinal it is a second one, which is the
                // whole point of the comparison being tested.
                .WithDirectory(output);

            CreateMirror(fileSystem).Mirror(Path.Combine(Root, "input"), output, new HashSet<string>());

            // When case is folded the two spellings are the output itself and must not be copied;
            // when it is not, 'OUT' is an ordinary input subtree and must be.
            Assert.Equal(!isCaseInsensitive,
                fileSystem.FileExists(Path.Combine(Root, "input", "out", "OUT", "App.dll")));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestSourceContainmentUsesTheSourceAncestorsCaseRules()
        {
            string source = In();
            string output = In("out");
            string distinctSource = In("Out");
            var fileSystem = new InMemoryFileSystem(isCaseInsensitive: false)
                .WithFile(Path.Combine(distinctSource, "asset.txt"), "source asset")
                .WithDirectory(output)
                .WithCaseSensitivity(source, isCaseInsensitive: false)
                .WithCaseSensitivity(output, isCaseInsensitive: true);

            CreateMirror(fileSystem).Mirror(source, output, new HashSet<string>());

            Assert.Equal("source asset",
                fileSystem.GetContents(Path.Combine(output, "Out", "asset.txt")));
        }

        [Fact(Timeout = 5000)]
        public void TestComparisonRefusesToReadPastAWriter()
        {
            // The half of the guard that does not show up in the answer. A file caught half way
            // through being written can hold, for the moment it is read, exactly the bytes that are
            // already in the output -- and equal is the answer that skips the copy, leaving the old
            // bytes there. So the comparison asks not to be given the file at all while anything is
            // writing it, and 'IsAlreadyCopied' turns the refusal into a copy.
            //
            // Both sides, because either can be the one being written: the input by whatever is
            // building it, the output by another rewrite of the same directory.
            byte[] content = CreateContent(1024);
            var fileSystem = new InMemoryFileSystem()
                .WithFile(In("a.dll"), content)
                .WithFile(Out("a.dll"), content);

            CreateMirror(fileSystem).HasSameContent(In("a.dll"), Out("a.dll"));

            Assert.Equal(2, fileSystem.Reads.Count);
            Assert.All(fileSystem.Reads, read => Assert.Equal(FileReadSharing.DenyWriters, read.Sharing));
        }

        [Fact(Timeout = 5000)]
        public void TestFileOfADifferentLengthIsCopied()
        {
            var fileSystem = new InMemoryFileSystem()
                .WithFile(In("a.dll"), CreateContent(1024))
                .WithFile(Out("a.dll"), CreateContent(2048));

            Assert.False(CreateMirror(fileSystem).IsAlreadyCopied(In("a.dll"), Out("a.dll")));
        }

        [Fact(Timeout = 5000)]
        public void TestFileWithEqualMetadataButDifferentContentIsCopied()
        {
            // The reason this compares content rather than length and timestamp as the MSBuild copy
            // task does. A file restored, checked out or unpacked with its timestamp preserved keeps
            // the size and time of the one it replaced, and skipping it on that evidence would leave
            // the previous bytes in the output.
            var stamp = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
            var fileSystem = new InMemoryFileSystem()
                .WithFile(In("a.dll"), CreateContent(4096, 1))
                .WithFile(Out("a.dll"), CreateContent(4096, 2));
            fileSystem.Touch(In("a.dll"), stamp);
            fileSystem.Touch(Out("a.dll"), stamp);

            var mirror = CreateMirror(fileSystem);
            Assert.Equal(fileSystem.GetFile(In("a.dll")).Length, fileSystem.GetFile(Out("a.dll")).Length);
            Assert.Equal(fileSystem.GetFile(In("a.dll")).LastWriteTimeUtc,
                fileSystem.GetFile(Out("a.dll")).LastWriteTimeUtc);
            Assert.False(mirror.IsAlreadyCopied(In("a.dll"), Out("a.dll")),
                "equal length and equal timestamp is not equal content");
        }

        [Fact(Timeout = 5000)]
        public void TestMissingTargetIsCopied()
        {
            var fileSystem = new InMemoryFileSystem()
                .WithFile(In("a.dll"), CreateContent(16))
                .WithDirectory(Out());

            Assert.False(CreateMirror(fileSystem).IsAlreadyCopied(In("a.dll"), Out("a.dll")));
        }

        [Fact(Timeout = 5000)]
        public void TestUpToDateOutputIsPreserved()
        {
            // The invariant the whole class exists for, and the one that spans this and the cache:
            // the cache decides which outputs are protected, and this is what has to honour it.
            var fileSystem = new InMemoryFileSystem()
                .WithFile(In("App.dll"), "the original assembly")
                .WithFile(Out("App.dll"), "the rewritten assembly");

            var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Out("App.dll") };
            CreateMirror(fileSystem).CopyFileUnlessProtected(In("App.dll"), Out(), protectedPaths);

            Assert.Equal("the rewritten assembly", fileSystem.GetContents(Out("App.dll")));
        }

        [Fact(Timeout = 5000)]
        public void TestUnprotectedOutputIsOverwritten()
        {
            // The companion to the test above: without a protected set this copy is exactly what puts
            // the original back over the rewritten assembly, which is why the set has to be right.
            var fileSystem = new InMemoryFileSystem()
                .WithFile(In("App.dll"), "the original assembly")
                .WithFile(Out("App.dll"), "the rewritten assembly");

            CreateMirror(fileSystem).CopyFileUnlessProtected(In("App.dll"), Out(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            Assert.Equal("the original assembly", fileSystem.GetContents(Out("App.dll")));
        }

        [Theory(Timeout = 5000)]
        [InlineData(RewritingCache.ManifestFileName)]
        [InlineData(RewritingOutputLedger.ManifestFileName)]
        public void TestManifestOfAnotherRunIsNeverCopied(string manifestName)
        {
            // An input directory that was itself rewritten in place holds a manifest describing that
            // run. Copying it would leave the output directory claiming to be up to date on the
            // strength of a run that produced something else.
            var fileSystem = new InMemoryFileSystem()
                .WithFile(In(manifestName), "{ \"SchemaVersion\": 3 }")
                .WithDirectory(Out());

            CreateMirror(fileSystem).CopyFileUnlessProtected(In(manifestName), Out(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            Assert.False(fileSystem.FileExists(Out(manifestName)));
        }

        [Theory(Timeout = 5000)]
        [InlineData(RewritingCache.ManifestFileName)]
        [InlineData(RewritingOutputLedger.ManifestFileName)]
        [Trait("Category", "RewritingRemediation")]
        public void TestNestedManifestNamedAssetsAreCopied(string manifestName)
        {
            var fileSystem = new InMemoryFileSystem()
                .WithFile(In("assets", manifestName), "ordinary nested asset")
                .WithDirectory(Out());

            CreateMirror(fileSystem).Mirror(In(), Out(), new HashSet<string>());

            Assert.Equal("ordinary nested asset",
                fileSystem.GetContents(Out("assets", manifestName)));
        }

        [Theory(Timeout = 5000)]
        [InlineData("rewriting.cache.json.0123456789abcdef.tmp")]
        [InlineData("REWRITING.OUTPUTS.JSON.0123456789ABCDEF.TMP")]
        [Trait("Category", "RewritingRemediation")]
        public void TestOrphanedRootPublicationFilesAreNotMirrored(string fileName)
        {
            var fileSystem = new InMemoryFileSystem(isCaseInsensitive: true)
                .WithFile(In(fileName), "private publication")
                .WithDirectory(Out());

            CreateMirror(fileSystem).Mirror(In(), Out(), new HashSet<string>());

            Assert.False(fileSystem.FileExists(Out(fileName)));
        }

        [Fact(Timeout = 5000)]
        public void TestMirrorCopiesUntrackedFilesAndNestedDirectories()
        {
            var fileSystem = new InMemoryFileSystem()
                .WithFile(In("App.dll"), "the original assembly")
                .WithFile(In("untracked.txt"), "written after the first run")
                .WithFile(In("nested", "deep.txt"), "nested content")
                .WithDirectory(Out());

            CreateMirror(fileSystem).Mirror(In(), Out(), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            Assert.Equal("written after the first run", fileSystem.GetContents(Out("untracked.txt")));
            Assert.Equal("nested content", fileSystem.GetContents(Out("nested", "deep.txt")));
            Assert.Equal("the original assembly", fileSystem.GetContents(Out("App.dll")));
        }

        [Fact(Timeout = 5000)]
        public void TestMirrorLeavesProtectedOutputsAlone()
        {
            var fileSystem = new InMemoryFileSystem()
                .WithFile(In("App.dll"), "the original assembly")
                .WithFile(In("untracked.txt"), "written after the first run")
                .WithFile(Out("App.dll"), "the rewritten assembly");

            var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Out("App.dll") };
            CreateMirror(fileSystem).Mirror(In(), Out(), protectedPaths);

            Assert.Equal("the rewritten assembly", fileSystem.GetContents(Out("App.dll")));
            Assert.Equal("written after the first run", fileSystem.GetContents(Out("untracked.txt")));
        }

        [Fact(Timeout = 5000)]
        public void TestCaseCollidingSourceFilesAreRejectedForCaseInsensitiveOutput()
        {
            var fileSystem = new InMemoryFileSystem(isCaseInsensitive: false)
                .WithFile(In("Foo.dll"), "first")
                .WithFile(In("foo.dll"), "second")
                .WithDirectory(Out())
                .WithCaseSensitivity(Out(), isCaseInsensitive: true);

            var error = Assert.Throws<InvalidDataException>(() =>
                CreateMirror(fileSystem).GetMirroredFiles(In(), Out(), includeFingerprints: false));

            Assert.Contains("Foo.dll", error.Message);
            Assert.Contains("foo.dll", error.Message);
        }

        [Fact(Timeout = 5000)]
        public void TestNestedMirrorBackupIsExcludedFromSourceInventory()
        {
            string output = In("nested", "output");
            string backup = output + ".mirror-backup-test";
            var fileSystem = new InMemoryFileSystem()
                .WithFile(In("App.dll"), "assembly")
                .WithFile(Path.Combine(backup, "old.dll"), "old output")
                .WithDirectory(output);

            var files = CreateMirror(fileSystem).GetMirroredFiles(In(), output,
                includeFingerprints: false, excludedDirectories: new[] { backup });

            Assert.Contains("App.dll", files.Keys);
            Assert.DoesNotContain(files.Keys, path => path.Contains("mirror-backup", StringComparison.Ordinal));
        }

        [Fact(Timeout = 5000)]
        public void TestMetadataOnlyInventoryDoesNotReadFileBytes()
        {
            var fileSystem = new InMemoryFileSystem()
                .WithFile(In("App.dll"), "assembly")
                .WithFile(In("nested", "deep.txt"), "nested")
                .WithDirectory(Out());

            CreateMirror(fileSystem).GetMirroredFiles(In(), Out(), includeFingerprints: false);

            Assert.Empty(fileSystem.Reads);
        }

        [Fact(Timeout = 5000)]
        public void TestMetadataConfirmationStillDetectsSameMetadataDifferentBytes()
        {
            var fileSystem = new InMemoryFileSystem()
                .WithFile(In("App.dll"), "aaaaaaaa")
                .WithFile(Out("App.dll"), "aaaaaaaa")
                .WithDirectory(Out());
            var mirror = CreateMirror(fileSystem);
            var before = mirror.GetMirroredFiles(In(), Out(), includeFingerprints: false);
            DateTime stamp = fileSystem.GetFile(In("App.dll")).LastWriteTimeUtc;

            fileSystem.WriteAllText(In("App.dll"), "bbbbbbbb");
            fileSystem.Touch(In("App.dll"), stamp);
            var after = mirror.GetMirroredFiles(In(), Out(), includeFingerprints: false);

            Assert.False(mirror.DescribeSameFiles(In(), Out(), before, after));
        }

        [Fact(Timeout = 5000)]
        public void TestAnUnchangedTreeDescribesTheSameFilesTwice()
        {
            // The baseline the confirmation rests on: listing a tree that nothing touched twice has
            // to agree with itself, or every run retries the copy and then fails.
            var fileSystem = new InMemoryFileSystem()
                .WithFile(In("App.dll"), "the original assembly")
                .WithFile(In("nested", "deep.txt"), "nested content")
                .WithDirectory(Out());
            var mirror = CreateMirror(fileSystem);

            var before = mirror.GetMirroredFiles(In(), Out());
            var after = mirror.GetMirroredFiles(In(), Out());

            Assert.True(RewritingOutputMirror.DescribeSameFiles(before, after));
        }

        [Fact(Timeout = 5000)]
        public void TestAFileModifiedAfterItWasCopiedIsNoticed()
        {
            // The copy walks the tree in one pass, so a file rewritten in place after that pass
            // reached it leaves the output holding bytes the input no longer has. Both listings name
            // exactly the same files, which is why comparing names alone accepted this and left an
            // output that no version of the input ever produced.
            var fileSystem = new InMemoryFileSystem()
                .WithFile(In("App.dll"), "the original assembly")
                .WithDirectory(Out());
            var mirror = CreateMirror(fileSystem);

            var before = mirror.GetMirroredFiles(In(), Out());
            fileSystem.WriteAllText(In("App.dll"), "an assembly rebuilt while the copy was running");
            var after = mirror.GetMirroredFiles(In(), Out());

            Assert.Equal(before.Keys, after.Keys);
            Assert.False(RewritingOutputMirror.DescribeSameFiles(before, after));
        }

        [Fact(Timeout = 5000)]
        public void TestAFileRewrittenToTheSameLengthIsNoticed()
        {
            // The case metadata alone might have been expected to miss. It does not: the write time
            // moves even when the length does not, and only a replacement that preserves both
            // survives this -- which is then caught by the content comparison on the next run.
            var fileSystem = new InMemoryFileSystem()
                .WithFile(In("App.dll"), "aaaaaaaa")
                .WithDirectory(Out());
            var mirror = CreateMirror(fileSystem);

            var before = mirror.GetMirroredFiles(In(), Out());
            fileSystem.WriteAllText(In("App.dll"), "bbbbbbbb");
            var after = mirror.GetMirroredFiles(In(), Out());

            Assert.False(RewritingOutputMirror.DescribeSameFiles(before, after));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestAFileRewrittenWhilePreservingMetadataIsNoticed()
        {
            var fileSystem = new InMemoryFileSystem()
                .WithFile(In("App.dll"), "aaaaaaaa")
                .WithDirectory(Out());
            var mirror = CreateMirror(fileSystem);

            var before = mirror.GetMirroredFiles(In(), Out());
            DateTime stamp = fileSystem.GetFile(In("App.dll")).LastWriteTimeUtc;
            fileSystem.WriteAllText(In("App.dll"), "bbbbbbbb");
            fileSystem.Touch(In("App.dll"), stamp);
            var after = mirror.GetMirroredFiles(In(), Out());

            Assert.False(RewritingOutputMirror.DescribeSameFiles(before, after));
        }

        [Fact(Timeout = 5000)]
        public void TestAFileAppearingOrDisappearingIsStillNoticed()
        {
            // What the inventory comparison always caught, kept under test now that it compares
            // more than names.
            var fileSystem = new InMemoryFileSystem()
                .WithFile(In("App.dll"), "the original assembly")
                .WithDirectory(Out());
            var mirror = CreateMirror(fileSystem);

            var before = mirror.GetMirroredFiles(In(), Out());
            fileSystem.WithFile(In("Newcomer.dll"), "arrived mid-copy");

            Assert.False(RewritingOutputMirror.DescribeSameFiles(
                before, mirror.GetMirroredFiles(In(), Out())));

            fileSystem.DeleteFile(In("Newcomer.dll"));
            fileSystem.DeleteFile(In("App.dll"));

            Assert.False(RewritingOutputMirror.DescribeSameFiles(
                before, mirror.GetMirroredFiles(In(), Out())));
        }
    }
}

// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Coyote.Logging;
using Microsoft.Coyote.Rewriting;
using Microsoft.Coyote.Tests.Common.IO;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Tools.Tests
{
    public class RewritingOutputLedgerTests : BaseToolsTest
    {
        private static readonly string Root =
            Path.GetFullPath(Path.DirectorySeparatorChar + "coyote-output-ledger-tests");

        public RewritingOutputLedgerTests(ITestOutputHelper output)
            : base(output)
        {
        }

        private static string In(params string[] parts) =>
            Path.Combine(new[] { Root, "input" }.Concat(parts).ToArray());

        private static string Out(params string[] parts) =>
            Path.Combine(new[] { Root, "output" }.Concat(parts).ToArray());

        private static RewritingOutputLedger CreateLedger(InMemoryFileSystem fileSystem) =>
            new RewritingOutputLedger(fileSystem, new MemoryLogWriter(Configuration.Create()), In(), Out());

        [Fact(Timeout = 5000)]
        public void TestOnlyPreviouslyOwnedStaleOutputsAreRemoved()
        {
            var fileSystem = new InMemoryFileSystem()
                .WithDirectory(In())
                .WithFile(Out("content", "owned.txt"), "old content")
                .WithFile(Out("content", "unowned.txt"), "custom content")
                .WithFile(Out("App.diff.json"), "old artifact");

            CreateLedger(fileSystem).Commit(
                new[] { "content/owned.txt" },
                new[] { "App.diff.json" });

            var next = CreateLedger(fileSystem);
            next.RemoveStaleMirroredFiles(Array.Empty<string>());
            next.Commit(Array.Empty<string>(), Array.Empty<string>());

            Assert.False(fileSystem.FileExists(Out("content", "owned.txt")));
            Assert.False(fileSystem.FileExists(Out("App.diff.json")));
            Assert.True(fileSystem.FileExists(Out("content", "unowned.txt")));
            Assert.True(fileSystem.DirectoryExists(Out("content")));
        }

        [Fact(Timeout = 5000)]
        public void TestAProducedPathThatBecomesMirroredIsPreserved()
        {
            var fileSystem = new InMemoryFileSystem()
                .WithDirectory(In())
                .WithFile(Out("App.dll"), "current mirrored content");
            CreateLedger(fileSystem).Commit(Array.Empty<string>(), new[] { "App.dll" });

            CreateLedger(fileSystem).Commit(new[] { "App.dll" }, Array.Empty<string>());

            Assert.True(fileSystem.FileExists(Out("App.dll")));
        }

        [Fact(Timeout = 5000)]
        public void TestAPathEscapingTheOutputInvalidatesTheWholeLedger()
        {
            var fileSystem = new InMemoryFileSystem()
                .WithDirectory(In())
                .WithFile(Out("owned.txt"), "must survive")
                .WithFile(Path.Combine(Root, "outside.txt"), "outside");
            var manifest = new OutputOwnershipManifest()
            {
                SchemaVersion = 1,
                AssembliesDirectory = RewritingCacheValidator.NormalizeDirectory(In()),
                OutputDirectory = RewritingCacheValidator.NormalizeDirectory(Out()),
                MirroredFiles = new List<string>() { "owned.txt", "../outside.txt" },
                ProducedFiles = new List<string>()
            };
            fileSystem.WriteAllText(Out(RewritingOutputLedger.ManifestFileName),
                JsonSerializer.Serialize(manifest));

            var ledger = CreateLedger(fileSystem);
            ledger.RemoveStaleMirroredFiles(Array.Empty<string>());

            Assert.True(fileSystem.FileExists(Out("owned.txt")));
            Assert.True(fileSystem.FileExists(Path.Combine(Root, "outside.txt")));
        }

        [Theory(Timeout = 5000)]
        [InlineData("/absolute.txt")]
        [InlineData("duplicate.txt")]
        public void TestAnInvalidProducedPathInvalidatesTheWholeLedger(string invalidPath)
        {
            var fileSystem = new InMemoryFileSystem()
                .WithDirectory(In())
                .WithFile(Out("owned.txt"), "must survive");
            var produced = invalidPath == "duplicate.txt" ?
                new List<string>() { invalidPath, invalidPath } :
                new List<string>() { invalidPath };
            var manifest = new OutputOwnershipManifest()
            {
                SchemaVersion = 1,
                AssembliesDirectory = RewritingCacheValidator.NormalizeDirectory(In()),
                OutputDirectory = RewritingCacheValidator.NormalizeDirectory(Out()),
                MirroredFiles = new List<string>() { "owned.txt" },
                ProducedFiles = produced
            };
            fileSystem.WriteAllText(Out(RewritingOutputLedger.ManifestFileName),
                JsonSerializer.Serialize(manifest));

            CreateLedger(fileSystem).RemoveStaleMirroredFiles(Array.Empty<string>());

            Assert.True(fileSystem.FileExists(Out("owned.txt")));
        }

        [Fact(Timeout = 5000)]
        public void TestCorruptOrMismatchedLedgerOwnsNothing()
        {
            var fileSystem = new InMemoryFileSystem()
                .WithDirectory(In())
                .WithFile(Out("owned.txt"), "must survive");
            fileSystem.WriteAllText(Out(RewritingOutputLedger.ManifestFileName), "{ broken");
            CreateLedger(fileSystem).RemoveStaleMirroredFiles(Array.Empty<string>());
            Assert.True(fileSystem.FileExists(Out("owned.txt")));

            var manifest = new OutputOwnershipManifest()
            {
                SchemaVersion = 1,
                AssembliesDirectory = RewritingCacheValidator.NormalizeDirectory(In("elsewhere")),
                OutputDirectory = RewritingCacheValidator.NormalizeDirectory(Out()),
                MirroredFiles = new List<string>() { "owned.txt" },
                ProducedFiles = new List<string>()
            };
            fileSystem.WriteAllText(Out(RewritingOutputLedger.ManifestFileName),
                JsonSerializer.Serialize(manifest));
            CreateLedger(fileSystem).RemoveStaleMirroredFiles(Array.Empty<string>());
            Assert.True(fileSystem.FileExists(Out("owned.txt")));
        }

        [Fact(Timeout = 5000)]
        public void TestAnAttemptThatFailedStillOwnsWhatItCopied()
        {
            // The mirror is retried when the input tree moves underneath it, and the attempt that
            // failed has already copied part of it. A file whose source then vanished is in no set
            // the ledger otherwise knows about -- not the previous run's manifest, and not the
            // listing this run commits -- so without being claimed here it stays in the output for
            // good, belonging to nothing and removable by nothing.
            var fileSystem = new InMemoryFileSystem()
                .WithDirectory(In())
                .WithDirectory(Out());

            var ledger = CreateLedger(fileSystem);
            fileSystem.WithFile(Out("transient.dll"), "copied by the attempt that failed");
            ledger.RemoveStaleMirroredFiles(Array.Empty<string>(), new[] { "transient.dll" });

            Assert.False(fileSystem.FileExists(Out("transient.dll")));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestAnUncopiedAttemptDoesNotOwnAnUnrelatedOutput()
        {
            var fileSystem = new InMemoryFileSystem()
                .WithDirectory(In())
                .WithFile(Out("transient.dll"), "user-owned");

            CreateLedger(fileSystem).RemoveStaleMirroredFiles(Array.Empty<string>(),
                new[] { "transient.dll" });

            Assert.True(fileSystem.FileExists(Out("transient.dll")));
            Assert.Equal("user-owned", fileSystem.GetContents(Out("transient.dll")));
        }

        [Fact(Timeout = 5000)]
        public void TestAnAttemptedFileThatSurvivedIntoTheRunIsKept()
        {
            // The other half of the rule: what an earlier attempt copied and the successful attempt
            // still lists is an ordinary mirrored file, and claiming it must not mean deleting it.
            var fileSystem = new InMemoryFileSystem()
                .WithDirectory(In())
                .WithFile(Out("kept.dll"), "still in the input");

            var ledger = CreateLedger(fileSystem);
            ledger.RemoveStaleMirroredFiles(new[] { "kept.dll" }, new[] { "kept.dll" });
            ledger.Commit(new[] { "kept.dll" }, Array.Empty<string>(), new[] { "kept.dll" });

            Assert.True(fileSystem.FileExists(Out("kept.dll")));
        }

        [Fact(Timeout = 5000)]
        public void TestCommitRemovesWhatOnlyAFailedAttemptContributed()
        {
            // Reached when the copy itself threw rather than when the inventory disagreed: nothing
            // re-lists in that case, so the commit is the last chance to notice.
            var fileSystem = new InMemoryFileSystem()
                .WithDirectory(In())
                .WithDirectory(Out());

            var ledger = CreateLedger(fileSystem);
            fileSystem.WithFile(Out("transient.dll"), "copied by the attempt that failed")
                .WithFile(Out("kept.dll"), "still in the input");
            ledger.Commit(new[] { "kept.dll" }, Array.Empty<string>(),
                new[] { "kept.dll", "transient.dll" });

            Assert.True(fileSystem.FileExists(Out("kept.dll")));
            Assert.False(fileSystem.FileExists(Out("transient.dll")));
        }

        [Fact(Timeout = 5000)]
        public void TestAFailedAtomicReplacementPreservesThePreviousLedger()
        {
            var fileSystem = new InMemoryFileSystem()
                .WithDirectory(In())
                .WithDirectory(Out());
            CreateLedger(fileSystem).Commit(new[] { "first.txt" }, Array.Empty<string>());
            string manifestPath = Out(RewritingOutputLedger.ManifestFileName);
            string before = fileSystem.ReadAllText(manifestPath);
            fileSystem.SetReadOnly(manifestPath);

            Assert.Throws<UnauthorizedAccessException>(() =>
                CreateLedger(fileSystem).Commit(new[] { "second.txt" }, Array.Empty<string>()));
            Assert.Equal(before, fileSystem.ReadAllText(manifestPath));
        }
    }
}

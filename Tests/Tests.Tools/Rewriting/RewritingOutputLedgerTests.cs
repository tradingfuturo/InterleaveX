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
        public void TestVersion3CacheMigratesOnlyProvableOwnership()
        {
            var fileSystem = new InMemoryFileSystem()
                .WithFile(In("App.dll"), "input")
                .WithFile(Out("App.dll"), "output")
                .WithFile(Out("custom.txt"), "custom");
            var cache = new CacheManifest()
            {
                SchemaVersion = 3,
                AssembliesDirectory = RewritingCacheValidator.NormalizeDirectory(In()),
                OutputDirectory = RewritingCacheValidator.NormalizeDirectory(Out()),
                Entries = new List<CacheEntry>()
                {
                    new CacheEntry()
                    {
                        Input = new CacheFile() { Path = In("App.dll"), Exists = true },
                        Output = new CacheFile() { Path = Out("App.dll"), Exists = true },
                        Artifacts = new List<CacheFile>()
                    }
                },
                ResolvedModules = new List<CacheFile>()
            };
            fileSystem.WriteAllText(Out(RewritingCache.ManifestFileName), JsonSerializer.Serialize(cache));

            var ledger = CreateLedger(fileSystem);
            ledger.RemoveStaleMirroredFiles(Array.Empty<string>());
            ledger.Commit(Array.Empty<string>(), Array.Empty<string>());

            Assert.False(fileSystem.FileExists(Out("App.dll")));
            Assert.True(fileSystem.FileExists(Out("custom.txt")));
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

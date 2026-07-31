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
    /// Tests what a run records, as opposed to what a later run makes of it.
    /// </summary>
    /// <remarks>
    /// The interval this is about is the one between rewriting reading a file and the cache
    /// fingerprinting it. Everything the cache writes is measured once the passes are over, so a
    /// dependency replaced while they ran is recorded as its new self while the output beside it was
    /// built from the old one. The manifest that results is entirely self-consistent -- every file it
    /// names matches what is on disk -- so the check that reads it back has nothing to notice, and
    /// every run after it skips against an output no input ever produced.
    ///
    /// Reachable at all only because the cache records an <see cref="IRewrittenAssembly"/> rather
    /// than the Mono.Cecil-backed type: describing a file that changes mid-run meant staging a real
    /// assembly and rewriting it, which is why none of this had a test.
    /// </remarks>
    public class RewritingCacheTests : BaseToolsTest
    {
        public RewritingCacheTests(ITestOutputHelper output)
            : base(output)
        {
        }

        private static readonly string Root =
            Path.GetFullPath(Path.DirectorySeparatorChar + "coyote-record-tests");

        private static string In(params string[] parts) =>
            Path.Combine(new[] { Root, "input" }.Concat(parts).ToArray());

        private static string Out(params string[] parts) =>
            Path.Combine(new[] { Root, "output" }.Concat(parts).ToArray());

        /// <summary>
        /// An assembly that was read, as the cache sees one.
        /// </summary>
        /// <remarks>
        /// The stamps are taken when this is built, which is where the real one takes them: at the
        /// point the file is read, and before anything a test does to it afterwards.
        /// </remarks>
        private sealed class FakeAssembly : IRewrittenAssembly
        {
            private readonly Dictionary<string, ResolutionStamp> ResolutionStamps =
                new Dictionary<string, ResolutionStamp>(StringComparer.Ordinal);

            private readonly HashSet<string> UnreliableStampPaths =
                new HashSet<string>(StringComparer.Ordinal);

            internal FakeAssembly(IFileSystem fileSystem, string filePath, params string[] resolvedModulePaths)
            {
                this.Name = Path.GetFileName(filePath);
                this.FilePath = filePath;
                this.ReferenceNames = Array.Empty<string>();
                this.SearchDirectories = new[] { Path.GetDirectoryName(filePath) };
                this.ResolvedModulePaths = resolvedModulePaths;
                this.ResolutionCandidatePaths = Array.Empty<string>();
                this.FrameworkInventoryRoots = Array.Empty<string>();
                this.FrameworkInventorySnapshots = Array.Empty<CacheDirectoryListing>();

                foreach (string path in resolvedModulePaths.Concat(new[]
                {
                    filePath,
                    Path.ChangeExtension(filePath, "pdb"),
                    Path.ChangeExtension(filePath, ".runtimeconfig.json")
                }))
                {
                    IFileEntry entry = fileSystem.GetFile(path);
                    this.ResolutionStamps[path] = new ResolutionStamp(entry,
                        entry.Exists ? RewritingCacheValidator.ComputeFileFingerprint(fileSystem, path) : null);
                }
            }

            public string Name { get; }

            public string FilePath { get; }

            public IReadOnlyList<string> ReferenceNames { get; }

            public IReadOnlyList<string> SearchDirectories { get; }

            public IEnumerable<string> ResolvedModulePaths { get; }

            public IEnumerable<string> ResolutionCandidatePaths { get; }

            public IEnumerable<string> UnreliableResolutionStampPaths => this.UnreliableStampPaths;

            public IReadOnlyList<string> FrameworkInventoryRoots { get; }

            public IReadOnlyList<CacheDirectoryListing> FrameworkInventorySnapshots { get; }

            public bool TryGetResolutionStamp(string path, out ResolutionStamp stamp) =>
                this.ResolutionStamps.TryGetValue(path, out stamp);

            internal void MarkUnreliable(string path) => this.UnreliableStampPaths.Add(path);
        }

        /// <summary>
        /// A file system holding one assembly and the dependency it resolved, with the output of a
        /// rewrite already in place.
        /// </summary>
        private static InMemoryFileSystem CreateFileSystem() =>
            new InMemoryFileSystem()
                .WithFile(In("App.dll"), "the original assembly")
                .WithFile(In("App.runtimeconfig.json"), "{ }")
                .WithFile(In("Dependency.dll"), "a dependency")
                .WithFile(Out("App.dll"), "the rewritten assembly")
                .WithDirectory(Out());

        private static RewritingCache CreateCache(InMemoryFileSystem fileSystem)
        {
            var options = RewritingOptions.Create();
            options.AssembliesDirectory = In();
            options.OutputDirectory = Out();
            options.AssemblyPaths = new HashSet<string>() { In("App.dll") };
            return new RewritingCache(options, Configuration.Create(),
                new MemoryLogWriter(Configuration.Create()), fileSystem);
        }

        /// <summary>
        /// Records the run the fixture describes and returns whether a manifest was written.
        /// </summary>
        private static bool TryRecordRun(InMemoryFileSystem fileSystem, Action<InMemoryFileSystem> afterReading)
        {
            var cache = CreateCache(fileSystem);
            var assembly = new FakeAssembly(fileSystem, In("App.dll"), In("Dependency.dll"));
            cache.RegisterRewriteInputs(new[] { assembly });

            // Everything before this stands for rewriting: the files have been read, and the output
            // has been written from what they held at the time.
            afterReading?.Invoke(fileSystem);

            cache.RecordAssembly(assembly, Out("App.dll"), Array.Empty<string>());
            cache.Save();
            return fileSystem.FileExists(Out(RewritingCache.ManifestFileName));
        }

        [Fact(Timeout = 5000)]
        public void TestAnUnchangedRunWritesItsManifest()
        {
            // The check must not fire on the ordinary run, which is every run: a cache that refuses
            // to record anything is a cache that never reports anything as up to date.
            Assert.True(TryRecordRun(CreateFileSystem(), null));
        }

        [Fact(Timeout = 5000)]
        public void TestAModuleReplacedAfterItWasResolvedIsNotRecorded()
        {
            // The case this exists for. Recording the dependency as it is now would describe an
            // output that was rewritten against what it was before, and nothing downstream could
            // ever tell the two apart.
            Assert.False(TryRecordRun(CreateFileSystem(), fileSystem =>
                fileSystem.WriteAllText(In("Dependency.dll"), "a different dependency")));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestAModuleReplacedWhilePreservingMetadataIsNotRecorded()
        {
            Assert.False(TryRecordRun(CreateFileSystem(), fileSystem =>
            {
                DateTime stamp = fileSystem.GetFile(In("Dependency.dll")).LastWriteTimeUtc;
                fileSystem.WriteAllText(In("Dependency.dll"), "b dependency");
                fileSystem.Touch(In("Dependency.dll"), stamp);
            }));
        }

        [Fact(Timeout = 5000)]
        public void TestAnInputReplacedWhileItWasRewrittenIsNotRecorded()
        {
            // A rebuild landing mid-run. The rewritten output came from the assembly that was there
            // when the passes ran, so recording the one that replaced it would report the next run
            // as up to date against an output the new input never produced.
            Assert.False(TryRecordRun(CreateFileSystem(), fileSystem =>
                fileSystem.WriteAllText(In("App.dll"), "a rebuilt assembly")));
        }

        [Fact(Timeout = 5000)]
        public void TestARuntimeConfigReplacedDuringRewritingIsNotRecorded()
        {
            // The runtime config names the shared frameworks resolution falls back to, so editing it
            // points the rewriter at different implementation assemblies. Recorded as read, for the
            // same reason as the assemblies themselves.
            Assert.False(TryRecordRun(CreateFileSystem(), fileSystem =>
                fileSystem.WriteAllText(In("App.runtimeconfig.json"), "{ \"runtimeOptions\": { } }")));
        }

        [Fact(Timeout = 5000)]
        public void TestASymbolFileAppearingDuringRewritingIsNotRecorded()
        {
            // Whether symbols are read decides whether they are written, and that was answered
            // before the passes ran. One arriving afterwards means the output does not carry what a
            // rewrite of what is on disk now would have carried.
            Assert.False(TryRecordRun(CreateFileSystem(), fileSystem =>
                fileSystem.WithFile(In("App.pdb"), "symbols that were not there")));
        }

        [Fact(Timeout = 5000)]
        public void TestAnUnreliableResolutionStampIsNotRecorded()
        {
            var fileSystem = CreateFileSystem();
            var cache = CreateCache(fileSystem);
            var assembly = new FakeAssembly(fileSystem, In("App.dll"), In("Dependency.dll"));
            assembly.MarkUnreliable(In("Dependency.dll"));
            cache.RegisterRewriteInputs(new[] { assembly });

            cache.RecordAssembly(assembly, Out("App.dll"), Array.Empty<string>());
            cache.Save();

            Assert.False(fileSystem.FileExists(Out(RewritingCache.ManifestFileName)));
        }

        [Fact(Timeout = 5000)]
        public void TestTheRewrittenOutputIsNotJudgedAgainstWhatWasRead()
        {
            // The output is *supposed* to differ from everything that was read -- this run wrote it.
            // Rewriting in place makes the input its own output, so a check that did not know which
            // paths this run produced would refuse every in-place run there is.
            var fileSystem = new InMemoryFileSystem()
                .WithFile(In("App.dll"), "the original assembly")
                .WithFile(In("App.runtimeconfig.json"), "{ }")
                .WithFile(In("Dependency.dll"), "a dependency");

            var options = RewritingOptions.Create();
            options.AssembliesDirectory = In();
            options.OutputDirectory = In();
            options.AssemblyPaths = new HashSet<string>() { In("App.dll") };
            var cache = new RewritingCache(options, Configuration.Create(),
                new MemoryLogWriter(Configuration.Create()), fileSystem);

            var assembly = new FakeAssembly(fileSystem, In("App.dll"), In("Dependency.dll"));
            cache.RegisterRewriteInputs(new[] { assembly });

            // What rewriting in place does: the input is replaced by its own rewritten self.
            fileSystem.WriteAllText(In("App.dll"), "the rewritten assembly");

            cache.RecordAssembly(assembly, In("App.dll"), Array.Empty<string>());
            cache.Save();

            Assert.True(fileSystem.FileExists(In(RewritingCache.ManifestFileName)));
        }
    }
}

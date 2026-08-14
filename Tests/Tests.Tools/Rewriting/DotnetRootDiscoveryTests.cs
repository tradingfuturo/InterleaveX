// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Coyote.Logging;
using Microsoft.Coyote.Rewriting;
using Microsoft.Coyote.Tests.Common.IO;
using Mono.Cecil;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Tools.Tests
{
    /// <summary>
    /// Tests that the .NET installation the rewriter probes is the one it was told about.
    /// </summary>
    /// <remarks>
    /// Which installation this answer names decides which assemblies resolution can fall back to,
    /// and therefore what IL a rewrite produces. It used to be cached in a static for the lifetime of
    /// the process, so it was whatever the first caller resolved; the file system and the environment
    /// are passed in instead, so that a caller describing a different installation gets a different
    /// answer rather than the machine's.
    ///
    /// Only the 'DOTNET_ROOT' branch is exercised here. The fallback below it derives a root from
    /// <c>RuntimeEnvironment.GetRuntimeDirectory()</c>, which is the real runtime's and cannot be
    /// described by a file system held in memory, so a test of it would only assert where this test
    /// process happens to be installed.
    /// </remarks>
    public class DotnetRootDiscoveryTests : BaseToolsTest
    {
        public DotnetRootDiscoveryTests(ITestOutputHelper output)
            : base(output)
        {
        }

        /// <summary>
        /// An absolute path that names nothing on the machine running this, because the file system
        /// held in memory normalizes lexically and would resolve a relative one against the runner.
        /// </summary>
        private static readonly string Root =
            Path.GetFullPath(Path.DirectorySeparatorChar + "coyote-dotnet-root-tests");

        /// <summary>
        /// The variable the answer is read from, which differs with the bitness of this process.
        /// </summary>
        private static string RootVariable =>
            Environment.Is64BitProcess ? "DOTNET_ROOT" : "DOTNET_ROOT(x86)";

        /// <summary>
        /// A file system describing an installation at the specified root, laid out as the real one
        /// is. Naming a file is enough: the fake creates every directory above it.
        /// </summary>
        private static InMemoryFileSystem Installation(string root) =>
            new InMemoryFileSystem().WithFile(
                Path.Combine(root, "shared", "Microsoft.NETCore.App", "9.9.9", "System.Runtime.dll"),
                "an assembly");

        [Fact(Timeout = 5000)]
        public void TestTheInstallationNamedByTheEnvironmentIsTheOneFound()
        {
            string root = Path.Combine(Root, "elsewhere");

            Assert.Equal(root, AssemblyInfo.GetDotnetRoot(
                Installation(root), name => name == RootVariable ? root : null));
        }

        [Fact(Timeout = 5000)]
        public void TestTheEnvironmentIsAskedRatherThanTheProcess()
        {
            // The point of passing one in. A delegate that answers for no variable at all must not
            // be quietly supplemented by what this process was started with, or a caller describing
            // an installation would still get whichever one the machine has.
            string root = Path.Combine(Root, "unreachable");
            var asked = new List<string>();

            string found = AssemblyInfo.GetDotnetRoot(Installation(root), name =>
            {
                asked.Add(name);
                return null;
            });

            Assert.Contains(RootVariable, asked);
            Assert.NotEqual(root, found);
        }

        [Fact(Timeout = 5000)]
        public void TestAnInstallationThatIsNotThereIsNotUsed()
        {
            // The variable is somebody's setting rather than an observation, so it can name a
            // directory that has since gone. Taking it on trust would send resolution probing a root
            // that offers nothing, and the assemblies that would have resolved from the real one
            // would silently fail to.
            string root = Path.Combine(Root, "removed");

            Assert.NotEqual(root, AssemblyInfo.GetDotnetRoot(
                new InMemoryFileSystem(), name => name == RootVariable ? root : null));
        }

        [Fact(Timeout = 5000)]
        public void TestTheFileSystemIsAskedRatherThanTheDisk()
        {
            // The companion to the test above, and the reason the file system is passed in too:
            // whether the named root exists is answered by the file system this run was given.
            // Reading the real disk would report a root that the caller's file system does not hold.
            string root = Path.Combine(Root, "described");

            Assert.False(Directory.Exists(root),
                "this test needs a root the real file system does not hold");
            Assert.Equal(root, AssemblyInfo.GetDotnetRoot(
                Installation(root), name => name == RootVariable ? root : null));
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestCacheIdentityAndFallbackKeepTheRootFrozenAtConstruction()
        {
            string rootA = Path.Combine(Root, "frozen-a");
            string rootB = Path.Combine(Root, "changed-b");
            string relativeCandidate = Path.Combine(
                "shared", "Test.Framework", "1.0.0", "FrozenProbe.dll");
            byte[] assembly = File.ReadAllBytes(typeof(DotnetRootDiscoveryTests).Assembly.Location);
            var fileSystem = new InMemoryFileSystem()
                .WithFile(Path.Combine(rootA, relativeCandidate), assembly)
                .WithFile(Path.Combine(rootB, relativeCandidate), assembly);
            var options = RewritingOptions.Create();
            options.AssembliesDirectory = Path.Combine(Root, "input");
            options.OutputDirectory = Path.Combine(Root, "output");
            options.AssemblyPaths = new HashSet<string>()
            {
                Path.Combine(options.AssembliesDirectory, "App.dll")
            };
            var configuration = Configuration.Create();
            using var logWriter = new MemoryLogWriter(configuration);
            int reads = 0;
            var frozen = new RewritingEngine(options, configuration, logWriter, new Profiler(),
                fileSystem, name => name == RootVariable ? (++reads is 1 ? rootA : rootB) : null);
            var changed = new RewritingEngine(options, configuration, logWriter, new Profiler(),
                fileSystem, name => name == RootVariable ? rootB : null);

            Assert.Equal(rootA, frozen.EffectiveDotnetRoot);
            Assert.NotEqual(frozen.CreateCache().ConfigurationIdentity,
                changed.CreateCache().ConfigurationIdentity);
            using AssemblyDefinition resolved = frozen.TryResolveFromSharedFrameworks(
                new AssemblyNameReference("FrozenProbe", new Version(1, 0, 0, 0)));

            Assert.NotNull(resolved);
            Assert.Equal(1, reads);
            Assert.Contains(fileSystem.Reads, read =>
                string.Equals(read.Path, Path.Combine(rootA, relativeCandidate),
                    StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(fileSystem.Reads, read =>
                string.Equals(read.Path, Path.Combine(rootB, relativeCandidate),
                    StringComparison.OrdinalIgnoreCase));
        }
    }
}

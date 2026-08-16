// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.IO;
using System.Reflection;
using Microsoft.Coyote.IO;
using Microsoft.Coyote.Rewriting;
using Microsoft.Coyote.Tests.Common.IO;
using Mono.Cecil;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Tools.Tests
{
    /// <summary>
    /// Tests that resolution stamp failures remain visible to cache recording.
    /// </summary>
    public sealed class TrackingAssemblyResolverTests : BaseToolsTest
    {
        public TrackingAssemblyResolverTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestAStampFailureIsMarkedUnreliableWithoutFailingResolution()
        {
            string path = Path.GetFullPath(Path.DirectorySeparatorChar +
                "coyote-resolver-tests" + Path.DirectorySeparatorChar + "Dependency.dll");
            var fileSystem = new InMemoryFileSystem().WithFile(path, "dependency");
            fileSystem.BeforeGetFile = failingPath =>
            {
                if (string.Equals(failingPath, path, StringComparison.Ordinal))
                {
                    throw new IOException("injected stamp failure");
                }
            };
            var resolver = new TrackingAssemblyResolver(fileSystem);

            resolver.Stamp(path);

            Assert.Contains(path, resolver.UnreliableResolutionStampPaths);
            Assert.False(resolver.TryGetResolutionStamp(path, out _));
        }

        /// <summary>
        /// A resolved assembly must not be left open, because rewriting overwrites what it resolves.
        /// </summary>
        /// <remarks>
        /// A batch that replaces its assemblies in place copies each rewritten assembly back over the
        /// snapshot copy it was read from, so that batch members rewritten later resolve signatures
        /// from the transformed dependency
        /// (<see cref="RewritingEngine"/>, right after the assembly is written). Resolution and that
        /// overwrite therefore name the same files, and the whole batch is loaded before the first
        /// assembly is rewritten, so a resolution taken during loading is still cached when the
        /// overwrite runs.
        ///
        /// <see cref="AssemblyInfo"/> reads the assembly it owns with InMemory set, but that covers
        /// only that one assembly. Cecil's <see cref="BaseAssemblyResolver"/> builds its own
        /// <see cref="ReaderParameters"/> for everything it resolves, and InMemory defaults to false
        /// there, so the resolved module keeps a FileStream on the file and
        /// <see cref="DefaultAssemblyResolver"/> caches that module for the resolver's lifetime. The
        /// overlay then fails with "The process cannot access the file ... because it is being used by
        /// another process", killing the run.
        ///
        /// Loading is enough to reach this on its own: reading a custom attribute whose constructor
        /// argument is an enum from another assembly makes Cecil resolve that assembly, to learn the
        /// enum's underlying type, before any pass has run.
        ///
        /// The probe assembly is renamed rather than copied under its own name because Cecil seeds its
        /// search directories with "." — resolving a name that also exists beside the test binary finds
        /// that copy instead, and the test then proves nothing.
        /// </remarks>
        [Fact(Timeout = 15000)]
        public void TestResolutionDoesNotHoldTheResolvedFileOpen()
        {
            const string ProbeName = "CoyoteResolverLockProbe";
            string directory = Path.Combine(Path.GetTempPath(),
                "coyote-resolver-lock-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, ProbeName + ".dll");
                using (var probe = AssemblyDefinition.ReadAssembly(
                    Assembly.GetExecutingAssembly().Location,
                    new ReaderParameters() { InMemory = true }))
                {
                    probe.Name.Name = ProbeName;
                    probe.MainModule.Name = ProbeName + ".dll";
                    probe.Write(path);
                }

                using var resolver = new TrackingAssemblyResolver(HostFileSystem.Instance);
                resolver.AddSearchDirectory(directory);
                Assert.NotNull(resolver.Resolve(AssemblyNameReference.Parse(ProbeName)));

                // The overlay the engine performs, while that resolution is still cached.
                File.Copy(path, path + ".copy", true);
                File.Copy(path + ".copy", path, true);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
}

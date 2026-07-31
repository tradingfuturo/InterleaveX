// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.IO;
using Microsoft.Coyote.Rewriting;
using Microsoft.Coyote.Tests.Common.IO;
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
    }
}

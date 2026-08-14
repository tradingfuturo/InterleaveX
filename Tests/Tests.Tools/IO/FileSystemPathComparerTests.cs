// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Coyote.IO;
using Microsoft.Coyote.Tests.Common.IO;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Tools.Tests
{
    public sealed class FileSystemPathComparerTests : BaseToolsTest
    {
        public FileSystemPathComparerTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestCaseInsensitiveChildrenDoNotFoldCaseSensitiveAncestors()
        {
            string root = Path.GetFullPath(Path.DirectorySeparatorChar + "coyote-path-comparer-tests");
            string upper = Path.Combine(root, "Lib");
            string lower = Path.Combine(root, "lib");
            var fileSystem = new InMemoryFileSystem(isCaseInsensitive: false)
                .WithDirectory(upper)
                .WithDirectory(lower)
                .WithCaseSensitivity(upper, true)
                .WithCaseSensitivity(lower, true);
            var comparer = new FileSystemPathComparer(fileSystem);
            var paths = new HashSet<string>(comparer)
            {
                Path.Combine(upper, "One.dll"),
                Path.Combine(lower, "One.dll")
            };

            Assert.Equal(2, paths.Count);
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestCaseInsensitiveDriveRootSpellingsCompareEqual()
        {
            string root = Path.GetPathRoot(Path.GetFullPath("."));
            if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
            {
                return;
            }

            string upperRoot = char.ToUpperInvariant(root[0]) + root.Substring(1);
            string lowerRoot = char.ToLowerInvariant(root[0]) + root.Substring(1);
            var fileSystem = new InMemoryFileSystem(isCaseInsensitive: true)
                .WithDirectory(upperRoot)
                .WithCaseSensitivity(upperRoot, isCaseInsensitive: true);
            var comparer = new FileSystemPathComparer(fileSystem);

            Assert.Equal(
                comparer.GetHashCode(Path.Combine(upperRoot, "Cache", "One.dll")),
                comparer.GetHashCode(Path.Combine(lowerRoot, "cache", "one.dll")));
            Assert.True(comparer.Equals(
                Path.Combine(upperRoot, "Cache", "One.dll"),
                Path.Combine(lowerRoot, "cache", "one.dll")));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestCaseInsensitiveUncRootSpellingsCompareEqual()
        {
            if (Path.DirectorySeparatorChar != '\\')
            {
                return;
            }

            const string upperRoot = @"\\SERVER\SHARE\";
            const string lowerRoot = @"\\server\share\";
            var fileSystem = new InMemoryFileSystem(isCaseInsensitive: true)
                .WithDirectory(upperRoot)
                .WithCaseSensitivity(upperRoot, isCaseInsensitive: true);
            var comparer = new FileSystemPathComparer(fileSystem);

            Assert.True(comparer.Equals(
                Path.Combine(upperRoot, "Cache", "One.dll"),
                Path.Combine(lowerRoot, "cache", "one.dll")));
            Assert.Equal(
                comparer.GetHashCode(Path.Combine(upperRoot, "Cache", "One.dll")),
                comparer.GetHashCode(Path.Combine(lowerRoot, "cache", "one.dll")));
        }
    }
}

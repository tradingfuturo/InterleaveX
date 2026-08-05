// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Tools.Tests
{
    /// <summary>
    /// Keeps the three places that name a release version from drifting apart.
    /// </summary>
    /// <remarks>
    /// <para>The version is written down three times — in the build, in the citation metadata and in the
    /// history — and nothing until now compared them. Nothing in <c>Scripts/</c>, in CI, or in any test
    /// reads <c>CITATION.cff</c> at all, so a bump that updates two of the three is published exactly as
    /// if it were consistent.</para>
    /// <para>What this deliberately does NOT check is the release DATE, and it is worth being straight
    /// about why: <c>History.md</c> records no dates against its release headings, so there is no second
    /// witness to compare <c>date-released</c> against. Asserting it would mean inventing the ground
    /// truth this test is supposed to check. That date is therefore still guarded by review alone; giving
    /// the history headings dates would be the way to close it, and is a change to make deliberately
    /// rather than as a side effect of a test.</para>
    /// </remarks>
    public class ReleaseMetadataTests : BaseToolsTest
    {
        public ReleaseMetadataTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 15000)]
        public void TestReleaseVersionsAgree()
        {
            string root = FindRepositoryRoot(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));

            string build = XDocument.Load(Path.Combine(root, "Common", "version.props"))
                .Descendants("VersionPrefix").Select(node => node.Value.Trim()).FirstOrDefault();
            Assert.False(string.IsNullOrEmpty(build), "Common/version.props declares no VersionPrefix.");

            // The top-level key only. 'cff-version' on the first line and the nested 'version' of the
            // upstream Microsoft Coyote reference are both decoys, and the latter is indented.
            string citation = MatchOne(
                Path.Combine(root, "CITATION.cff"), @"^version:\s*(\S+)\s*$", "CITATION.cff");

            // The newest release heading, which is the first one in the file.
            string history = MatchOne(
                Path.Combine(root, "History.md"), @"^###\s+v(\S+)\s+\(InterleaveX\)", "History.md");

            Assert.True(
                build == citation && build == history,
                $"The release version does not agree across the repository: Common/version.props says " +
                $"'{build}', CITATION.cff says '{citation}', and the newest History.md heading says " +
                $"'{history}'. A release that updates only some of these publishes as if it were consistent.");
        }

        private static string MatchOne(string path, string pattern, string description)
        {
            var regex = new Regex(pattern, RegexOptions.Multiline);
            Match match = regex.Match(File.ReadAllText(path));
            Assert.True(match.Success, $"Could not find a version in {description} using '{pattern}'.");
            return match.Groups[1].Value.Trim();
        }

        private static string FindRepositoryRoot(string start)
        {
            for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "InterleaveX.sln")))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException("Could not find the InterleaveX repository root.");
        }
    }
}

// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System.IO;
using Microsoft.Coyote.SystematicTesting;
using Microsoft.Coyote.Testing;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Tools.Tests
{
    public class TestReportAggregationTests : BaseToolsTest
    {
        public TestReportAggregationTests(ITestOutputHelper output)
            : base(output)
        {
        }

        private static TestReport CreateReport(string assembly, int paths, params string[] exploredPaths)
        {
            Configuration configuration = Configuration.Create();
            configuration.AssemblyToBeAnalyzed = assembly;
            var report = new TestReport(configuration);
            for (int idx = 0; idx < paths; idx++)
            {
                ((ITestReport)report).SetSchedulingStatistics(false, null, 2, 2, 1, 10, false, true);
            }

            foreach (string path in exploredPaths)
            {
                report.CoverageInfo.DeclareExploredExecutionPath(path);
            }

            return report;
        }

        [Fact(Timeout = 5000)]
        public void TestReportRoundTripsThroughAFile()
        {
            TestReport report = CreateReport("App.dll", 3, "aaaa", "bbbb");
            string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            try
            {
                report.Save(path);
                TestReport loaded = TestReport.Load(path);

                Assert.Equal(report.NumOfExploredFairPaths, loaded.NumOfExploredFairPaths);
                Assert.Equal(report.TotalControlledOperations, loaded.TotalControlledOperations);
                Assert.Equal(report.CoverageInfo.ExploredPaths.Count, loaded.CoverageInfo.ExploredPaths.Count);
                Assert.Contains("aaaa", loaded.CoverageInfo.ExploredPaths);

                // The serializer bypasses constructors, so a loaded report must still have
                // had its non-serialized state restored or merging it throws.
                Assert.True(loaded.Merge(CreateReport("App.dll", 1)));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact(Timeout = 5000)]
        public void TestMergedPathsSumAndUniquePathsAreDeduplicated()
        {
            // Three shards, each exploring 100 paths, with overlapping explored path sets.
            TestReport aggregate = CreateReport("App.dll", 100, "aaaa", "bbbb");
            Assert.True(aggregate.Merge(CreateReport("App.dll", 100, "bbbb", "cccc")));
            Assert.True(aggregate.Merge(CreateReport("App.dll", 100, "cccc", "dddd")));

            Assert.Equal(300, aggregate.NumOfExploredFairPaths + aggregate.NumOfExploredUnfairPaths);

            // The unique count is a genuine union across shards, not a sum.
            Assert.Equal(4, aggregate.CoverageInfo.ExploredPaths.Count);

            string text = aggregate.GetText(Configuration.Create(), "...");
            Assert.Contains("Explored 300 execution paths", text);
            Assert.Contains("4 unique", text);
        }

        [Fact(Timeout = 5000)]
        public void TestReportsForDifferentAssembliesDoNotMerge()
        {
            TestReport aggregate = CreateReport("App.dll", 10);
            Assert.False(aggregate.Merge(CreateReport("Other.dll", 10)));
        }
    }
}

// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System.Threading.Tasks;
using Microsoft.Coyote.SystematicTesting;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// Tests for the scheduling statistics reported by <see cref="TestReport"/>.
    /// </summary>
    /// <remarks>
    /// These statistics are derived from the complete set of operations the runtime registered
    /// during an iteration, including the ones that already completed. They are asserted here
    /// because they are otherwise unverified: the reported numbers are only ever rendered into the
    /// human-readable report, so a change that dropped completed operations from the runtime's
    /// bookkeeping would deflate every one of them without failing any other test.
    /// <para>
    /// The exact totals are specific to <see cref="Seed"/>. The accompanying structural assertions
    /// are not, and are the ones that actually encode the invariant worth protecting.
    /// </para>
    /// </remarks>
    public class TestReportStatisticsTests : BaseBugFindingTest
    {
        public TestReportStatisticsTests(ITestOutputHelper output)
            : base(output)
        {
        }

        private const uint Iterations = 20;

        /// <summary>
        /// The seed is fixed because several of the reported statistics (the concurrency degree in
        /// particular) depend on the schedules actually explored, and would otherwise vary between
        /// runs.
        /// </summary>
        private const uint Seed = 199;

        /// <summary>
        /// The number of operations the program below always creates: one for the test entry point
        /// and one per task.
        /// </summary>
        private const int BaseOperationCount = 4;

        private static Configuration GetStatisticsConfiguration() => Configuration.Create()
            .WithTelemetryEnabled(false)
            .WithPartiallyControlledConcurrencyAllowed(false)
            .WithTestingIterations(Iterations)
            .WithRandomGeneratorSeed(Seed)
            .WithTestIterationsRunToCompletion();

        /// <summary>
        /// A program with a fixed concurrency structure: the main operation plus three tasks, each
        /// with a synchronous body. Depending on how 'Task.WhenAll' is scheduled an extra
        /// continuation operation may or may not be created, so the operation count is either
        /// <see cref="BaseOperationCount"/> or one more.
        /// </summary>
        private static async Task RunThreeIndependentTasks()
        {
            int value = 0;
            object mutex = new object();
            var t1 = Task.Run(() =>
            {
                lock (mutex)
                {
                    value += 1;
                }
            });

            var t2 = Task.Run(() =>
            {
                lock (mutex)
                {
                    value += 2;
                }
            });

            var t3 = Task.Run(() =>
            {
                lock (mutex)
                {
                    value += 3;
                }
            });

            await Task.WhenAll(t1, t2, t3);
        }

        [Fact(Timeout = 30000)]
        public void TestControlledOperationStatistics()
        {
            TestReport report = this.RunSystematicTest(RunThreeIndependentTasks, GetStatisticsConfiguration());

            // Every iteration registers at least the main operation and the three tasks. This is
            // the assertion that fails if completed operations stop being counted.
            Assert.True(report.MinControlledOperations >= BaseOperationCount,
                $"Expected at least {BaseOperationCount} operations per iteration, but the minimum was " +
                $"{report.MinControlledOperations}.");
            Assert.True(report.MaxControlledOperations >= report.MinControlledOperations,
                "The maximum operation count was below the minimum.");
            Assert.InRange(report.TotalControlledOperations,
                report.MinControlledOperations * (int)Iterations,
                report.MaxControlledOperations * (int)Iterations);

            Assert.Equal(4, report.MinControlledOperations);
            Assert.Equal(5, report.MaxControlledOperations);
            Assert.Equal(99, report.TotalControlledOperations);
        }

        [Fact(Timeout = 30000)]
        public void TestConcurrencyDegreeStatistics()
        {
            TestReport report = this.RunSystematicTest(RunThreeIndependentTasks, GetStatisticsConfiguration());

            // The concurrency degree is the high-water mark of simultaneously enabled operations in
            // an iteration, so it is positive and can never exceed the operation count.
            Assert.True(report.MinConcurrencyDegree > 0,
                $"Expected a positive minimum concurrency degree, but found {report.MinConcurrencyDegree}.");
            Assert.True(report.MinConcurrencyDegree <= report.MaxConcurrencyDegree,
                "The minimum concurrency degree exceeded the maximum.");
            Assert.True(report.MaxConcurrencyDegree <= report.MaxControlledOperations,
                "The concurrency degree exceeded the number of controlled operations.");
            Assert.InRange(report.TotalConcurrencyDegree,
                report.MinConcurrencyDegree * (int)Iterations,
                report.MaxConcurrencyDegree * (int)Iterations);

            Assert.Equal(3, report.MinConcurrencyDegree);
            Assert.Equal(4, report.MaxConcurrencyDegree);
            Assert.Equal(73, report.TotalConcurrencyDegree);
        }

        [Fact(Timeout = 30000)]
        public void TestOperationGroupingDegreeStatistics()
        {
            TestReport report = this.RunSystematicTest(RunThreeIndependentTasks, GetStatisticsConfiguration());

            // Each task started with 'Task.Run' owns its own operation group, alongside the group
            // owned by the main operation. A group is only counted while at least one of its
            // operations is still registered, so this also deflates if completed operations are
            // dropped.
            Assert.True(report.MinOperationGroupingDegree > 0,
                $"Expected a positive minimum grouping degree, but found {report.MinOperationGroupingDegree}.");
            Assert.True(report.MaxOperationGroupingDegree <= report.MaxControlledOperations,
                "The grouping degree exceeded the number of controlled operations.");
            Assert.InRange(report.TotalOperationGroupingDegree,
                report.MinOperationGroupingDegree * (int)Iterations,
                report.MaxOperationGroupingDegree * (int)Iterations);

            Assert.Equal(BaseOperationCount, report.MinOperationGroupingDegree);
            Assert.Equal(BaseOperationCount, report.MaxOperationGroupingDegree);
            Assert.Equal(BaseOperationCount * (int)Iterations, report.TotalOperationGroupingDegree);
        }

        [Fact(Timeout = 30000)]
        public void TestExploredPathStatistics()
        {
            TestReport report = this.RunSystematicTest(RunThreeIndependentTasks, GetStatisticsConfiguration());

            // The default portfolio mixes fair and unfair strategies, but every iteration must be
            // accounted for as exactly one explored path.
            Assert.Equal((int)Iterations, report.NumOfExploredFairPaths + report.NumOfExploredUnfairPaths);
            Assert.Equal(0, report.NumOfFoundBugs);

            // Each explored path contributes its trace digest, so the number of distinct digests
            // can never exceed the number of iterations.
            Assert.True(report.CoverageInfo.ExploredPaths.Count <= (int)Iterations,
                $"Recorded {report.CoverageInfo.ExploredPaths.Count} distinct paths across {Iterations} iterations.");
            Assert.True(report.CoverageInfo.ExploredPaths.Count > 1,
                "Expected the exploration to discover more than one distinct schedule.");

            Assert.Equal(16, report.NumOfExploredFairPaths);
            Assert.Equal(4, report.NumOfExploredUnfairPaths);
            Assert.Equal(20, report.CoverageInfo.ExploredPaths.Count);
        }
    }
}

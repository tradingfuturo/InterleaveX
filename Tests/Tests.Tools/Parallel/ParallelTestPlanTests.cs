// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using Microsoft.Coyote.SystematicTesting;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Tools.Tests
{
    public class ParallelTestPlanTests : BaseToolsTest
    {
        public ParallelTestPlanTests(ITestOutputHelper output)
            : base(output)
        {
        }

        /// <summary>
        /// The number of strategies the interleaving portfolio rotates through, which is
        /// what shard sizes must be a multiple of to stay aligned with a sequential run.
        /// </summary>
        private const uint InterleavingPortfolioSize = 5;

        private static Configuration GetPortfolioConfiguration(uint iterations) =>
            Configuration.Create().WithTestingIterations(iterations);

        private static Configuration GetSingleStrategyConfiguration(uint iterations) =>
            Configuration.Create().WithTestingIterations(iterations).WithRandomStrategy();

        private static void AssertShardsCoverExactly(IReadOnlyList<ParallelTestPlan.Shard> shards,
            uint baseSeed, uint iterations)
        {
            Assert.Equal(iterations, (uint)shards.Sum(s => (long)s.Iterations));
            Assert.DoesNotContain(shards, s => s.Iterations is 0);

            // Every seed the sequential run would have used is covered exactly once.
            var seeds = new HashSet<uint>();
            foreach (var shard in shards)
            {
                for (uint idx = 0; idx < shard.Iterations; idx++)
                {
                    Assert.True(seeds.Add(shard.Seed + idx), "A seed was assigned to more than one shard.");
                }
            }

            for (uint idx = 0; idx < iterations; idx++)
            {
                Assert.Contains(baseSeed + idx, seeds);
            }
        }

        [Fact(Timeout = 5000)]
        public void TestShardsCoverEveryIterationExactlyOnce()
        {
            var shards = ParallelTestPlan.Compute(GetPortfolioConfiguration(1000), 4711, 8);
            Assert.Equal(8, shards.Count);
            AssertShardsCoverExactly(shards, 4711, 1000);
        }

        [Fact(Timeout = 5000)]
        public void TestShardSizesArePortfolioAligned()
        {
            var shards = ParallelTestPlan.Compute(GetPortfolioConfiguration(1000), 0, 8);

            // Every shard but the last covers whole rotations of the portfolio, so each
            // worker's local iteration index selects the same strategy as it would have in
            // a sequential run.
            for (int idx = 0; idx < shards.Count - 1; idx++)
            {
                Assert.Equal(0u, shards[idx].Iterations % InterleavingPortfolioSize);
            }
        }

        [Fact(Timeout = 5000)]
        public void TestShardsAreNotSmallerThanOnePortfolioRotation()
        {
            // Seven iterations cannot usefully be split eight ways when the portfolio has
            // five strategies, so only two shards should be produced.
            var shards = ParallelTestPlan.Compute(GetPortfolioConfiguration(7), 100, 8);
            Assert.Equal(2, shards.Count);
            Assert.Equal(5u, shards[0].Iterations);
            Assert.Equal(2u, shards[1].Iterations);
            Assert.Equal(100u, shards[0].Seed);
            Assert.Equal(105u, shards[1].Seed);
            AssertShardsCoverExactly(shards, 100, 7);
        }

        [Fact(Timeout = 5000)]
        public void TestSingleIterationProducesOneShard()
        {
            var shards = ParallelTestPlan.Compute(GetPortfolioConfiguration(1), 42, 8);
            Assert.Single(shards);
            Assert.Equal(42u, shards[0].Seed);
            Assert.Equal(1u, shards[0].Iterations);
        }

        [Fact(Timeout = 5000)]
        public void TestSingleWorkerMatchesSequentialRun()
        {
            var shards = ParallelTestPlan.Compute(GetPortfolioConfiguration(1000), 42, 1);
            Assert.Single(shards);
            Assert.Equal(42u, shards[0].Seed);
            Assert.Equal(1000u, shards[0].Iterations);
        }

        [Fact(Timeout = 5000)]
        public void TestExplicitStrategyIsNotPortfolioAligned()
        {
            // With an explicit strategy the portfolio is disabled, so there is no rotation
            // to stay aligned with and the split can be even.
            var shards = ParallelTestPlan.Compute(GetSingleStrategyConfiguration(1000), 0, 8);
            Assert.Equal(8, shards.Count);
            Assert.All(shards, s => Assert.Equal(125u, s.Iterations));
            AssertShardsCoverExactly(shards, 0, 1000);
        }

        [Fact(Timeout = 5000)]
        public void TestIterationsNotDivisibleByWorkerCount()
        {
            var shards = ParallelTestPlan.Compute(GetPortfolioConfiguration(1000), 7, 3);
            AssertShardsCoverExactly(shards, 7, 1000);
        }

        [Fact(Timeout = 5000)]
        public void TestSeedsNearMaxValueDoNotCollide()
        {
            uint baseSeed = uint.MaxValue - 20;
            var shards = ParallelTestPlan.Compute(GetPortfolioConfiguration(40), baseSeed, 4);
            Assert.Equal(shards.Count, shards.Select(s => s.Seed).Distinct().Count());
            Assert.Equal(40u, (uint)shards.Sum(s => (long)s.Iterations));
        }

        [Fact(Timeout = 5000)]
        public void TestTimeoutModeUsesSeparatedSeedOrigins()
        {
            Configuration configuration = Configuration.Create().WithTestingIterations(10).WithTestingTimeout(60);
            var shards = ParallelTestPlan.Compute(configuration, 0, 8);
            Assert.Equal(8, shards.Count);

            var seeds = shards.Select(s => s.Seed).OrderBy(s => s).ToArray();
            for (int idx = 1; idx < seeds.Length; idx++)
            {
                // Widely separated, so two workers would have to run hundreds of millions of
                // iterations before their seed ranges could meet.
                Assert.True(seeds[idx] - seeds[idx - 1] > 100_000_000,
                    $"Seed origins {seeds[idx - 1]} and {seeds[idx]} are too close.");
            }
        }

        [Fact(Timeout = 5000)]
        public void TestChildArgsOverrideAndStripParallel()
        {
            string[] original = { "test", "App.dll", "-i", "1000", "--parallel", "8" };
            var shard = new ParallelTestPlan.Shard(0, 4711, 125);
            string[] args = ParallelTestPlan.BuildChildArgs(original, "MyTest", shard, "out");

            Assert.Equal("test", args[0]);
            Assert.Equal("App.dll", args[1]);
            AssertOptionValue(args, "-m", "MyTest");
            AssertOptionValue(args, "-i", "125");
            AssertOptionValue(args, "--seed", "4711");
            AssertOptionValue(args, "-o", "out");
            AssertNoParallelOption(args);
        }

        [Fact(Timeout = 5000)]
        public void TestChildArgsStripAttachedOptionValues()
        {
            string[] original = { "test", "App.dll", "--seed=99", "--parallel:4", "-i=500" };
            var shard = new ParallelTestPlan.Shard(1, 5000, 250);
            string[] args = ParallelTestPlan.BuildChildArgs(original, "MyTest", shard, "out");

            Assert.DoesNotContain("--seed=99", args);
            Assert.DoesNotContain("-i=500", args);
            AssertOptionValue(args, "--seed", "5000");
            AssertOptionValue(args, "-i", "250");
            AssertNoParallelOption(args);
        }

        [Fact(Timeout = 5000)]
        public void TestChildArgsPassThroughUnknownOptions()
        {
            string[] original =
            {
                "test", "App.dll", "-s", "probabilistic", "-sv", "5", "--skip-lock-races",
                "-v", "debug", "--parallel", "4"
            };

            var shard = new ParallelTestPlan.Shard(0, 1, 10);
            string[] args = ParallelTestPlan.BuildChildArgs(original, "MyTest", shard, "out");

            AssertOptionValue(args, "-s", "probabilistic");
            AssertOptionValue(args, "-sv", "5");
            AssertOptionValue(args, "-v", "debug");
            Assert.Contains("--skip-lock-races", args);
            AssertNoParallelOption(args);
        }

        [Fact(Timeout = 5000)]
        public void TestChildArgsStripDebuggerAndUserOverrides()
        {
            string[] original = { "test", "App.dll", "-b", "-m", "Other", "-o", "C:\\out", "--parallel", "2" };
            var shard = new ParallelTestPlan.Shard(0, 1, 10);
            string[] args = ParallelTestPlan.BuildChildArgs(original, "MyTest", shard, "worker");

            Assert.DoesNotContain("-b", args);
            Assert.DoesNotContain("Other", args);
            Assert.DoesNotContain("C:\\out", args);
            AssertOptionValue(args, "-m", "MyTest");
            AssertOptionValue(args, "-o", "worker");
            AssertNoParallelOption(args);
        }

        private static void AssertOptionValue(string[] args, string option, string value)
        {
            int idx = System.Array.IndexOf(args, option);
            Assert.True(idx >= 0, $"Option '{option}' is missing.");
            Assert.Equal(value, args[idx + 1]);
            Assert.Equal(idx, System.Array.LastIndexOf(args, option));
        }

        private static void AssertNoParallelOption(string[] args) =>
            Assert.DoesNotContain(args, a =>
                a is "-p" || a is "--parallel" || a.StartsWith("--parallel=") || a.StartsWith("--parallel:"));
    }
}

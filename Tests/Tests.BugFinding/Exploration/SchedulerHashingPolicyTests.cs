// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Coyote.Logging;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Specifications;
using Microsoft.Coyote.SystematicTesting;
using Microsoft.Coyote.Testing;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// Tests for the policy that decides when the runtime computes the program state implicitly.
    /// </summary>
    /// <remarks>
    /// Computing that state walks every registered operation and every specification monitor at
    /// every scheduling point, so it is only worth doing for an iteration whose strategy reads the
    /// result. Q-learning is the only such strategy, and under the default portfolio it runs one
    /// iteration in five, so the policy has to be per iteration rather than per run.
    /// <para>
    /// These assertions are on the scheduler rather than on <see cref="Configuration"/>, because the
    /// configuration alone cannot express "this iteration needs it". A test that only checked the
    /// configuration helper would still pass if the per-iteration gate were removed entirely.
    /// </para>
    /// </remarks>
    public class SchedulerHashingPolicyTests : BaseBugFindingTest
    {
        public SchedulerHashingPolicyTests(ITestOutputHelper output)
            : base(output)
        {
        }

        /// <summary>
        /// The number of strategies in the default interleaving portfolio. Q-learning is last, so
        /// it runs on the final iteration of each rotation.
        /// </summary>
        private const uint PortfolioSize = 5;

        /// <summary>
        /// Deliberately not the base class helper, which lowers verbosity and disables several race
        /// checks. These tests need the shipped defaults, since the portfolio composition and the
        /// rotation are exactly what is under test.
        /// </summary>
        private static Configuration CreateConfiguration() => Configuration.Create()
            .WithTelemetryEnabled(false)
            .WithPartiallyControlledConcurrencyAllowed(false);

        /// <summary>
        /// Returns whether implicit hashing is enabled for each of the specified iterations, by
        /// driving the scheduler through them exactly as the testing engine would.
        /// </summary>
        private static List<bool> GetHashingPerIteration(Configuration configuration, uint iterations)
        {
            var scheduler = OperationScheduler.Setup(configuration, ExecutionTrace.Create());
            var logWriter = new LogWriter(configuration);

            var result = new List<bool>();
            for (uint iteration = 0; iteration < iterations; ++iteration)
            {
                scheduler.InitializeNextIteration(iteration, logWriter);
                result.Add(scheduler.IsImplicitProgramStateHashingEnabled);
            }

            return result;
        }

        [Fact(Timeout = 5000)]
        public void TestSchedulerSetupDoesNotMutateConfiguration()
        {
            // The scheduler used to turn implicit hashing on by writing to the configuration it was
            // handed, which made a run-wide, caller-visible change out of a per-iteration decision.
            var configuration = CreateConfiguration();
            Assert.False(configuration.IsImplicitProgramStateHashingEnabled,
                "Expected implicit program-state hashing to be disabled by default.");

            OperationScheduler.Setup(configuration, ExecutionTrace.Create());

            Assert.False(configuration.IsImplicitProgramStateHashingEnabled,
                "Setting up the scheduler must not enable implicit hashing on the caller's configuration.");
        }

        [Fact(Timeout = 5000)]
        public void TestDefaultPortfolioHashesOnlyTheQLearningIteration()
        {
            // Two full rotations, to show the pattern repeats rather than being a one-off.
            var hashing = GetHashingPerIteration(CreateConfiguration(), PortfolioSize * 2);

            for (int iteration = 0; iteration < hashing.Count; ++iteration)
            {
                bool isQLearningIteration = (iteration + 1) % PortfolioSize is 0;
                Assert.True(hashing[iteration] == isQLearningIteration,
                    $"Iteration {iteration} expected hashing={isQLearningIteration}, got {hashing[iteration]}.");
            }

            // Stated separately so a regression that flipped every iteration on cannot be mistaken
            // for the intended pattern.
            Assert.Equal(2, hashing.FindAll(h => h).Count);
        }

        [Fact(Timeout = 5000)]
        public void TestExplicitQLearningStrategyHashesEveryIteration()
        {
            var configuration = CreateConfiguration().WithQLearningStrategy();
            var hashing = GetHashingPerIteration(configuration, PortfolioSize);
            Assert.All(hashing, h => Assert.True(h,
                "Explicit q-learning must hash on every iteration."));
        }

        [Fact(Timeout = 5000)]
        public void TestRandomStrategyWithTraceAnalysisDoesNotHash()
        {
            // Trace analysis records the hashed state on each graph node, but nothing ever reads it
            // back, so it must not be a reason to compute the state.
            var configuration = CreateConfiguration()
                .WithRandomStrategy()
                .WithTraceAnalysisEnabled();

            var hashing = GetHashingPerIteration(configuration, PortfolioSize);
            Assert.All(hashing, h => Assert.False(h,
                "Trace analysis must not enable implicit program-state hashing."));
        }

        [Fact(Timeout = 5000)]
        public void TestExplicitConfigurationOverridesStrategyRequirement()
        {
            // A strategy that does not need the state must still get it when the user asks, since
            // the visited-state coverage count is computed from it.
            var configuration = CreateConfiguration().WithRandomStrategy();
            configuration.IsImplicitProgramStateHashingEnabled = true;

            var hashing = GetHashingPerIteration(configuration, PortfolioSize);
            Assert.All(hashing, h => Assert.True(h,
                "An explicitly enabled configuration must hash regardless of the strategy."));
        }

        [Fact(Timeout = 5000)]
        public void TestPortfolioWithExplicitHashingHashesEveryIteration()
        {
            var configuration = CreateConfiguration();
            configuration.IsImplicitProgramStateHashingEnabled = true;

            var hashing = GetHashingPerIteration(configuration, PortfolioSize * 2);
            Assert.All(hashing, h => Assert.True(h,
                "An explicitly enabled configuration must hash on every portfolio iteration."));
        }

        /// <summary>
        /// A registered state-hashing function is the user computing state for their own purposes,
        /// so it must keep running on every iteration no matter which strategy is active.
        /// </summary>
        [Fact(Timeout = 60000)]
        public void TestRegisteredStateHashingFunctionRunsOnEveryIteration()
        {
            const uint Iterations = PortfolioSize * 2;

            var iterationsThatHashed = new HashSet<uint>();
            uint current = 0;

            // Seeded like every other test in the suite. 'CreateConfiguration' deliberately avoids
            // the base class helper, which is also where the default seed is applied, so without
            // this the one test here that runs a real engine would explore from a fresh seed on
            // every run and a failure could not be reproduced -- the very thing the per-test seeds
            // exist to guarantee.
            var configuration = this.WithDefaultRandomSeed(CreateConfiguration()
                .WithTestingIterations(Iterations)
                .WithTestIterationsRunToCompletion());

            var logWriter = new LogWriter(configuration);
            using var engine = new TestingEngine(configuration, () =>
            {
                Specification.RegisterStateHashingFunction(() =>
                {
                    iterationsThatHashed.Add(current);
                    return 0;
                });

                return RunTwoTasks();
            }, logWriter);

            engine.RegisterStartIterationCallBack(iteration => current = iteration);
            engine.Run();

            Assert.Equal(0, engine.TestReport.NumOfFoundBugs);
            Assert.Equal((int)Iterations, iterationsThatHashed.Count);
        }

        /// <summary>
        /// Gating implicit hashing must change the count of distinct visited states and nothing
        /// else. Every other reported statistic, and the explored schedules themselves, must be
        /// identical whether or not the non-consuming portfolio strategies compute the state.
        /// </summary>
        /// <remarks>
        /// The pre-gating behaviour is still reachable, by asking for implicit hashing explicitly,
        /// which is what makes this comparison possible inside a single build. Both arms run the
        /// same programs under the same seed, so any difference is attributable to the gate alone.
        /// </remarks>
        [Theory(Timeout = 120000)]
        [InlineData("race")]
        [InlineData("nondet")]
        [InlineData("readwrite")]
        public void TestGatingChangesOnlyTheVisitedStateCount(string program)
        {
            foreach (var mode in new[] { PortfolioMode.Fair, PortfolioMode.Unfair })
            {
                var gated = RunPortfolio(program, mode, isHashingForced: false);
                var ungated = RunPortfolio(program, mode, isHashingForced: true);

                Assert.Equal(ungated.Traces, gated.Traces);
                Assert.Equal(ungated.Bugs, gated.Bugs);
                Assert.Equal(ungated.FairPaths, gated.FairPaths);
                Assert.Equal(ungated.UnfairPaths, gated.UnfairPaths);
                Assert.Equal(ungated.Operations, gated.Operations);
                Assert.Equal(ungated.ConcurrencyDegree, gated.ConcurrencyDegree);
                Assert.Equal(ungated.GroupingDegree, gated.GroupingDegree);

                // The one intended difference. Asserted as an inequality rather than pinned to a
                // value, because the exact count depends on per-process string hash codes.
                Assert.True(gated.VisitedStates < ungated.VisitedStates,
                    $"{program}/{mode}: expected gating to record fewer visited states, " +
                    $"got {gated.VisitedStates} gated vs {ungated.VisitedStates} ungated.");
            }
        }

        private static (string Traces, int Bugs, int FairPaths, int UnfairPaths, int VisitedStates,
            int Operations, int ConcurrencyDegree, int GroupingDegree) RunPortfolio(
            string program, PortfolioMode mode, bool isHashingForced)
        {
            var configuration = CreateConfiguration()
                .WithTestingIterations(20)
                .WithRandomGeneratorSeed(199)
                .WithTestIterationsRunToCompletion();
            configuration.PortfolioMode = mode;
            if (isHashingForced)
            {
                configuration.IsImplicitProgramStateHashingEnabled = true;
            }

            var logWriter = new LogWriter(configuration);
            using var engine = new TestingEngine(configuration, PortfolioPrograms[program], logWriter);

            var digests = new List<string>();
            engine.RegisterEndIterationCallBack(_ => digests.Add(engine.Scheduler.Trace.GetDigest()));
            engine.Run();

            var report = engine.TestReport;
            return (string.Join(",", digests), report.NumOfFoundBugs, report.NumOfExploredFairPaths,
                report.NumOfExploredUnfairPaths, report.CoverageInfo.VisitedStates.Count,
                report.TotalControlledOperations, report.TotalConcurrencyDegree,
                report.TotalOperationGroupingDegree);
        }

        private static readonly Dictionary<string, System.Func<Task>> PortfolioPrograms =
            new Dictionary<string, System.Func<Task>>
            {
                ["race"] = RunTwoTasks,
                ["nondet"] = RunNondeterministicChoices,
                ["readwrite"] = RunSharedStateAccesses,
            };

        private static async Task RunNondeterministicChoices()
        {
            var generator = Random.Generator.Create();
            int total = 0;
            var t1 = Task.Run(() =>
            {
                if (generator.NextBoolean())
                {
                    total += generator.NextInteger(4);
                }
            });

            var t2 = Task.Run(() =>
            {
                total += generator.NextInteger(3);
            });

            await Task.WhenAll(t1, t2);
        }

        private static async Task RunSharedStateAccesses()
        {
            int shared = 0;
            var t1 = Task.Run(() =>
            {
                SchedulingPoint.Read("shared");
                int local = shared;
                SchedulingPoint.Write("shared");
                shared = local + 1;
            });

            var t2 = Task.Run(() =>
            {
                SchedulingPoint.Read("shared");
                int local = shared;
                SchedulingPoint.Write("shared");
                shared = local + 1;
            });

            await Task.WhenAll(t1, t2);
        }

        private static async Task RunTwoTasks()
        {
            int shared = 0;
            var t1 = Task.Run(() =>
            {
                int local = shared;
                SchedulingPoint.Interleave();
                shared = local + 1;
            });

            var t2 = Task.Run(() =>
            {
                int local = shared;
                SchedulingPoint.Interleave();
                shared = local + 1;
            });

            await Task.WhenAll(t1, t2);
        }
    }
}

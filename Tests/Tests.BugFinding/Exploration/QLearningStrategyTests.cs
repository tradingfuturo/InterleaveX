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
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// Tests for the Q-learning exploration strategy.
    /// </summary>
    /// <remarks>
    /// Q-learning is the only consumer of implicit program-state hashing: it uses the hashed
    /// program state as the key of its Q-table, and picks the next operation by sampling the
    /// distribution formed by that state's Q-values. That makes it uniquely sensitive both to the
    /// set of operations offered at a scheduling point and to the order in which they are offered,
    /// since the sampled index is resolved back to an operation through that order.
    /// </remarks>
    public class QLearningStrategyTests : BaseBugFindingTest
    {
        public QLearningStrategyTests(ITestOutputHelper output)
            : base(output)
        {
        }

        private const uint Seed = 199;

        private static Configuration GetQLearningConfiguration(uint iterations) => Configuration.Create()
            .WithTelemetryEnabled(false)
            .WithPartiallyControlledConcurrencyAllowed(false)
            .WithQLearningStrategy()
            .WithTestingIterations(iterations)
            .WithRandomGeneratorSeed(Seed);

        /// <summary>
        /// A lost-update race: both tasks read, yield, then write.
        /// </summary>
        private static async Task RunLostUpdateRace()
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
            Specification.Assert(shared is 2, "Lost update: value is {0} instead of 2.", shared);
        }

        /// <summary>
        /// Three tasks contending on a lock, with a nondeterministic choice, so that the strategy
        /// faces scheduling points with more than two candidate operations.
        /// </summary>
        private static async Task RunContendedProgram()
        {
            var generator = Random.Generator.Create();
            int value = 0;
            object mutex = new object();
            var tasks = new List<Task>();
            for (int i = 0; i < 3; i++)
            {
                tasks.Add(Task.Run(() =>
                {
                    lock (mutex)
                    {
                        value++;
                    }

                    if (generator.NextBoolean())
                    {
                        SchedulingPoint.Interleave();
                    }
                }));
            }

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Verifies that Q-learning finds a straightforward concurrency bug and that the trace it
        /// produces replays.
        /// </summary>
        [Fact(Timeout = 60000)]
        public void TestQLearningFindsLostUpdate()
        {
            this.TestWithError(RunLostUpdateRace,
                configuration: GetQLearningConfiguration(200),
                expectedError: "Lost update: value is 1 instead of 2.",
                replay: true);
        }

        /// <summary>
        /// Verifies that Q-learning explores a variety of distinct schedules rather than collapsing
        /// onto a single one.
        /// </summary>
        /// <remarks>
        /// This is the assertion that catches a selection bug: if the membership test that filters
        /// the Q-table down to the currently enabled operations were broken, or if the sampled
        /// index were resolved against a differently ordered list, the strategy would still run but
        /// would stop covering the schedule space.
        /// </remarks>
        [Fact(Timeout = 60000)]
        public void TestQLearningExploresDistinctSchedules()
        {
            TestReport report = this.RunSystematicTest(RunContendedProgram,
                GetQLearningConfiguration(50).WithTestIterationsRunToCompletion());

            Assert.True(report.CoverageInfo.ExploredPaths.Count >= 10,
                $"Q-learning explored only {report.CoverageInfo.ExploredPaths.Count} distinct schedules across " +
                "50 iterations, which suggests the strategy is no longer discriminating between operations.");
            Assert.True(report.CoverageInfo.VisitedStates.Count >= 10,
                $"Q-learning visited only {report.CoverageInfo.VisitedStates.Count} distinct program states.");
        }

        /// <summary>
        /// Verifies that Q-learning uses implicit program-state hashing, which is what supplies the
        /// keys of its Q-table.
        /// </summary>
        [Fact(Timeout = 10000)]
        public void TestQLearningEnablesProgramStateHashing()
        {
            var configuration = Configuration.Create().WithQLearningStrategy();
            Assert.True(configuration.IsImplicitProgramStateHashingEnabled,
                "Expected the Q-learning strategy to enable implicit program-state hashing.");
            Assert.Equal(Testing.ExplorationStrategy.QLearning, configuration.ExplorationStrategy);
            Assert.Equal(Testing.PortfolioMode.None, configuration.PortfolioMode);
        }

        /// <summary>
        /// Verifies that Q-learning explores exactly the same sequence of schedules for a fixed
        /// seed, and reports the first divergent iteration when it does not.
        /// </summary>
        /// <remarks>
        /// Unlike the aggregate digest asserted by <c>SchedulingDeterminismTests</c>, this test
        /// keeps the per-iteration traces so that a regression points at the first iteration that
        /// diverged rather than at a single opaque hash.
        /// </remarks>
        [Fact(Timeout = 60000)]
        public void TestQLearningIsDeterministic()
        {
            List<string> first = ExploreTraces(20);
            List<string> second = ExploreTraces(20);

            Assert.Equal(first.Count, second.Count);
            for (int idx = 0; idx < first.Count; idx++)
            {
                Assert.True(first[idx] == second[idx],
                    $"Q-learning diverged at iteration {idx}:" +
                    $"\n  first run:  {first[idx]}\n  second run: {second[idx]}");
            }

            // A strategy that always made the same choice would produce identical traces for every
            // iteration, which would make the comparison above vacuous.
            Assert.True(new HashSet<string>(first).Count > 1,
                "Expected Q-learning to explore more than one distinct trace.");
        }

        /// <summary>
        /// Verifies that restricting the program state to live operations is off by default, and
        /// that turning it on actually changes the computed state.
        /// </summary>
        /// <remarks>
        /// The default must keep every registered operation in the hash, because that is what makes
        /// the exploration reproducible against traces recorded before the option existed. Note
        /// that the option cannot be expected to shrink the reported number of visited states: the
        /// state is a lossy fold rather than an encoding, and because Q-learning keys its table on
        /// that state, changing it makes the strategy explore a different set of schedules
        /// altogether. All that can be asserted is that the option is off by default, that it takes
        /// effect, and that exploration still works with it on.
        /// </remarks>
        [Fact(Timeout = 60000)]
        public void TestLiveOperationStateHashingIsOptIn()
        {
            Assert.False(Configuration.Create().IsLiveOperationStateHashingEnabled,
                "Expected live-operation state hashing to be disabled by default.");
            Assert.True(Configuration.Create().WithLiveOperationStateHashingEnabled().IsLiveOperationStateHashingEnabled);

            TestReport baseline = this.RunSystematicTest(RunContendedProgram,
                GetQLearningConfiguration(30).WithTestIterationsRunToCompletion());
            TestReport liveOnly = this.RunSystematicTest(RunContendedProgram,
                GetQLearningConfiguration(30).WithTestIterationsRunToCompletion()
                    .WithLiveOperationStateHashingEnabled());

            // The option must actually be consulted when computing the program state. Both runs use
            // the same seed and program, so an identical set of visited states would mean the
            // completed operations were never contributing in the first place.
            Assert.False(liveOnly.CoverageInfo.VisitedStates.SetEquals(baseline.CoverageInfo.VisitedStates),
                "Enabling live-operation state hashing did not change the computed program states.");

            // Both configurations must still explore, and neither may report a bug in this program.
            Assert.True(liveOnly.CoverageInfo.VisitedStates.Count > 1,
                "Expected live-operation hashing to still distinguish program states.");
            Assert.True(liveOnly.CoverageInfo.ExploredPaths.Count > 1,
                "Expected live-operation hashing to still explore distinct schedules.");
            Assert.Equal(0, baseline.NumOfFoundBugs);
            Assert.Equal(0, liveOnly.NumOfFoundBugs);
        }

        /// <summary>
        /// Runs the contended program under Q-learning and returns the ordered per-iteration
        /// execution traces.
        /// </summary>
        private static List<string> ExploreTraces(uint iterations)
        {
            var configuration = GetQLearningConfiguration(iterations).WithTestIterationsRunToCompletion();
            var logWriter = new LogWriter(configuration);
            using var engine = new TestingEngine(configuration, RunContendedProgram, logWriter);

            var traces = new List<string>();
            engine.RegisterEndIterationCallBack(_ => traces.Add(engine.Scheduler.Trace.ToString()));
            engine.Run();
            return traces;
        }
    }
}

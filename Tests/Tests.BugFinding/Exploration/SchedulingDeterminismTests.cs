// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Coyote.Logging;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.SystematicTesting;
using Microsoft.Coyote.Testing;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// Golden-file tests that pin the exact sequence of schedules explored by each exploration
    /// strategy for a fixed random seed.
    /// </summary>
    /// <remarks>
    /// These tests exist to guard refactorings of the scheduling hot path. Any change to the order
    /// in which operations are offered to a strategy, to the number or order of random draws, or to
    /// the set of operations a reducer filters out will change one or more of these digests, even
    /// when every other test still passes.
    /// <para>
    /// The digest is deliberately built from the <em>ordered</em> per-iteration execution traces
    /// rather than from <see cref="Coverage.CoverageInfo.ExploredPaths"/>, which is a set and would
    /// therefore not detect a reordering of the schedules explored.
    /// </para>
    /// <para>
    /// To regenerate after an intentional behavioral change, run the test and paste the replacement
    /// table from the failure message over <see cref="GoldenDigests"/>. Never regenerate to make a
    /// red test go green without first explaining why the schedule changed.
    /// </para>
    /// </remarks>
    public class SchedulingDeterminismTests : BaseBugFindingTest
    {
        public SchedulingDeterminismTests(ITestOutputHelper output)
            : base(output)
        {
        }

        /// <summary>
        /// The number of iterations to explore per configuration. Kept small enough to keep the
        /// suite fast, but large enough that the priority/delay change points and the Q-learning
        /// tables actually get exercised across iterations.
        /// </summary>
        private const uint Iterations = 50;

        /// <summary>
        /// The fixed seed. Every digest below is only meaningful for this seed.
        /// </summary>
        private const uint Seed = 199;

        /// <summary>
        /// The expected digest for each '{program}/{strategy}/{reducers}/{hashing}' configuration.
        /// </summary>
        private static readonly Dictionary<string, string> GoldenDigests = new Dictionary<string, string>
        {
            ["race/random/none/all"] = "62f886580f5ca2f1",
            ["race/probabilistic/none/all"] = "5a02470eba177881",
            ["race/prioritization/none/all"] = "ff65710ce4aee49c",
            ["race/fair-prioritization/none/all"] = "970af7c57b17513c",
            ["race/delay-bounding/none/all"] = "7c9496a45aa8e66d",
            ["race/fair-delay-bounding/none/all"] = "8a6eeb4bce2e16dd",
            ["race/q-learning/none/all"] = "b11f031116b10217",
            ["race/dfs/none/all"] = "a349ebc20a23f5cd",
            ["race/portfolio-fair/none/all"] = "9adcf77e3040b2c4",
            ["race/portfolio-unfair/none/all"] = "179f5ba3a21dbb4c",
            ["nondet/random/none/all"] = "5d69593080bde4e8",
            ["nondet/probabilistic/none/all"] = "59e18f552e96309c",
            ["nondet/prioritization/none/all"] = "9a0818d8825b4611",
            ["nondet/fair-prioritization/none/all"] = "eeec703275c6fcd1",
            ["nondet/delay-bounding/none/all"] = "ed3a840132d65e7f",
            ["nondet/fair-delay-bounding/none/all"] = "be723eb7a69a60bf",
            ["nondet/q-learning/none/all"] = "2a82cf382a440efb",
            ["nondet/dfs/none/all"] = "dc857e425faa385a",
            ["nondet/portfolio-fair/none/all"] = "2980a655400c340d",
            ["nondet/portfolio-unfair/none/all"] = "5dec8e09a5f176e9",
            ["readwrite/random/none/all"] = "f98d1f57b06ca07c",
            ["readwrite/probabilistic/none/all"] = "08ad002abe0a7d0b",
            ["readwrite/prioritization/none/all"] = "3f357026302eac94",
            ["readwrite/fair-prioritization/none/all"] = "d785dcb0a8391354",
            ["readwrite/delay-bounding/none/all"] = "8c8801d4a467227f",
            ["readwrite/fair-delay-bounding/none/all"] = "664231a306fadf2f",
            ["readwrite/q-learning/none/all"] = "d0e7e61355c2a4ed",
            ["readwrite/dfs/none/all"] = "47e73d423ab1b80d",
            ["readwrite/portfolio-fair/none/all"] = "aaefcc03fa49618a",
            ["readwrite/portfolio-unfair/none/all"] = "af935d8ed29ae84a",
            ["readwrite/random/cycle/all"] = "7125ecb210116a70",
            ["readwrite/fair-prioritization/cycle/all"] = "88a9e8e1b439c00d",
            ["readwrite/random/partial-order/all"] = "e0b286ae74f4f511",
            ["readwrite/fair-prioritization/partial-order/all"] = "71abfa619e24744b",
            ["readwrite/random/both/all"] = "8f0c03e4854fe0b2",
            ["readwrite/fair-prioritization/both/all"] = "9e8f930f31c01d8f",

            // Live hashing computes the program state from only the operations that have not
            // completed, which coarsens it, so q-learning keys on fewer distinct states and explores
            // a different sequence. Each of these therefore differs from its '/all' counterpart
            // above; a pair that agreed would mean the setting had stopped taking effect.
            ["race/q-learning/none/live"] = "d86476fa34bd9141",
            ["nondet/q-learning/none/live"] = "3fa8acbef452726a",
            ["readwrite/q-learning/none/live"] = "f9125ccb5c988406",
        };

        /// <summary>
        /// The programs under test, keyed by name.
        /// </summary>
        /// <remarks>
        /// Every program must be fully controlled and free of real-time dependencies, otherwise the
        /// digests would not be reproducible from run to run.
        /// </remarks>
        private static readonly Dictionary<string, Func<Task>> Programs = new Dictionary<string, Func<Task>>
        {
            // Two tasks racing on shared state guarded by a lock, plus an unguarded read. Exercises
            // Acquire/Release scheduling points and operation grouping.
            ["race"] = static async () =>
            {
                int value = 0;
                object mutex = new object();
                var t1 = Task.Run(() =>
                {
                    lock (mutex)
                    {
                        value++;
                    }
                });

                var t2 = Task.Run(() =>
                {
                    lock (mutex)
                    {
                        value += 2;
                    }
                });

                var t3 = Task.Run(() => _ = value);
                await Task.WhenAll(t1, t2, t3);
            },

            // Nondeterministic boolean and integer choices interleaved with concurrency. Exercises
            // the NextBoolean/NextInteger paths of every strategy, which are recorded in the trace
            // alongside scheduling decisions.
            ["nondet"] = static async () =>
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
            },

            // Explicit Read/Write scheduling points on named shared state. This is the only program
            // that gives the trace-cycle and partial-order reducers anything to reduce.
            ["readwrite"] = static async () =>
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
                    shared = local + 2;
                });

                var t3 = Task.Run(() =>
                {
                    SchedulingPoint.Read("shared");
                    _ = shared;
                });

                await Task.WhenAll(t1, t2, t3);
            },
        };

        /// <summary>
        /// Applies the named exploration strategy to the specified configuration.
        /// </summary>
        private static void ApplyStrategy(Configuration configuration, string strategy)
        {
            switch (strategy)
            {
                case "random":
                    configuration.WithRandomStrategy();
                    break;
                case "probabilistic":
                    configuration.WithProbabilisticStrategy();
                    break;
                case "prioritization":
                    configuration.WithPrioritizationStrategy(false);
                    break;
                case "fair-prioritization":
                    configuration.WithPrioritizationStrategy(true);
                    break;
                case "delay-bounding":
                    configuration.WithDelayBoundingStrategy(false);
                    break;
                case "fair-delay-bounding":
                    configuration.WithDelayBoundingStrategy(true);
                    break;
                case "q-learning":
                    configuration.WithQLearningStrategy();
                    break;
                case "dfs":
                    configuration.WithDFSStrategy();
                    break;
                case "portfolio-fair":
                    configuration.PortfolioMode = PortfolioMode.Fair;
                    break;
                case "portfolio-unfair":
                    configuration.PortfolioMode = PortfolioMode.Unfair;
                    break;
                default:
                    throw new ArgumentException($"Unknown strategy '{strategy}'.", nameof(strategy));
            }
        }

        /// <summary>
        /// Applies the named reducer combination to the specified configuration.
        /// </summary>
        private static void ApplyReducers(Configuration configuration, string reducers)
        {
            switch (reducers)
            {
                case "none":
                    break;
                case "cycle":
                    configuration.WithExecutionTraceCycleReductionEnabled(true);
                    break;
                case "partial-order":
                    configuration.WithPartialOrderSamplingEnabled(true);
                    break;
                case "both":
                    configuration.WithExecutionTraceCycleReductionEnabled(true);
                    configuration.WithPartialOrderSamplingEnabled(true);
                    break;
                default:
                    throw new ArgumentException($"Unknown reducers '{reducers}'.", nameof(reducers));
            }
        }

        /// <summary>
        /// Runs the specified configuration and returns a digest of the ordered sequence of
        /// execution traces explored, followed by the summary statistics of the run.
        /// </summary>
        private static string ComputeDigest(string program, string strategy, string reducers, string hashing)
        {
            // Race checking is left at its default (enabled) so that the collection, lock, atomic
            // and volatile scheduling points injected by the rewriter are all exercised.
            var configuration = Configuration.Create()
                .WithTelemetryEnabled(false)
                .WithPartiallyControlledConcurrencyAllowed(false)
                .WithTestingIterations(Iterations)
                .WithRandomGeneratorSeed(Seed)
                .WithTestIterationsRunToCompletion();

            if (hashing is "live")
            {
                configuration.WithLiveOperationStateHashingEnabled();
            }
            else if (hashing != "all")
            {
                throw new ArgumentException($"Unknown hashing '{hashing}'.", nameof(hashing));
            }

            // Portfolio mode is the default, so an explicit strategy must turn it off. The
            // per-strategy 'With...Strategy' helpers already do this; the portfolio cases below
            // re-enable it deliberately.
            configuration.PortfolioMode = PortfolioMode.None;
            ApplyStrategy(configuration, strategy);
            ApplyReducers(configuration, reducers);

            var logWriter = new LogWriter(configuration);
            using var engine = new TestingEngine(configuration, Programs[program], logWriter);

            // Record the trace digest of every iteration, in order. The end-of-iteration callback
            // runs after the iteration has detached but before the next one clears the trace.
            var digests = new List<string>();
            engine.RegisterEndIterationCallBack(_ => digests.Add(engine.Scheduler.Trace.GetDigest()));
            engine.Run();

            var report = engine.TestReport;
            var builder = new StringBuilder();
            builder.Append(string.Join(",", digests));
            builder.Append('|').Append(report.NumOfFoundBugs.ToString(CultureInfo.InvariantCulture));
            builder.Append('|').Append(report.NumOfExploredFairPaths.ToString(CultureInfo.InvariantCulture));
            builder.Append('|').Append(report.NumOfExploredUnfairPaths.ToString(CultureInfo.InvariantCulture));
            builder.Append('|').Append(report.CoverageInfo.VisitedStates.Count.ToString(CultureInfo.InvariantCulture));
            builder.Append('|').Append(report.TotalControlledOperations.ToString(CultureInfo.InvariantCulture));
            builder.Append('|').Append(report.TotalConcurrencyDegree.ToString(CultureInfo.InvariantCulture));
            builder.Append('|').Append(report.TotalOperationGroupingDegree.ToString(CultureInfo.InvariantCulture));
            return Hash(builder.ToString());
        }

        /// <summary>
        /// Returns a compact FNV-1a 64-bit digest of the specified value.
        /// </summary>
        private static string Hash(string value)
        {
            ulong hash = 14695981039346656037UL;
            foreach (char c in value)
            {
                hash = (hash ^ c) * 1099511628211UL;
            }

            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The configurations under test. Every exploration strategy is swept across every program
        /// with reduction disabled, and the reducer combinations are swept across the one program
        /// that declares 'READ' and 'WRITE' scheduling points for them to act on.
        /// </summary>
        /// <remarks>
        /// The last component selects which operations the program state is computed from: 'all' of
        /// them, or only the 'live' ones. The live variants are swept over q-learning alone, because
        /// that is the strategy that keys its table on the program state, so it is the only one whose
        /// explored schedules move when the set of hashed operations changes. Without them nothing in
        /// this suite exercises live hashing at all.
        /// </remarks>
        private static IEnumerable<(string Program, string Strategy, string Reducers, string Hashing)> Configurations()
        {
            string[] strategies =
            {
                "random", "probabilistic", "prioritization", "fair-prioritization", "delay-bounding",
                "fair-delay-bounding", "q-learning", "dfs", "portfolio-fair", "portfolio-unfair"
            };

            foreach (string program in new[] { "race", "nondet", "readwrite" })
            {
                foreach (string strategy in strategies)
                {
                    yield return (program, strategy, "none", "all");
                }
            }

            foreach (string reducers in new[] { "cycle", "partial-order", "both" })
            {
                foreach (string strategy in new[] { "random", "fair-prioritization" })
                {
                    yield return ("readwrite", strategy, reducers, "all");
                }
            }

            foreach (string program in new[] { "race", "nondet", "readwrite" })
            {
                yield return (program, "q-learning", "none", "live");
            }
        }

        /// <summary>
        /// Asserts that every configuration explores exactly the sequence of schedules it explored
        /// when the goldens were recorded.
        /// </summary>
        /// <remarks>
        /// All configurations are checked in a single test so that a refactoring which shifts many
        /// of them reports every difference at once, and so that the failure message carries a
        /// ready-to-paste replacement table.
        /// </remarks>
        [Fact(Timeout = 600000)]
        public void TestExplorationIsDeterministic()
        {
            var mismatches = new List<string>();
            var table = new StringBuilder();
            foreach ((string program, string strategy, string reducers, string hashing) in Configurations())
            {
                string key = $"{program}/{strategy}/{reducers}/{hashing}";
                string actual = ComputeDigest(program, strategy, reducers, hashing);
                table.AppendLine($"            [\"{key}\"] = \"{actual}\",");
                if (!GoldenDigests.TryGetValue(key, out string expected))
                {
                    mismatches.Add($"{key}: no golden recorded, actual '{actual}'");
                }
                else if (expected != actual)
                {
                    mismatches.Add($"{key}: expected '{expected}', actual '{actual}'");
                }
            }

            Assert.True(mismatches.Count is 0,
                $"The explored schedules changed for {mismatches.Count} configuration(s):" +
                Environment.NewLine + string.Join(Environment.NewLine, mismatches) +
                Environment.NewLine + Environment.NewLine +
                "If this change is intentional, replace GoldenDigests with:" +
                Environment.NewLine + table.ToString());
        }

        /// <summary>
        /// Verifies that the digest of a configuration is stable across repeated runs in the same
        /// process, which is what makes the golden comparison above meaningful.
        /// </summary>
        /// <remarks>
        /// Stability across separate processes is not checked here, and cannot be from inside one
        /// process, so it is worth recording why it holds. Program state hashing feeds
        /// <c>LastCallSite.GetHashCode()</c> into the state, and .NET randomizes string hash codes per
        /// process, so the state values themselves do differ from run to run. They never reach a
        /// digest: the Q-table keyed by those values is only ever indexed, never enumerated, and the
        /// inner table the strategy does enumerate is keyed by operation id. A per-process relabeling
        /// of state values therefore leaves every scheduling decision, and hence every trace digest,
        /// untouched. The one quantity that could still move is the count of distinct states, which
        /// would need two call sites to collide in one process and not in another.
        /// </remarks>
        [Fact(Timeout = 120000)]
        public void TestDigestIsStableAcrossRuns()
        {
            Assert.Equal(ComputeDigest("race", "fair-prioritization", "none", "all"),
                ComputeDigest("race", "fair-prioritization", "none", "all"));
            Assert.Equal(ComputeDigest("readwrite", "q-learning", "both", "all"),
                ComputeDigest("readwrite", "q-learning", "both", "all"));
            Assert.Equal(ComputeDigest("readwrite", "q-learning", "none", "live"),
                ComputeDigest("readwrite", "q-learning", "none", "live"));
        }
    }
}

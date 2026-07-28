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
    /// Two digests are recorded per configuration, and the split is what makes the goldens
    /// diagnostic rather than merely a tripwire. <see cref="GoldenTraceDigests"/> covers the ordered
    /// traces alone, so it moves if and only if exploration itself changed.
    /// <see cref="GoldenDigests"/> additionally folds in the summary statistics of the run. A change
    /// that leaves the traces alone but moves a reported statistic therefore shows up as a
    /// combined-only mismatch, which a single fused digest could not distinguish from a genuine
    /// change of schedule.
    /// </para>
    /// <para>
    /// To regenerate after an intentional behavioral change, run the test and paste the replacement
    /// tables from the failure message over the two dictionaries. Never regenerate to make a red
    /// test go green without first explaining why the schedule changed. A trace-digest mismatch in
    /// particular is a claim that exploration moved, and needs to be justified as such.
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
        /// The expected digest of the ordered execution traces alone, for each
        /// '{program}/{strategy}/{reducers}/{hashing}' configuration.
        /// </summary>
        /// <remarks>
        /// This table is the exploration invariant. It excludes every summary statistic, so it moves
        /// only when the sequence of scheduling decisions moves. An optimization that is meant to
        /// leave exploration untouched must leave this table byte-identical.
        /// </remarks>
        private static readonly Dictionary<string, string> GoldenTraceDigests = new Dictionary<string, string>
        {
            ["race/random/none/all"] = "89dfb05a78f2f270",
            ["race/probabilistic/none/all"] = "dc6a6c096fab7f03",
            ["race/prioritization/none/all"] = "e8da9549c38b0aa8",
            ["race/fair-prioritization/none/all"] = "e8da9549c38b0aa8",
            ["race/delay-bounding/none/all"] = "cbaba95471be64c5",
            ["race/fair-delay-bounding/none/all"] = "cbaba95471be64c5",
            ["race/q-learning/none/all"] = "d0feb905769b45e2",
            ["race/dfs/none/all"] = "9d4c05bfcf22d98d",
            ["race/portfolio-fair/none/all"] = "535028a4e4fd6e14",
            ["race/portfolio-unfair/none/all"] = "535028a4e4fd6e14",
            ["nondet/random/none/all"] = "ee4eda9960ba70fc",
            ["nondet/probabilistic/none/all"] = "00e0ed802894342a",
            ["nondet/prioritization/none/all"] = "349cb262aef42fac",
            ["nondet/fair-prioritization/none/all"] = "349cb262aef42fac",
            ["nondet/delay-bounding/none/all"] = "e341ad6e333fc670",
            ["nondet/fair-delay-bounding/none/all"] = "e341ad6e333fc670",
            ["nondet/q-learning/none/all"] = "1ce82532a88bc8ed",
            ["nondet/dfs/none/all"] = "723de41b119a3b9f",
            ["nondet/portfolio-fair/none/all"] = "f6ffe61bc97f2229",
            ["nondet/portfolio-unfair/none/all"] = "f6ffe61bc97f2229",
            ["readwrite/random/none/all"] = "70f6ebd42de89ce6",
            ["readwrite/probabilistic/none/all"] = "36f7319f970872f3",
            ["readwrite/prioritization/none/all"] = "16fb80ed89ca82e3",
            ["readwrite/fair-prioritization/none/all"] = "16fb80ed89ca82e3",
            ["readwrite/delay-bounding/none/all"] = "fa2411e378d59281",
            ["readwrite/fair-delay-bounding/none/all"] = "fa2411e378d59281",
            ["readwrite/q-learning/none/all"] = "8942c9dfd955ad48",
            ["readwrite/dfs/none/all"] = "68c7c6985f31f5cd",
            ["readwrite/portfolio-fair/none/all"] = "d7f0b09aa301c6ea",
            ["readwrite/portfolio-unfair/none/all"] = "d7f0b09aa301c6ea",
            ["readwrite/random/cycle/all"] = "99d5a2f3a5293f80",
            ["readwrite/fair-prioritization/cycle/all"] = "db28985c3fd40f8f",
            ["readwrite/random/partial-order/all"] = "7c14e8cc7942fee1",
            ["readwrite/fair-prioritization/partial-order/all"] = "6ccb8c3cd6982c1b",
            ["readwrite/random/both/all"] = "0d03bf2bb8acdaf4",
            ["readwrite/fair-prioritization/both/all"] = "29299296daba4a7f",

            // The live-hashing variants coarsen the program state, so q-learning keys on fewer
            // distinct states and explores a different sequence. Each therefore differs from its
            // '/all' counterpart above; a pair that agreed would mean the setting stopped taking
            // effect. This is the one place where a trace digest is expected to differ by design.
            ["race/q-learning/none/live"] = "1ad0b8f71730e160",
            ["nondet/q-learning/none/live"] = "2a373672b888ea50",
            ["readwrite/q-learning/none/live"] = "fe5e1b268d9bf821",
        };

        /// <summary>
        /// The expected digest for each '{program}/{strategy}/{reducers}/{hashing}' configuration,
        /// covering the ordered execution traces together with the summary statistics of the run.
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

            // The portfolio entries below carry a lower visited-state count than they did before
            // implicit program-state hashing was gated on the active strategy. Only the iterations
            // running q-learning now compute the state, so only those contribute to the count. The
            // traces are untouched, which is why the corresponding GoldenTraceDigests entries did
            // not move, and TestGatingChangesOnlyTheVisitedStateCount asserts that no other
            // statistic moved either. The 'nondet' portfolio entries are absent from this list
            // because that program's state space is small enough that the q-learning iterations
            // alone already reach every state the full rotation reached.
            ["race/portfolio-fair/none/all"] = "cee97aeb3df6828a",
            ["race/portfolio-unfair/none/all"] = "003266bccebe50d2",
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
            ["readwrite/portfolio-fair/none/all"] = "9196f2cc34367ebe",
            ["readwrite/portfolio-unfair/none/all"] = "fa7eae0c266d4afe",
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
        /// Runs the specified configuration and returns two digests: one over the ordered sequence
        /// of execution traces explored, and one over those traces together with the summary
        /// statistics of the run.
        /// </summary>
        /// <remarks>
        /// Both come from a single run, so the pair is always self-consistent. Computing them
        /// separately rather than folding everything into one value is what lets a caller tell
        /// "exploration changed" apart from "a reported statistic changed".
        /// </remarks>
        private static (string Trace, string Combined) ComputeDigest(string program, string strategy,
            string reducers, string hashing)
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

            // Snapshot the trace-only digest before the statistics are appended, so the two values
            // describe the same run.
            string traceDigest = Hash(builder.ToString());

            builder.Append('|').Append(report.NumOfFoundBugs.ToString(CultureInfo.InvariantCulture));
            builder.Append('|').Append(report.NumOfExploredFairPaths.ToString(CultureInfo.InvariantCulture));
            builder.Append('|').Append(report.NumOfExploredUnfairPaths.ToString(CultureInfo.InvariantCulture));
            builder.Append('|').Append(report.CoverageInfo.VisitedStates.Count.ToString(CultureInfo.InvariantCulture));
            builder.Append('|').Append(report.TotalControlledOperations.ToString(CultureInfo.InvariantCulture));
            builder.Append('|').Append(report.TotalConcurrencyDegree.ToString(CultureInfo.InvariantCulture));
            builder.Append('|').Append(report.TotalOperationGroupingDegree.ToString(CultureInfo.InvariantCulture));
            return (traceDigest, Hash(builder.ToString()));
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
            var traceMismatches = new List<string>();
            var combinedMismatches = new List<string>();
            var traceTable = new StringBuilder();
            var combinedTable = new StringBuilder();

            foreach ((string program, string strategy, string reducers, string hashing) in Configurations())
            {
                string key = $"{program}/{strategy}/{reducers}/{hashing}";
                (string trace, string combined) = ComputeDigest(program, strategy, reducers, hashing);
                traceTable.AppendLine($"            [\"{key}\"] = \"{trace}\",");
                combinedTable.AppendLine($"            [\"{key}\"] = \"{combined}\",");
                Compare(GoldenTraceDigests, key, trace, traceMismatches);
                Compare(GoldenDigests, key, combined, combinedMismatches);
            }

            var message = new StringBuilder();
            if (traceMismatches.Count > 0)
            {
                // Report this first and describe it as what it is: exploration moved, which is a
                // different and more serious claim than a statistic moving.
                message.AppendLine($"The explored schedules changed for {traceMismatches.Count} configuration(s):");
                message.AppendLine(string.Join(Environment.NewLine, traceMismatches));
                message.AppendLine();
            }

            if (combinedMismatches.Count > 0)
            {
                message.AppendLine(traceMismatches.Count is 0 ?
                    $"The explored schedules are unchanged, but the reported statistics changed for " +
                    $"{combinedMismatches.Count} configuration(s):" :
                    $"Combined digest mismatches ({combinedMismatches.Count}):");
                message.AppendLine(string.Join(Environment.NewLine, combinedMismatches));
                message.AppendLine();
            }

            if (message.Length > 0)
            {
                message.AppendLine("If this change is intentional, replace GoldenTraceDigests with:");
                message.AppendLine(traceTable.ToString());
                message.AppendLine("and GoldenDigests with:");
                message.AppendLine(combinedTable.ToString());
            }

            Assert.True(traceMismatches.Count is 0 && combinedMismatches.Count is 0, message.ToString());
        }

        /// <summary>
        /// Records a mismatch against the specified golden table, if there is one.
        /// </summary>
        private static void Compare(Dictionary<string, string> goldens, string key, string actual,
            List<string> mismatches)
        {
            if (!goldens.TryGetValue(key, out string expected))
            {
                mismatches.Add($"{key}: no golden recorded, actual '{actual}'");
            }
            else if (expected != actual)
            {
                mismatches.Add($"{key}: expected '{expected}', actual '{actual}'");
            }
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

        /// <summary>
        /// Verifies that the two golden tables cover exactly the swept configurations, so that a
        /// configuration cannot be added without recording both of its digests.
        /// </summary>
        [Fact(Timeout = 5000)]
        public void TestGoldenTablesCoverEveryConfiguration()
        {
            var keys = new List<string>();
            foreach ((string program, string strategy, string reducers, string hashing) in Configurations())
            {
                keys.Add($"{program}/{strategy}/{reducers}/{hashing}");
            }

            Assert.Equal(keys.Count, GoldenTraceDigests.Count);
            Assert.Equal(keys.Count, GoldenDigests.Count);
            foreach (string key in keys)
            {
                Assert.True(GoldenTraceDigests.ContainsKey(key), $"No trace digest recorded for '{key}'.");
                Assert.True(GoldenDigests.ContainsKey(key), $"No combined digest recorded for '{key}'.");
            }
        }
    }
}

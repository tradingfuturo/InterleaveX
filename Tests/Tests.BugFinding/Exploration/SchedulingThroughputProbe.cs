// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Coyote.Logging;
using Microsoft.Coyote.SystematicTesting;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// A throughput probe used to compare scheduling cost before and after a change. It is skipped
    /// by default because it measures wall-clock time and is not an assertion about correctness.
    /// </summary>
    public class SchedulingThroughputProbe : BaseBugFindingTest
    {
        public SchedulingThroughputProbe(ITestOutputHelper output)
            : base(output)
        {
        }

        private static Configuration GetProbeConfiguration(uint iterations) => Configuration.Create()
            .WithTelemetryEnabled(false)
            .WithPartiallyControlledConcurrencyAllowed(false)
            .WithTestingIterations(iterations)
            .WithRandomGeneratorSeed(199)
            .WithTestIterationsRunToCompletion();

        /// <summary>
        /// Creates and awaits the specified number of short-lived tasks in sequence. Every task
        /// completes before the next is created, so the number of live operations stays tiny while
        /// the number of operations the iteration has created grows.
        /// </summary>
        private static Func<Task> MakeShortLivedWorkload(int count) => async () =>
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                await Task.Run(() => value++);
            }
        };

        /// <summary>
        /// Runs a fixed number of concurrent tasks that each take many scheduling steps, so the
        /// number of live operations stays constant.
        /// </summary>
        private static Func<Task> MakeLongLivedWorkload(int tasks, int steps) => async () =>
        {
            int value = 0;
            object mutex = new object();
            var running = new Task[tasks];
            for (int t = 0; t < tasks; t++)
            {
                running[t] = Task.Run(() =>
                {
                    for (int s = 0; s < steps; s++)
                    {
                        lock (mutex)
                        {
                            value++;
                        }
                    }
                });
            }

            await Task.WhenAll(running);
        };

        private void Measure(string label, Func<Task> workload, uint iterations, bool hashLiveOperationsOnly = false)
        {
            var configuration = GetProbeConfiguration(iterations)
                .WithLiveOperationStateHashingEnabled(hashLiveOperationsOnly);
            var logWriter = new LogWriter(configuration);

            // Warm up the JIT and the rewriting caches before timing.
            using (var warmup = new TestingEngine(configuration, workload, logWriter))
            {
                warmup.Run();
            }

            using var engine = new TestingEngine(configuration, workload, logWriter);
            var watch = Stopwatch.StartNew();
            engine.Run();
            watch.Stop();

            long steps = engine.TestReport.TotalExploredFairSteps + engine.TestReport.TotalExploredUnfairSteps;
            double perSecond = steps / watch.Elapsed.TotalSeconds;
            this.TestOutput.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "PROBE {0}: {1} ms, {2} steps, {3:N0} steps/s", label, watch.ElapsedMilliseconds, steps, perSecond));
        }

        [Fact(Timeout = 600000, Skip = "Throughput probe; run manually to compare against a baseline.")]
        public void ProbeSchedulingThroughput()
        {
            this.Measure("short-lived-100", MakeShortLivedWorkload(100), 20);
            this.Measure("short-lived-200", MakeShortLivedWorkload(200), 20);
            this.Measure("short-lived-400", MakeShortLivedWorkload(400), 20);
            this.Measure("long-lived-8x2000", MakeLongLivedWorkload(8, 2000), 10);

            // The same workloads with the program state restricted to live operations, which is the
            // one remaining traversal whose cost grows with the number of completed operations.
            this.Measure("short-lived-100-live-hash", MakeShortLivedWorkload(100), 20, true);
            this.Measure("short-lived-400-live-hash", MakeShortLivedWorkload(400), 20, true);
        }
    }
}

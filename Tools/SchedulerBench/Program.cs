// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Coyote.SystematicTesting;

namespace Microsoft.Coyote.Benchmarking.Scheduler
{
    /// <summary>
    /// Benchmark harness that drives the systematic testing runtime directly.
    /// </summary>
    /// <remarks>
    /// The benchmarks in <c>Tools/BenchmarkRunner</c> all exercise the production actor
    /// runtime, so none of them observe changes to the scheduler, the exploration
    /// strategies, or the per-scheduling-step bookkeeping. This harness fills that gap.
    /// It reports wall-clock time and bytes allocated per run, which BenchmarkDotNet's
    /// diagnoser in this repository does not (it captures a single post-run memory
    /// snapshot, which is a leak detector rather than an allocation metric).
    /// </remarks>
    public static class Program
    {
        public static int Main(string[] args)
        {
            // Must happen before any Configuration is created, as the configuration
            // constructor reads this variable.
            Environment.SetEnvironmentVariable("COYOTE_CLI_TELEMETRY_OPTOUT", "1");

            string workload = GetOption(args, "--workload", "deep");
            string strategy = GetOption(args, "--strategy", "default");
            uint iterations = uint.Parse(GetOption(args, "--iterations", "100"));
            uint seed = uint.Parse(GetOption(args, "--seed", "42"));
            int repeat = int.Parse(GetOption(args, "--repeat", "5"));
            int warmup = int.Parse(GetOption(args, "--warmup", "3"));
            string label = GetOption(args, "--label", "run");
            bool header = Array.IndexOf(args, "--no-header") < 0;

            if (workload is not "deep" and not "wide")
            {
                Console.Error.WriteLine($"Unknown workload '{workload}'. Expected 'deep' or 'wide'.");
                return 1;
            }

            if (strategy is not "default" and not "random")
            {
                Console.Error.WriteLine($"Unknown strategy '{strategy}'. Expected 'default' or 'random'.");
                return 1;
            }

            if (header)
            {
                Console.WriteLine("label,workload,strategy,iterations,run,elapsed_ms,cpu_ms,allocated_bytes,paths,steps,bugs");
            }

            // The first few runs are dominated by JIT and are discarded. Without this the
            // wall-clock spread across runs exceeds 100%, which swamps the effect sizes
            // these measurements need to resolve.
            var elapsedSamples = new List<double>(repeat);
            var cpuSamples = new List<double>(repeat);
            var allocatedSamples = new List<long>(repeat);
            int steps = 0;

            for (int run = 0; run < warmup + repeat; run++)
            {
                RunResult result = RunOnce(workload, strategy, iterations, seed);
                if (result.Bugs > 0)
                {
                    Console.Error.WriteLine(
                        $"Workload '{workload}' reported {result.Bugs} bug(s); it must be bug-free for all " +
                        "iterations to run. Measurement aborted.");
                    return 1;
                }

                bool isWarmup = run < warmup;
                if (!isWarmup)
                {
                    elapsedSamples.Add(result.ElapsedMs);
                    cpuSamples.Add(result.CpuMs);
                    allocatedSamples.Add(result.AllocatedBytes);
                    steps = result.Steps;
                }

                Console.WriteLine(
                    $"{label},{workload},{strategy},{iterations},{(isWarmup ? "warmup" : run.ToString())}," +
                    $"{result.ElapsedMs:F1},{result.CpuMs:F1},{result.AllocatedBytes}," +
                    $"{result.Paths},{result.Steps},{result.Bugs}");
            }

            elapsedSamples.Sort();
            cpuSamples.Sort();
            allocatedSamples.Sort();
            double medianElapsed = Median(elapsedSamples);
            double medianCpu = Median(cpuSamples);
            long medianAllocated = (long)Median(allocatedSamples.ConvertAll(v => (double)v));

            double cpuSpread = cpuSamples.Count > 1 && medianCpu > 0 ?
                (cpuSamples[cpuSamples.Count - 1] - cpuSamples[0]) / medianCpu * 100 : 0;
            double allocSpread = allocatedSamples.Count > 1 && medianAllocated > 0 ?
                (double)(allocatedSamples[allocatedSamples.Count - 1] - allocatedSamples[0]) / medianAllocated * 100 : 0;

            Console.WriteLine(
                $"{label},{workload},{strategy},{iterations},MEDIAN,{medianElapsed:F1},{medianCpu:F1}," +
                $"{medianAllocated},steps={steps},cpuSpread={cpuSpread:F1}%,allocSpread={allocSpread:F3}%");
            return 0;
        }

        /// <summary>
        /// Runs the specified workload once for the given number of testing iterations.
        /// </summary>
        private static RunResult RunOnce(string workload, string strategy, uint iterations, uint seed)
        {
            Configuration configuration = Configuration.Create()
                .WithTestingIterations(iterations)
                .WithRandomGeneratorSeed(seed);
            if (strategy is "random")
            {
                // Sets PortfolioMode.None, which in turn leaves implicit program state
                // hashing disabled. The default configuration takes the opposite path.
                configuration = configuration.WithRandomStrategy();
            }

            Func<Task> test = workload is "deep" ? Workloads.RunDeepAsync : Workloads.RunWideAsync;
            using TestingEngine engine = TestingEngine.Create(configuration, test);

            // The testing engine always installs a console logger, so silence it for the
            // duration of the measurement to keep console I/O out of the timings.
            TextWriter originalOut = Console.Out;
            Console.SetOut(TextWriter.Null);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // Wall clock is dominated by thread handoffs, which block in the kernel and are
            // highly sensitive to machine load; its run-to-run spread swamps the effect
            // sizes of interest. Total processor time excludes blocked time and therefore
            // tracks the CPU work actually performed, which is what these changes remove.
            Process process = Process.GetCurrentProcess();
            TimeSpan cpuBefore = process.TotalProcessorTime;
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                engine.Run();
            }
            finally
            {
                stopwatch.Stop();
                Console.SetOut(originalOut);
            }

            long allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
            TimeSpan cpuAfter = process.TotalProcessorTime;

            return new RunResult
            {
                ElapsedMs = stopwatch.Elapsed.TotalMilliseconds,
                CpuMs = (cpuAfter - cpuBefore).TotalMilliseconds,
                AllocatedBytes = allocatedAfter - allocatedBefore,
                Paths = engine.TestReport.NumOfExploredFairPaths + engine.TestReport.NumOfExploredUnfairPaths,
                Steps = engine.TestReport.TotalExploredFairSteps + engine.TestReport.TotalExploredUnfairSteps,
                Bugs = engine.TestReport.NumOfFoundBugs
            };
        }

        /// <summary>
        /// Returns the median of the specified sorted samples.
        /// </summary>
        private static double Median(List<double> sorted) =>
            sorted.Count is 0 ? 0 :
            sorted.Count % 2 is 1 ? sorted[sorted.Count / 2] :
            (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;

        /// <summary>
        /// Returns the value of the specified option, or the default value if absent.
        /// </summary>
        private static string GetOption(string[] args, string name, string defaultValue)
        {
            int idx = Array.IndexOf(args, name);
            return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : defaultValue;
        }

        /// <summary>
        /// The result of a single benchmark run.
        /// </summary>
        private struct RunResult
        {
            internal double ElapsedMs;
            internal double CpuMs;
            internal long AllocatedBytes;
            internal int Paths;
            internal int Steps;
            internal int Bugs;
        }
    }
}

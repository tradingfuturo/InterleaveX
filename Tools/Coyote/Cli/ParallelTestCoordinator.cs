// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.Coyote.Logging;
using Microsoft.Coyote.SystematicTesting;

namespace Microsoft.Coyote.Cli
{
    /// <summary>
    /// Runs testing iterations in parallel by sharding them across worker processes.
    /// </summary>
    /// <remarks>
    /// Iterations cannot be parallelized within a single process. The runtime keeps
    /// process wide state that is reset between iterations, and more fundamentally the
    /// program under test has static state of its own, so concurrent iterations would
    /// interfere with each other and report bugs that do not exist. Coyote's model is that
    /// one iteration owns the process, so parallelism has to be at process granularity.
    /// </remarks>
    internal sealed class ParallelTestCoordinator
    {
        /// <summary>
        /// Environment variable naming the file whose creation asks a worker to stop at its
        /// next iteration boundary.
        /// </summary>
        internal const string StopFileVariable = "INTERLEAVEX_INTERNAL_STOP_FILE";

        /// <summary>
        /// Environment variable naming the file a worker writes its test report to.
        /// </summary>
        internal const string ReportFileVariable = "INTERLEAVEX_INTERNAL_REPORT_FILE";

        /// <summary>
        /// Environment variable naming the process a worker should not outlive.
        /// </summary>
        internal const string ParentProcessVariable = "INTERLEAVEX_INTERNAL_PARENT_PID";

        /// <summary>
        /// How long to wait for workers to stop gracefully before killing them.
        /// </summary>
        private static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(10);

        /// <summary>
        /// The configuration of the run being sharded.
        /// </summary>
        private readonly Configuration Configuration;

        /// <summary>
        /// The command line this process was invoked with.
        /// </summary>
        private readonly string[] RawArgs;

        /// <summary>
        /// Used to log messages.
        /// </summary>
        private readonly LogWriter LogWriter;

        /// <summary>
        /// The workers of the current run.
        /// </summary>
        private readonly List<Worker> Workers;

        /// <summary>
        /// Set once the workers have been asked to stop.
        /// </summary>
        private int IsStopRequested;

        /// <summary>
        /// The output directory of the lowest indexed worker that found a bug, or null if
        /// no worker found one. Its artifacts are the ones promoted to the top level, so
        /// that the documented path to a reproducible trace keeps working.
        /// </summary>
        internal string BuggyWorkerDirectory
        {
            get
            {
                foreach (var worker in this.Workers)
                {
                    if (worker.Process != null && worker.Process.HasExited &&
                        worker.Process.ExitCode is (int)ExitCode.BugFound)
                    {
                        return worker.Directory;
                    }
                }

                return null;
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ParallelTestCoordinator"/> class.
        /// </summary>
        internal ParallelTestCoordinator(Configuration configuration, string[] rawArgs, LogWriter logWriter)
        {
            this.Configuration = configuration;
            this.RawArgs = rawArgs;
            this.LogWriter = logWriter;
            this.Workers = new List<Worker>();
        }

        /// <summary>
        /// Runs the specified test method sharded across worker processes, and returns the
        /// merged report.
        /// </summary>
        internal TestReport Run(string method, string runDirectory)
        {
            uint baseSeed = this.Configuration.RandomGeneratorSeed ?? (uint)Guid.NewGuid().GetHashCode();
            var shards = ParallelTestPlan.Compute(this.Configuration, baseSeed, this.Configuration.ParallelWorkerCount);

            this.LogWriter.LogImportant("... Sharding {0} across {1} worker process(es), base seed {2}.",
                this.Configuration.TestingTimeout > 0 ?
                    $"a {this.Configuration.TestingTimeout} second run" :
                    $"{this.Configuration.TestingIterations} iteration(s)",
                shards.Count, baseSeed);

            string stopFile = Path.Combine(runDirectory, ".stop");
            SafeDelete(stopFile);

            ConsoleCancelEventHandler cancelHandler = (sender, e) =>
            {
                e.Cancel = true;
                this.RequestStop(stopFile, "cancellation was requested");
            };

            Console.CancelKeyPress += cancelHandler;
            try
            {
                foreach (var shard in shards)
                {
                    this.StartWorker(method, shard, runDirectory, stopFile);
                }

                this.WaitForWorkers(stopFile);
                return this.MergeReports();
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
                this.RequestStop(stopFile, null);
                this.KillSurvivors();
                SafeDelete(stopFile);
            }
        }

        /// <summary>
        /// Starts a worker process for the specified shard.
        /// </summary>
        private void StartWorker(string method, ParallelTestPlan.Shard shard, string runDirectory, string stopFile)
        {
            string workerDirectory = Path.Combine(runDirectory, $"w{shard.Index}");
            Directory.CreateDirectory(workerDirectory);

            string reportFile = Path.Combine(workerDirectory, "report.ser");
            string[] childArgs = ParallelTestPlan.BuildChildArgs(this.RawArgs, method, shard, workerDirectory);

            var startInfo = new ProcessStartInfo(GetHostPath())
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Environment.CurrentDirectory
            };

            foreach (string arg in GetHostArguments(childArgs))
            {
                startInfo.ArgumentList.Add(arg);
            }

            startInfo.Environment[StopFileVariable] = stopFile;
            startInfo.Environment[ReportFileVariable] = reportFile;
            startInfo.Environment[ParentProcessVariable] =
                Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);

            // A parallel run would otherwise report one telemetry event per worker and
            // understate each run's elapsed time, so contribute nothing instead.
            startInfo.Environment["COYOTE_CLI_TELEMETRY_OPTOUT"] = "1";

            var worker = new Worker(shard, reportFile, workerDirectory);
            try
            {
                worker.Process = Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                worker.LaunchError = ex.Message;
                this.Workers.Add(worker);
                return;
            }

            // Buffer the output rather than writing it through, so that the console is not
            // an interleaving of every worker's messages.
            worker.Process.OutputDataReceived += (sender, e) => worker.Append(e.Data);
            worker.Process.ErrorDataReceived += (sender, e) => worker.Append(e.Data);
            worker.Process.BeginOutputReadLine();
            worker.Process.BeginErrorReadLine();
            this.Workers.Add(worker);
        }

        /// <summary>
        /// Waits for all workers to exit, stopping the remaining ones once a bug is found.
        /// </summary>
        private void WaitForWorkers(string stopFile)
        {
            var pending = new List<Worker>(this.Workers);
            while (pending.Count > 0)
            {
                for (int idx = pending.Count - 1; idx >= 0; idx--)
                {
                    Worker worker = pending[idx];
                    if (worker.Process is null)
                    {
                        this.LogWriter.LogError("..... [w{0}] failed to start: {1}", worker.Shard.Index, worker.LaunchError);
                        pending.RemoveAt(idx);
                        continue;
                    }

                    if (!worker.Process.WaitForExit(200))
                    {
                        continue;
                    }

                    worker.Process.WaitForExit();
                    pending.RemoveAt(idx);
                    this.LogWriter.LogImportant("..... [w{0}] finished with exit code {1}.",
                        worker.Shard.Index, (ExitCode)worker.Process.ExitCode);

                    if (worker.Process.ExitCode is (int)ExitCode.BugFound &&
                        !this.Configuration.RunTestIterationsToCompletion)
                    {
                        this.RequestStop(stopFile, "a bug was found");
                    }
                }
            }
        }

        /// <summary>
        /// Asks all workers to stop at their next iteration boundary.
        /// </summary>
        private void RequestStop(string stopFile, string reason)
        {
            if (Interlocked.Exchange(ref this.IsStopRequested, 1) != 0)
            {
                return;
            }

            try
            {
                File.Create(stopFile).Dispose();
                if (reason != null)
                {
                    this.LogWriter.LogImportant("..... Stopping the remaining worker(s) because {0}.", reason);
                }
            }
            catch (IOException)
            {
                // Nothing more can be done to stop the workers gracefully; they are killed
                // by the caller once the grace period elapses.
            }
        }

        /// <summary>
        /// Kills any worker that has not exited within the grace period.
        /// </summary>
        private void KillSurvivors()
        {
            var deadline = Stopwatch.StartNew();
            foreach (var worker in this.Workers)
            {
                if (worker.Process is null || worker.Process.HasExited)
                {
                    continue;
                }

                int remaining = (int)Math.Max(0, (StopGracePeriod - deadline.Elapsed).TotalMilliseconds);
                if (!worker.Process.WaitForExit(remaining))
                {
                    try
                    {
                        worker.Process.Kill(entireProcessTree: true);
                    }
                    catch (Exception)
                    {
                        // The process exited between the check and the kill.
                    }
                }
            }
        }

        /// <summary>
        /// Merges the reports of all workers into a single report.
        /// </summary>
        private TestReport MergeReports()
        {
            TestReport aggregate = new TestReport(this.Configuration);
            foreach (var worker in this.Workers)
            {
                if (!File.Exists(worker.ReportFile))
                {
                    this.LogWriter.LogError("..... [w{0}] produced no report.", worker.Shard.Index);
                    this.DumpOutput(worker);
                    aggregate.InternalErrors.Add($"Worker {worker.Shard.Index} produced no test report.");
                    continue;
                }

                try
                {
                    TestReport report = TestReport.Load(worker.ReportFile);
                    if (!aggregate.Merge(report))
                    {
                        this.LogWriter.LogWarning("..... [w{0}] report could not be merged.", worker.Shard.Index);
                    }
                }
                catch (Exception ex)
                {
                    this.LogWriter.LogError("..... [w{0}] report could not be read: {1}", worker.Shard.Index, ex.Message);
                    aggregate.InternalErrors.Add($"Worker {worker.Shard.Index} report could not be read: {ex.Message}");
                }

                if (this.LogWriter.IsVerbose(LogSeverity.Info))
                {
                    this.DumpOutput(worker);
                }
            }

            return aggregate;
        }

        /// <summary>
        /// Writes the buffered output of the specified worker, prefixed so that it can be
        /// told apart from the output of the other workers.
        /// </summary>
        private void DumpOutput(Worker worker)
        {
            foreach (string line in worker.GetOutput())
            {
                this.LogWriter.LogImportant("[w{0}] {1}", worker.Shard.Index, line);
            }
        }

        /// <summary>
        /// Returns the path used to launch a worker process.
        /// </summary>
        private static string GetHostPath() => Process.GetCurrentProcess().MainModule.FileName;

        /// <summary>
        /// Returns the arguments to pass to the host, accounting for whether this process
        /// was launched through its own apphost or through the shared dotnet host.
        /// </summary>
        private static IEnumerable<string> GetHostArguments(string[] childArgs)
        {
            string entryAssembly = Assembly.GetEntryAssembly()?.Location;
            string host = GetHostPath();
            bool isApphost = !string.IsNullOrEmpty(entryAssembly) && string.Equals(
                Path.GetFileNameWithoutExtension(host),
                Path.GetFileNameWithoutExtension(entryAssembly),
                StringComparison.OrdinalIgnoreCase);
            if (!isApphost)
            {
                yield return "exec";
                yield return entryAssembly;
            }

            foreach (string arg in childArgs)
            {
                yield return arg;
            }
        }

        /// <summary>
        /// Deletes the specified file if it exists.
        /// </summary>
        private static void SafeDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // The file is in use or already gone; either way there is nothing to do.
            }
        }

        /// <summary>
        /// A single worker process and the shard it was assigned.
        /// </summary>
        private sealed class Worker
        {
            /// <summary>
            /// The shard assigned to this worker.
            /// </summary>
            internal ParallelTestPlan.Shard Shard { get; }

            /// <summary>
            /// The file this worker writes its test report to.
            /// </summary>
            internal string ReportFile { get; }

            /// <summary>
            /// The output directory of this worker.
            /// </summary>
            internal string Directory { get; }

            /// <summary>
            /// The worker process, or null if it could not be started.
            /// </summary>
            internal Process Process { get; set; }

            /// <summary>
            /// The reason the worker could not be started, if it could not be.
            /// </summary>
            internal string LaunchError { get; set; }

            /// <summary>
            /// Buffered standard output and error of this worker.
            /// </summary>
            private readonly List<string> Output = new List<string>();

            /// <summary>
            /// Initializes a new instance of the <see cref="Worker"/> class.
            /// </summary>
            internal Worker(ParallelTestPlan.Shard shard, string reportFile, string directory)
            {
                this.Shard = shard;
                this.ReportFile = reportFile;
                this.Directory = directory;
            }

            /// <summary>
            /// Appends a line of output from this worker.
            /// </summary>
            internal void Append(string line)
            {
                if (line != null)
                {
                    lock (this.Output)
                    {
                        this.Output.Add(line);
                    }
                }
            }

            /// <summary>
            /// Returns the buffered output of this worker.
            /// </summary>
            internal IEnumerable<string> GetOutput()
            {
                lock (this.Output)
                {
                    return new List<string>(this.Output);
                }
            }
        }
    }
}

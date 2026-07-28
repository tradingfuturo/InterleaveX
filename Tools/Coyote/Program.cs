// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Coyote.Cli;
using Microsoft.Coyote.Logging;
using Microsoft.Coyote.Rewriting;
using Microsoft.Coyote.SystematicTesting;

namespace Microsoft.Coyote
{
    /// <summary>
    /// The entry point to the Coyote tool.
    /// </summary>
    internal class Program
    {
        /// <summary>
        /// The command line this process was invoked with. Worker processes are launched by
        /// filtering these arguments, rather than reconstructing them from the configuration,
        /// so that options the coordinator does not know about are still passed through.
        /// </summary>
        private static string[] RawArgs;

        private static int Main(string[] args)
        {
            RawArgs = args;
            var parser = new CommandLineParser(args);
            if (!parser.IsSuccessful)
            {
                return (int)ExitCode.Error;
            }

            parser.SetTestCommandHandler(RunTest);
            parser.SetReplayCommandHandler(ReplayTest);
            parser.SetRewriteCommandHandler(RewriteAssemblies);
            return (int)parser.InvokeCommand();
        }

        /// <summary>
        /// Runs the test specified in the configuration.
        /// </summary>
        private static ExitCode RunTest(Configuration configuration)
        {
            using var logWriter = new LogWriter(configuration, true);
            try
            {
                // Load the configuration of the assembly to be tested.
                LoadAssemblyConfiguration(configuration.AssemblyToBeAnalyzed, logWriter);

                // Handle --list-tests: discover, print, and exit.
                if (configuration.ListTests)
                {
                    var testNames = TestMethodInfo.GetAllTestMethodNames(configuration, logWriter);
                    if (testNames.Count == 0)
                    {
                        logWriter.LogImportant(". No [Test] methods found in {0}.",
                            configuration.AssemblyToBeAnalyzed);
                        return ExitCode.Success;
                    }

                    logWriter.LogImportant(". Found {0} test method(s) in {1}:",
                        testNames.Count, configuration.AssemblyToBeAnalyzed);
                    foreach (var name in testNames)
                    {
                        logWriter.LogImportant("  {0}", name);
                    }

                    return ExitCode.Success;
                }

                // Determine which test methods to run.
                List<string> methodsToRun;
                if (!string.IsNullOrEmpty(configuration.TestMethodName))
                {
                    // Explicit -m flag: single test (existing behavior path).
                    methodsToRun = new List<string> { configuration.TestMethodName };
                }
                else
                {
                    // No -m flag: discover all [Test] methods.
                    methodsToRun = TestMethodInfo.GetAllTestMethodNames(configuration, logWriter);
                    if (methodsToRun.Count == 0)
                    {
                        throw new InvalidOperationException(
                            "Cannot detect a test method compatible with InterleaveX (Coyote) declared with the " +
                            $"'[{typeof(TestAttribute).FullName}]' attribute.");
                    }
                }

                bool multipleTests = methodsToRun.Count > 1;
                ExitCode worstExitCode = ExitCode.Success;
                int passed = 0, failed = 0, errored = 0;

                if (multipleTests)
                {
                    logWriter.LogImportant(". Testing {0} method(s) in {1}.",
                        methodsToRun.Count, configuration.AssemblyToBeAnalyzed);
                }

                for (int i = 0; i < methodsToRun.Count; i++)
                {
                    configuration.TestMethodName = methodsToRun[i];
                    ExitCode testExitCode;

                    try
                    {
                        if (multipleTests)
                        {
                            logWriter.LogImportant(string.Empty);
                            logWriter.LogImportant("== [{0}/{1}] Testing {2} ==",
                                i + 1, methodsToRun.Count, methodsToRun[i]);
                        }
                        else
                        {
                            logWriter.LogImportant(". Testing {0}.",
                                configuration.AssemblyToBeAnalyzed);
                        }

                        if (configuration.ParallelWorkerCount > 1)
                        {
                            testExitCode = RunTestInParallel(configuration, logWriter);
                        }
                        else
                        {
                            using TestingEngine engine = new TestingEngine(configuration, logWriter);
                            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

                            // When this process is a worker of a parallel run, stop at the next
                            // iteration boundary if the coordinator asks, or if it has gone away.
                            RegisterWorkerStopCallback(engine);
                            engine.Run();

                            string directory = OutputFileManager.CreateOutputDirectory(configuration);
                            string fileName = OutputFileManager.GetResolvedFileName(
                                configuration.AssemblyToBeAnalyzed, directory);

                            // Emit the test reports.
                            logWriter.LogImportant("... Emitting execution trace reports:");
                            if (engine.TryEmitReports(directory, fileName,
                                out IEnumerable<string> reportPaths))
                            {
                                foreach (var path in reportPaths)
                                {
                                    logWriter.LogImportant("..... Writing {0}", path);
                                }
                            }
                            else
                            {
                                logWriter.LogImportant("..... No test reports available.");
                            }

                            // Emit the coverage reports.
                            logWriter.LogImportant("... Emitting coverage reports:");
                            if (engine.TryEmitCoverageReports(directory, fileName, out reportPaths))
                            {
                                foreach (var path in reportPaths)
                                {
                                    logWriter.LogImportant("..... Writing {0}", path);
                                }
                            }
                            else
                            {
                                logWriter.LogImportant("..... No coverage reports available.");
                            }

                            // Saved last, once everything that can fail has run. A worker that saved
                            // its report and then threw would otherwise leave a clean report behind
                            // for the coordinator to merge, and the run it failed would pass.
                            SaveWorkerReport(engine);

                            logWriter.LogImportant(engine.TestReport.GetText(configuration, "..."));
                            logWriter.LogImportant("... Elapsed {0} sec.", engine.Profiler.Results());
                            testExitCode = GetExitCodeFromTestReport(engine.TestReport);
                        }
                    }
                    catch (Exception ex)
                    {
                        logWriter.LogError(ex.Message);
                        logWriter.LogDebug(ex.StackTrace);
                        testExitCode = ExitCode.Error;
                    }

                    switch (testExitCode)
                    {
                        case ExitCode.BugFound:
                            failed++;
                            break;
                        case ExitCode.InternalError:
                        case ExitCode.Error:
                            errored++;
                            break;
                        default:
                            passed++;
                            break;
                    }

                    if (testExitCode > worstExitCode)
                    {
                        worstExitCode = testExitCode;
                    }

                    if (configuration.StopOnFirstFailure && testExitCode != ExitCode.Success)
                    {
                        logWriter.LogImportant(string.Empty);
                        logWriter.LogImportant("Stopping after first failure (--stop-on-first-failure).");
                        break;
                    }
                }

                if (multipleTests)
                {
                    logWriter.LogImportant(string.Empty);
                    logWriter.LogImportant("== Summary ==");
                    logWriter.LogImportant("  {0} passed, {1} failed, {2} error(s), {3} total",
                        passed, failed, errored, methodsToRun.Count);
                }

                return worstExitCode;
            }
            catch (Exception ex)
            {
                logWriter.LogError(ex.Message);
                logWriter.LogDebug(ex.StackTrace);
                return ExitCode.Error;
            }
        }

        /// <summary>
        /// Replays an execution that is specified in the configuration.
        /// </summary>
        private static ExitCode ReplayTest(Configuration configuration)
        {
            using var logWriter = new LogWriter(configuration, true);
            try
            {
                // Load the configuration of the assembly to be replayed.
                LoadAssemblyConfiguration(configuration.AssemblyToBeAnalyzed, logWriter);

                logWriter.LogImportant(". Reproducing trace in {0}.", configuration.AssemblyToBeAnalyzed);
                using TestingEngine engine = new TestingEngine(configuration, logWriter);
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                engine.Run();

                // Emit the report.
                if (engine.TestReport.NumOfFoundBugs > 0)
                {
                    logWriter.LogImportant(engine.GetReport());
                }

                logWriter.LogImportant("... Elapsed {0} sec.", engine.Profiler.Results());
                return GetExitCodeFromTestReport(engine.TestReport);
            }
            catch (Exception ex)
            {
                logWriter.LogError(ex.Message);
                logWriter.LogDebug(ex.StackTrace);
                return ExitCode.Error;
            }
        }

        /// <summary>
        /// Rewrites the assemblies specified in the configuration.
        /// </summary>
        private static ExitCode RewriteAssemblies(Configuration configuration, RewritingOptions options)
        {
            using var logWriter = new LogWriter(configuration, true);
            try
            {
                if (options.AssemblyPaths.Count is 1)
                {
                    logWriter.LogImportant(". Rewriting {0}.", options.AssemblyPaths.First());
                }
                else
                {
                    logWriter.LogImportant(". Rewriting the assemblies specified in {0}.", options.AssembliesDirectory);
                }

                var profiler = new Profiler();
                RewritingEngine.Run(options, configuration, logWriter, profiler);
                logWriter.LogImportant("... Elapsed {0} sec.", profiler.Results());
            }
            catch (Exception ex)
            {
                if (ex is AggregateException aex)
                {
                    ex = aex.Flatten().InnerException;
                }

                logWriter.LogError(ex.Message);
                logWriter.LogDebug(ex.StackTrace);
                return ExitCode.Error;
            }

            return ExitCode.Success;
        }

        /// <summary>
        /// Loads the configuration of the specified assembly.
        /// </summary>
        private static void LoadAssemblyConfiguration(string assemblyFile, LogWriter logWriter)
        {
            // Load config file and absorb its settings.
            try
            {
                var configFile = System.Configuration.ConfigurationManager.OpenExeConfiguration(assemblyFile);
                var settings = configFile.AppSettings.Settings;
                foreach (var key in settings.AllKeys)
                {
                    if (System.Configuration.ConfigurationManager.AppSettings.Get(key) is null)
                    {
                        System.Configuration.ConfigurationManager.AppSettings.Set(key, settings[key].Value);
                    }
                    else
                    {
                        System.Configuration.ConfigurationManager.AppSettings.Add(key, settings[key].Value);
                    }
                }
            }
            catch (System.Configuration.ConfigurationErrorsException ex)
            {
                logWriter.LogError(ex.Message);
                logWriter.LogDebug(ex.StackTrace);
            }
        }

        /// <summary>
        /// Runs the configured test method by sharding its iterations across worker
        /// processes, then reports the merged result.
        /// </summary>
        private static ExitCode RunTestInParallel(Configuration configuration, LogWriter logWriter)
        {
            string directory = OutputFileManager.CreateOutputDirectory(configuration);
            string fileName = OutputFileManager.GetResolvedFileName(configuration.AssemblyToBeAnalyzed, directory);
            string runDirectory = Path.Combine(directory, "workers");
            Directory.CreateDirectory(runDirectory);

            var coordinator = new ParallelTestCoordinator(configuration, RawArgs, logWriter);
            var stopwatch = Stopwatch.StartNew();
            TestReport report = coordinator.Run(configuration.TestMethodName, runDirectory);
            stopwatch.Stop();

            // Promote the artifacts of the worker that found a bug, so that the reproducible
            // trace is where a sequential run would have left it.
            logWriter.LogImportant("... Emitting execution trace reports:");
            string buggyWorker = coordinator.BuggyWorkerDirectory;
            if (buggyWorker != null)
            {
                foreach (string path in PromoteWorkerArtifacts(configuration, buggyWorker,
                    coordinator.BuggyWorkerArtifactBaseline, directory, fileName))
                {
                    logWriter.LogImportant("..... Writing {0}", path);
                }
            }
            else
            {
                logWriter.LogImportant("..... No test reports available.");
            }

            // Emit the coverage reports from the merged coverage information.
            logWriter.LogImportant("... Emitting coverage reports:");
            if (TestingEngine.TryEmitCoverageReports(configuration, report.CoverageInfo, directory, fileName,
                out IEnumerable<string> coveragePaths))
            {
                foreach (string path in coveragePaths)
                {
                    logWriter.LogImportant("..... Writing {0}", path);
                }
            }
            else
            {
                logWriter.LogImportant("..... No coverage reports available.");
            }

            logWriter.LogImportant(report.GetText(configuration, "..."));
            // Report the workers that actually ran, not the number requested: the plan spawns fewer
            // when there are too few iterations to give each one a whole portfolio rotation.
            logWriter.LogImportant("... Elapsed {0} sec (wall clock across {1} workers).",
                stopwatch.Elapsed.TotalSeconds, coordinator.WorkerCount);
            return GetExitCodeFromTestReport(report);
        }

        /// <summary>
        /// Copies the report artifacts of the specified worker to the top level output
        /// directory, and returns the paths written.
        /// </summary>
        private static IEnumerable<string> PromoteWorkerArtifacts(Configuration configuration,
            string workerDirectory, int artifactBaseline, string directory, string fileName)
        {
            var paths = new List<string>();
            string source = Path.Combine(workerDirectory, "CoyoteOutput");
            if (!Directory.Exists(source))
            {
                return paths;
            }

            // A worker writes into a directory of its own that the coordinator empties first,
            // so its files normally carry the '_0' suffix that the output file manager assigns
            // first. Resolve the suffix from the files actually present rather than assuming
            // it: if the directory could not be emptied, the worker's artifacts are the ones
            // with the highest index, and promoting a stale '_0' would advertise a previous
            // run's trace as the repro for this run's bug.
            string workerStem = GetWorkerArtifactStem(configuration, source, artifactBaseline);
            if (workerStem is null)
            {
                return paths;
            }

            foreach (string path in Directory.GetFiles(source))
            {
                string name = Path.GetFileName(path);
                if (!name.StartsWith(workerStem, StringComparison.Ordinal))
                {
                    continue;
                }

                string target = Path.Combine(directory, fileName + name.Substring(workerStem.Length));
                File.Copy(path, target, true);
                paths.Add(target);
            }

            return paths;
        }

        /// <summary>
        /// Returns the '&lt;assembly&gt;_&lt;index&gt;' stem that the artifacts of the most recent run
        /// in the specified directory carry, or null if the directory holds no such artifact.
        /// </summary>
        private static string GetWorkerArtifactStem(Configuration configuration, string source,
            int artifactBaseline)
        {
            string assembly = Path.GetFileNameWithoutExtension(configuration.AssemblyToBeAnalyzed);
            return ParallelTestArtifacts.GetArtifactStem(assembly,
                Directory.GetFiles(source).Select(Path.GetFileName), artifactBaseline);
        }

        /// <summary>
        /// Registers a callback that stops the specified engine when the coordinator of a
        /// parallel run asks for it, or when that coordinator is no longer running.
        /// </summary>
        /// <remarks>
        /// Does nothing unless this process was launched as a worker. Stopping this way lets
        /// the engine finish its current iteration and emit its report, so the work already
        /// done still counts towards the merged result. The callback runs at every iteration
        /// boundary, so the checks themselves are throttled by the probe rather than performed
        /// here; see <see cref="ParallelWorkerStopProbe"/>.
        /// </remarks>
        private static void RegisterWorkerStopCallback(TestingEngine engine)
        {
            string stopFile = Environment.GetEnvironmentVariable(ParallelTestCoordinator.StopFileVariable);
            if (string.IsNullOrEmpty(stopFile))
            {
                return;
            }

            string parentId = Environment.GetEnvironmentVariable(ParallelTestCoordinator.ParentProcessVariable);
            _ = int.TryParse(parentId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parentProcessId);

            var probe = ParallelWorkerStopProbe.Create(stopFile, parentProcessId);
            engine.RegisterStartIterationCallBack(_ =>
            {
                if (probe.ShouldStop())
                {
                    engine.Stop();
                }
            });
        }

        /// <summary>
        /// Saves the report of the specified engine if this process is a worker of a
        /// parallel run.
        /// </summary>
        private static void SaveWorkerReport(TestingEngine engine)
        {
            string reportFile = Environment.GetEnvironmentVariable(ParallelTestCoordinator.ReportFileVariable);
            if (!string.IsNullOrEmpty(reportFile))
            {
                engine.TestReport.Save(reportFile);
            }
        }

        /// <summary>
        /// Callback invoked when an unhandled exception occurs.
        /// </summary>
        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args) =>
            Environment.Exit((int)ExitCode.InternalError);

        private static ExitCode GetExitCodeFromTestReport(TestReport report) =>
            report.InternalErrors.Count > 0 ? ExitCode.InternalError :
            report.NumOfFoundBugs > 0 ? ExitCode.BugFound :
            ExitCode.Success;
    }
}

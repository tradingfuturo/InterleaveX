// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
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
        private static int Main(string[] args)
        {
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

                        using TestingEngine engine = new TestingEngine(configuration, logWriter);
                        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
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

                        logWriter.LogImportant(engine.TestReport.GetText(configuration, "..."));
                        logWriter.LogImportant("... Elapsed {0} sec.", engine.Profiler.Results());
                        testExitCode = GetExitCodeFromTestReport(engine.TestReport);
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

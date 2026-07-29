// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Actors;
using Microsoft.Coyote.Actors.Coverage;
using Microsoft.Coyote.Logging;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.SystematicTesting;
using Microsoft.Coyote.SystematicTesting.Frameworks.XUnit;
using Xunit;
using Xunit.Abstractions;
using ActorRuntimeFactory = Microsoft.Coyote.Actors.RuntimeFactory;

namespace Microsoft.Coyote.Tests.Common
{
    public delegate void TestErrorChecker(string error);

    public abstract class BaseTest
    {
        /// <summary>
        /// Names the seed every test starts its exploration from, or 'random' for a fresh one per run.
        /// </summary>
        private const string RandomSeedVariable = "COYOTE_TEST_SEED";

        /// <summary>
        /// Where the starting seed of a test comes from when its configuration does not name one.
        /// </summary>
        private enum RandomSeedSource
        {
            /// <summary>
            /// Derived from the test's own identity, so a test explores the same schedules on every
            /// run while still differing from every other test.
            /// </summary>
            Derived,

            /// <summary>
            /// The one named by <see cref="RandomSeedVariable"/>, for reproducing a specific run.
            /// </summary>
            Fixed,

            /// <summary>
            /// Left unset, so the runtime derives one from a new <see cref="Guid"/> as it used to.
            /// </summary>
            Random
        }

        /// <summary>
        /// Where starting seeds come from in this process, and the one to use when it is fixed.
        /// </summary>
        private sealed class RandomSeedSetting
        {
            internal RandomSeedSource Source { get; }

            internal uint Seed { get; }

            private RandomSeedSetting(RandomSeedSource source, uint seed)
            {
                this.Source = source;
                this.Seed = seed;
            }

            /// <summary>
            /// Reads <see cref="RandomSeedVariable"/> and returns what it asks for.
            /// </summary>
            /// <remarks>
            /// A value that is neither a number nor 'random' throws rather than falling back,
            /// because the whole point of naming a seed is to reproduce a particular run: quietly
            /// exploring a different one would answer that question with the wrong run's result.
            /// </remarks>
            internal static RandomSeedSetting Resolve()
            {
                string value = Environment.GetEnvironmentVariable(RandomSeedVariable)?.Trim();
                if (string.IsNullOrEmpty(value))
                {
                    return new RandomSeedSetting(RandomSeedSource.Derived, 0);
                }

                if (string.Equals(value, "random", StringComparison.OrdinalIgnoreCase))
                {
                    return new RandomSeedSetting(RandomSeedSource.Random, 0);
                }

                if (uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint parsed))
                {
                    return new RandomSeedSetting(RandomSeedSource.Fixed, parsed);
                }

                throw new InvalidOperationException(
                    $"'{RandomSeedVariable}' must be an unsigned integer or 'random', but was '{value}'.");
            }
        }

        private static readonly RandomSeedSetting DefaultSeed = RandomSeedSetting.Resolve();

        /// <summary>
        /// True when this run was asked to explore from a new seed each time, as it used to.
        /// </summary>
        /// <remarks>
        /// The nightly run sets this, so that the exploration a fixed seed gives up still happens
        /// somewhere. Tests that assert on the seeding itself have to know which of the two they are
        /// running under, or they assert the opposite of what was asked for and fail exactly on the
        /// run nobody watches.
        /// </remarks>
        private protected static bool IsRandomSeedRequested => DefaultSeed.Source is RandomSeedSource.Random;

        protected readonly ITestOutputHelper TestOutput;

        /// <summary>
        /// The seed drawn for this test when the run asked for a new one each time, kept so that the
        /// test explores from a single seed and reports that same one.
        /// </summary>
        private uint? RandomModeSeed;

        /// <summary>
        /// Whether the seed this test explores from has already been reported, so that a test which
        /// builds several configurations says it once rather than once each.
        /// </summary>
        private bool HasLoggedRandomSeed;

        /// <summary>
        /// The identity recovered from the output helper, and whether it has been looked for yet.
        /// </summary>
        /// <remarks>
        /// Two fields rather than one, because null is an answer here: it means the search ran and
        /// found nothing, which is not the same as not having run. It is fixed for the lifetime of
        /// this instance, and finding it means a reflection sweep over the whole helper hierarchy,
        /// so it is found once rather than once per configuration a test builds.
        /// </remarks>
        private string ResolvedTestIdentity;
        private bool HasResolvedTestIdentity;

        public BaseTest(ITestOutputHelper output)
        {
            this.TestOutput = output;
        }

        /// <summary>
        /// Override to change the test scheduling policy used by the <see cref="TestingEngine"/>.
        /// By default this value is <see cref="SchedulingPolicy.None"/>.
        /// </summary>
        private protected virtual SchedulingPolicy SchedulingPolicy => SchedulingPolicy.None;

        protected void Test(Action test, Configuration configuration = null)
        {
            if (this.SchedulingPolicy is SchedulingPolicy.None)
            {
                this.RunTest((r) => test(), configuration);
            }
            else
            {
                this.RunSystematicTest(test, configuration, null, null);
            }
        }

        protected void Test(Action<IActorRuntime> test, Configuration configuration = null)
        {
            if (this.SchedulingPolicy is SchedulingPolicy.None)
            {
                this.RunTest(test, configuration);
            }
            else
            {
                this.RunSystematicTest(test, configuration, null, null);
            }
        }

        protected void Test(Func<Task> test, Configuration configuration = null)
        {
            if (this.SchedulingPolicy is SchedulingPolicy.None)
            {
                this.RunAsync(async (r) => await test(), configuration).Wait();
            }
            else
            {
                this.RunSystematicTest(test, configuration, null, null);
            }
        }

        protected void Test(Func<IActorRuntime, Task> test, Configuration configuration = null)
        {
            if (this.SchedulingPolicy is SchedulingPolicy.None)
            {
                this.RunAsync(test, configuration).Wait();
            }
            else
            {
                this.RunSystematicTest(test, configuration, null, null);
            }
        }

        protected TestReport RunSystematicTest(Action test, Configuration configuration = null) =>
            this.RunSystematicTest(test as Delegate, configuration, null, null);

        protected TestReport RunSystematicTest(Func<Task> test, Configuration configuration = null) =>
            this.RunSystematicTest(test as Delegate, configuration, null, null);

        protected TestReport RunSystematicTest(Action test, Configuration configuration = null,
            Action<uint> startIterationCallBack = null, Action<uint> endIterationCallBack = null) =>
            this.RunSystematicTest(test as Delegate, configuration, startIterationCallBack, endIterationCallBack);

        protected TestReport RunSystematicTest(Func<Task> test, Configuration configuration = null,
            Action<uint> startIterationCallBack = null, Action<uint> endIterationCallBack = null) =>
            this.RunSystematicTest(test as Delegate, configuration, startIterationCallBack, endIterationCallBack);

        private TestReport RunSystematicTest(Delegate test, Configuration configuration,
            Action<uint> startIterationCallBack, Action<uint> endIterationCallBack)
        {
            configuration ??= this.GetConfiguration();

            using var logger = new TestOutputLogger(this.TestOutput);
            try
            {
                using TestingEngine engine = this.RunTestingEngine(test, configuration,
                    startIterationCallBack, endIterationCallBack, logger);
                if (!configuration.RunTestIterationsToCompletion)
                {
                    var numErrors = engine.TestReport.NumOfFoundBugs;
                    Assert.True(numErrors is 0, GetBugReport(engine));
                }

                return engine.TestReport;
            }
            catch (Exception ex)
            {
                Assert.False(true, ex.Message + "\n" + ex.StackTrace);
            }

            return null;
        }

        protected string TestCoverage(Action<IActorRuntime> test, Configuration configuration)
        {
            TestReport report = this.RunSystematicTest(test, configuration, null, null);
            using var writer = new StringWriter();
            var coverageReporter = new ActorCoverageReporter(report.CoverageInfo);
            coverageReporter.WriteActivityCoverageText(writer);
            string result = writer.ToString().RemoveNamespaceReferences();
            return result;
        }

        protected void TestWithError(Action test, Configuration configuration = null, string expectedError = null,
            bool replay = false)
        {
            if (this.SchedulingPolicy is SchedulingPolicy.None)
            {
                this.RunWithErrors((r) => test(), configuration, (e) => { CheckSingleError(e, expectedError); });
            }
            else
            {
                this.RunSystematicTestWithErrors(test, configuration, (e) => { CheckSingleError(e, expectedError); }, replay);
            }
        }

        protected void TestWithError(Action<IActorRuntime> test, Configuration configuration = null,
            string expectedError = null, bool replay = false)
        {
            if (this.SchedulingPolicy is SchedulingPolicy.None)
            {
                this.RunWithErrors(test, configuration, (e) => { CheckSingleError(e, expectedError); });
            }
            else
            {
                this.RunSystematicTestWithErrors(test, configuration, (e) => { CheckSingleError(e, expectedError); }, replay);
            }
        }

        protected void TestWithError(Func<Task> test, Configuration configuration = null, string expectedError = null,
            bool replay = false)
        {
            if (this.SchedulingPolicy is SchedulingPolicy.None)
            {
                this.RunWithErrorsAsync(async (r) => await test(), configuration, (e) => { CheckSingleError(e, expectedError); }).Wait();
            }
            else
            {
                this.RunSystematicTestWithErrors(test, configuration, (e) => { CheckSingleError(e, expectedError); }, replay);
            }
        }

        protected void TestWithError(Func<IActorRuntime, Task> test, Configuration configuration = null,
            string expectedError = null, bool replay = false)
        {
            if (this.SchedulingPolicy is SchedulingPolicy.None)
            {
                this.RunWithErrorsAsync(test, configuration, (e) => { CheckSingleError(e, expectedError); }).Wait();
            }
            else
            {
                this.RunSystematicTestWithErrors(test, configuration, (e) => { CheckSingleError(e, expectedError); }, replay);
            }
        }

        protected void TestWithError(Action test, Configuration configuration = null, string[] expectedErrors = null,
            bool replay = false)
        {
            if (this.SchedulingPolicy is SchedulingPolicy.None)
            {
                this.RunWithErrors((r) => test(), configuration, (e) => { CheckMultipleErrors(e, expectedErrors); });
            }
            else
            {
                this.RunSystematicTestWithErrors(test, configuration, (e) => { CheckMultipleErrors(e, expectedErrors); }, replay);
            }
        }

        protected void TestWithError(Action<IActorRuntime> test, Configuration configuration = null,
            string[] expectedErrors = null, bool replay = false)
        {
            if (this.SchedulingPolicy is SchedulingPolicy.None)
            {
                this.RunWithErrors(test, configuration, (e) => { CheckMultipleErrors(e, expectedErrors); });
            }
            else
            {
                this.RunSystematicTestWithErrors(test, configuration, (e) => { CheckMultipleErrors(e, expectedErrors); }, replay);
            }
        }

        protected void TestWithError(Func<Task> test, Configuration configuration = null, string[] expectedErrors = null,
            bool replay = false)
        {
            if (this.SchedulingPolicy is SchedulingPolicy.None)
            {
                this.RunWithErrorsAsync(async (r) => await test(), configuration, (e) => { CheckMultipleErrors(e, expectedErrors); }).Wait();
            }
            else
            {
                this.RunSystematicTestWithErrors(test, configuration, (e) => { CheckMultipleErrors(e, expectedErrors); }, replay);
            }
        }

        protected void TestWithError(Func<IActorRuntime, Task> test, Configuration configuration = null,
            string[] expectedErrors = null, bool replay = false)
        {
            if (this.SchedulingPolicy is SchedulingPolicy.None)
            {
                this.RunWithErrorsAsync(test, configuration, (e) => { CheckMultipleErrors(e, expectedErrors); }).Wait();
            }
            else
            {
                this.RunSystematicTestWithErrors(test, configuration, (e) => { CheckMultipleErrors(e, expectedErrors); }, replay);
            }
        }

        protected void TestWithError(Action test, TestErrorChecker errorChecker, Configuration configuration = null,
            bool replay = false)
        {
            if (this.SchedulingPolicy is SchedulingPolicy.None)
            {
                this.RunWithErrors((r) => test(), configuration, errorChecker);
            }
            else
            {
                this.RunSystematicTestWithErrors(test, configuration, errorChecker, replay);
            }
        }

        protected void TestWithError(Action<IActorRuntime> test, TestErrorChecker errorChecker, Configuration configuration = null,
            bool replay = false)
        {
            if (this.SchedulingPolicy is SchedulingPolicy.None)
            {
                this.RunWithErrors(test, configuration, errorChecker);
            }
            else
            {
                this.RunSystematicTestWithErrors(test, configuration, errorChecker, replay);
            }
        }

        protected void TestWithError(Func<Task> test, TestErrorChecker errorChecker, Configuration configuration = null,
            bool replay = false)
        {
            if (this.SchedulingPolicy is SchedulingPolicy.None)
            {
                this.RunWithErrorsAsync(async (r) => await test(), configuration, errorChecker).Wait();
            }
            else
            {
                this.RunSystematicTestWithErrors(test, configuration, errorChecker, replay);
            }
        }

        protected void TestWithError(Func<IActorRuntime, Task> test, TestErrorChecker errorChecker, Configuration configuration = null,
            bool replay = false)
        {
            if (this.SchedulingPolicy is SchedulingPolicy.None)
            {
                this.RunWithErrorsAsync(test, configuration, errorChecker).Wait();
            }
            else
            {
                this.RunSystematicTestWithErrors(test, configuration, errorChecker, replay);
            }
        }

        private void RunSystematicTestWithErrors(Delegate test, Configuration configuration, TestErrorChecker errorChecker, bool replay)
        {
            configuration ??= this.GetConfiguration();
            if (this.SchedulingPolicy is SchedulingPolicy.Fuzzing)
            {
                // Increase iterations during fuzzing as some bugs might be harder to be found.
                configuration = configuration.WithTestingIterations(configuration.TestingIterations * 50);
            }

            using var logger = new TestOutputLogger(this.TestOutput);
            try
            {
                using TestingEngine engine = this.RunTestingEngine(test, configuration, null, null, logger);
                CheckErrors(engine, errorChecker);

                if (replay && this.SchedulingPolicy is SchedulingPolicy.Interleaving)
                {
                    configuration.WithReproducibleTrace(engine.ReproducibleTrace);
                    using TestingEngine replayEngine = this.RunTestingEngine(test, configuration, null, null, logger);
                    if (engine.TestReport.NumOfFoundBugs is 0)
                    {
                        this.TestOutput.WriteLine(engine.ReproducibleTrace);
                    }

                    string replayError = replayEngine.Scheduler.GetLastError();
                    Assert.True(replayError.Length is 0, replayError);
                    CheckErrors(replayEngine, errorChecker);
                }
            }
            catch (Exception ex)
            {
                Assert.False(true, ex.Message + "\n" + ex.StackTrace);
            }
        }

        protected void TestWithException<TException>(Action test, Configuration configuration = null, bool replay = false)
            where TException : Exception
        {
            if (this.SchedulingPolicy is SchedulingPolicy.None)
            {
                this.RunTestWithException<TException>(test, configuration);
            }
            else
            {
                this.RunSystematicTestWithException<TException>(test, configuration, replay);
            }
        }

        protected void TestWithException<TException>(Action<IActorRuntime> test, Configuration configuration = null,
            bool replay = false)
            where TException : Exception
        {
            if (this.SchedulingPolicy is SchedulingPolicy.None)
            {
                this.RunTestWithException<TException>(test, configuration);
            }
            else
            {
                this.RunSystematicTestWithException<TException>(test, configuration, replay);
            }
        }

        protected void TestWithException<TException>(Func<Task> test, Configuration configuration = null, bool replay = false)
            where TException : Exception
        {
            if (this.SchedulingPolicy is SchedulingPolicy.None)
            {
                this.RunTestWithExceptionAsync<TException>(test, configuration).Wait();
            }
            else
            {
                this.RunSystematicTestWithException<TException>(test, configuration, replay);
            }
        }

        protected void TestWithException<TException>(Func<IActorRuntime, Task> test, Configuration configuration = null,
            bool replay = false)
            where TException : Exception
        {
            if (this.SchedulingPolicy is SchedulingPolicy.None)
            {
                this.RunTestWithExceptionAsync<TException>(test, configuration).Wait();
            }
            else
            {
                this.RunSystematicTestWithException<TException>(test, configuration, replay);
            }
        }

        private void RunSystematicTestWithException<TException>(Delegate test, Configuration configuration = null, bool replay = false)
            where TException : Exception
        {
            configuration ??= this.GetConfiguration();
            if (this.SchedulingPolicy is SchedulingPolicy.Fuzzing)
            {
                // Increase iterations during fuzzing as some bugs might be harder to be found.
                configuration = configuration.WithTestingIterations(configuration.TestingIterations * 50);
            }

            Type exceptionType = typeof(TException);
            Assert.True(exceptionType.IsSubclassOf(typeof(Exception)), "Please configure the test correctly. " +
                $"Type '{exceptionType}' is not an exception type.");

            using var logger = new TestOutputLogger(this.TestOutput);
            try
            {
                using TestingEngine engine = this.RunTestingEngine(test, configuration, null, null, logger);
                CheckErrors(engine, exceptionType);

                if (replay && this.SchedulingPolicy is SchedulingPolicy.Interleaving)
                {
                    configuration.WithReproducibleTrace(engine.ReproducibleTrace);
                    using TestingEngine replayEngine = this.RunTestingEngine(test, configuration, null, null, logger);
                    if (engine.TestReport.NumOfFoundBugs is 0)
                    {
                        this.TestOutput.WriteLine(engine.ReproducibleTrace);
                    }

                    string replayError = replayEngine.Scheduler.GetLastError();
                    Assert.True(replayError.Length is 0, replayError);
                    CheckErrors(replayEngine, exceptionType);
                }
            }
            catch (Exception ex)
            {
                Assert.False(true, ex.Message + "\n" + ex.StackTrace);
            }
        }

        protected void RunTest(Action<IActorRuntime> test, Configuration configuration = null)
        {
            configuration ??= this.GetConfiguration();
            configuration.WithActorQuiescenceCheckingEnabledOutsideTesting();
            configuration.WithMonitoringEnabledOutsideTesting();

            using var logger = new TestOutputLogger(this.TestOutput);
            try
            {
                var runtime = ActorRuntimeFactory.Create(configuration);
                runtime.Logger = logger;
                for (int i = 0; i < configuration.TestingIterations; i++)
                {
                    test(runtime);
                }
            }
            catch (Exception ex)
            {
                Assert.False(true, ex.Message + "\n" + ex.StackTrace);
            }
        }

        protected async Task RunAsync(Func<IActorRuntime, Task> test, Configuration configuration = null, bool handleFailures = true)
        {
            configuration ??= this.GetConfiguration();
            configuration.WithActorQuiescenceCheckingEnabledOutsideTesting();
            configuration.WithMonitoringEnabledOutsideTesting();

            uint iterations = Math.Max(1, configuration.TestingIterations);
            for (int i = 0; i < iterations; i++)
            {
                using var logger = new TestOutputLogger(this.TestOutput);
                try
                {
                    var runtime = ActorRuntimeFactory.Create(configuration);
                    if (!configuration.IsConsoleLoggingEnabled)
                    {
                        runtime.Logger = logger;
                    }

                    var errorTask = new TaskCompletionSource<Exception>();
                    if (handleFailures)
                    {
                        runtime.OnFailure += (e) =>
                        {
                            errorTask.TrySetResult(Unwrap(e));
                        };
                    }

                    // TODO: but is this actually letting the test complete in the case
                    // of actors which run completely asynchronously?
                    await await Task.WhenAny(test(runtime), errorTask.Task);
                    if (handleFailures && errorTask.Task.IsCompleted)
                    {
                        Assert.False(true, errorTask.Task.Result.Message);
                    }
                }
                catch (Exception ex)
                {
                    Exception e = Unwrap(ex);
                    Assert.False(true, e.Message + "\n" + e.StackTrace);
                }
            }
        }

        private static Exception Unwrap(Exception ex)
        {
            Exception e = ex;
            if (e is AggregateException ae)
            {
                e = ae.InnerException;
            }
            else if (e is ActionExceptionFilterException fe)
            {
                e = fe.InnerException;
            }

            return e;
        }

        private static string ExtractErrorMessage(Exception ex)
        {
            if (ex is ActionExceptionFilterException actionException)
            {
                ex = actionException.InnerException;
            }

            var msg = ex.Message;
            if (ex is AggregateException ae)
            {
                StringBuilder sb = new StringBuilder();
                foreach (var e in ae.InnerExceptions)
                {
                    sb.AppendLine(e.Message);
                }

                msg = sb.ToString();
            }

            return msg;
        }

        private void RunWithErrors(Action<IActorRuntime> test, Configuration configuration, TestErrorChecker errorChecker)
        {
            configuration ??= this.GetConfiguration();
            configuration.WithActorQuiescenceCheckingEnabledOutsideTesting();
            configuration.WithMonitoringEnabledOutsideTesting();

            string errorMessage = string.Empty;
            using var logger = new TestOutputLogger(this.TestOutput);
            try
            {
                var runtime = ActorRuntimeFactory.Create(configuration);
                var errorTask = new TaskCompletionSource<Exception>();
                runtime.OnFailure += (e) =>
                {
                    errorTask.TrySetResult(e);
                };

                runtime.Logger = logger;
                for (int i = 0; i < configuration.TestingIterations; i++)
                {
                    test(runtime);
                    if (configuration.TestingIterations is 1)
                    {
                        Assert.True(errorTask.Task.Wait(GetErrorWaitingTimeout()), "Timeout waiting for error");
                        errorMessage = ExtractErrorMessage(errorTask.Task.Result);
                    }
                }
            }
            catch (Exception ex)
            {
                errorMessage = ExtractErrorMessage(ex);
            }

            if (string.IsNullOrEmpty(errorMessage))
            {
                Assert.True(false, string.Format("Error not found after all {0} test iterations", configuration.TestingIterations));
            }

            errorChecker(errorMessage);
        }

        private async Task RunWithErrorsAsync(Func<IActorRuntime, Task> test, Configuration configuration, TestErrorChecker errorChecker)
        {
            configuration ??= this.GetConfiguration();
            configuration.WithActorQuiescenceCheckingEnabledOutsideTesting();
            configuration.WithMonitoringEnabledOutsideTesting();

            string errorMessage = string.Empty;
            using var logger = new TestOutputLogger(this.TestOutput);
            try
            {
                var runtime = ActorRuntimeFactory.Create(configuration);
                var errorCompletion = new TaskCompletionSource<Exception>();
                runtime.OnFailure += (e) =>
                {
                    errorCompletion.TrySetResult(e);
                };

                runtime.Logger = logger;
                for (int i = 0; i < configuration.TestingIterations; i++)
                {
                    await test(runtime);
                    if (configuration.TestingIterations is 1)
                    {
                        Assert.True(errorCompletion.Task.Wait(GetErrorWaitingTimeout()), "Timeout waiting for error");
                        errorMessage = ExtractErrorMessage(errorCompletion.Task.Result);
                    }
                }
            }
            catch (Exception ex)
            {
                errorMessage = ExtractErrorMessage(ex);
            }

            if (string.IsNullOrEmpty(errorMessage))
            {
                Assert.True(false, string.Format("Error not found after all {0} test iterations", configuration.TestingIterations));
            }

            errorChecker(errorMessage);
        }

        protected void RunTestWithException<TException>(Action<IActorRuntime> test, Configuration configuration = null)
        {
            configuration ??= this.GetConfiguration();
            configuration.WithActorQuiescenceCheckingEnabledOutsideTesting();
            configuration.WithMonitoringEnabledOutsideTesting();

            Exception actualException = null;
            Type exceptionType = typeof(TException);
            Assert.True(exceptionType.IsSubclassOf(typeof(Exception)), "Please configure the test correctly. " +
                $"Type '{exceptionType}' is not an exception type.");

            using var logger = new TestOutputLogger(this.TestOutput);
            try
            {
                var runtime = ActorRuntimeFactory.Create(configuration);
                var errorCompletion = new TaskCompletionSource<Exception>();
                runtime.OnFailure += (e) =>
                {
                    errorCompletion.TrySetResult(e);
                };
                runtime.Logger = logger;
                for (int i = 0; i < configuration.TestingIterations; i++)
                {
                    test(runtime);
                    if (configuration.TestingIterations is 1)
                    {
                        Assert.True(errorCompletion.Task.Wait(GetErrorWaitingTimeout()), "Timeout waiting for error");
                        actualException = errorCompletion.Task.Result;
                    }
                }
            }
            catch (Exception ex)
            {
                actualException = ex;
            }

            if (actualException is null)
            {
                Assert.True(false, string.Format("Error not found after all {0} test iterations", configuration.TestingIterations));
            }

            Assert.True(actualException.GetType() == exceptionType, actualException.Message + "\n" + actualException.StackTrace);
        }

        protected void RunTestWithException<TException>(Action test, Configuration configuration = null)
        {
            configuration ??= this.GetConfiguration();
            configuration.WithActorQuiescenceCheckingEnabledOutsideTesting();
            configuration.WithMonitoringEnabledOutsideTesting();

            Exception actualException = null;
            Type exceptionType = typeof(TException);
            Assert.True(exceptionType.IsSubclassOf(typeof(Exception)), "Please configure the test correctly. " +
                $"Type '{exceptionType}' is not an exception type.");

            using var logger = new TestOutputLogger(this.TestOutput);
            try
            {
                var runtime = ActorRuntimeFactory.Create(configuration);
                var errorCompletion = new TaskCompletionSource<Exception>();
                runtime.OnFailure += (e) =>
                {
                    errorCompletion.TrySetResult(e);
                };
                runtime.Logger = logger;
                for (int i = 0; i < configuration.TestingIterations; i++)
                {
                    test();
                    if (configuration.TestingIterations is 1)
                    {
                        Assert.True(errorCompletion.Task.Wait(GetErrorWaitingTimeout()), "Timeout waiting for error");
                        actualException = errorCompletion.Task.Result;
                    }
                }
            }
            catch (Exception ex)
            {
                actualException = ex;
            }

            if (actualException is null)
            {
                Assert.True(false, string.Format("Error not found after all {0} test iterations", configuration.TestingIterations));
            }

            Assert.True(actualException.GetType() == exceptionType, actualException.Message + "\n" + actualException.StackTrace);
        }

        protected async Task RunTestWithExceptionAsync<TException>(Func<IActorRuntime, Task> test, Configuration configuration = null)
        {
            configuration ??= this.GetConfiguration();
            configuration.WithActorQuiescenceCheckingEnabledOutsideTesting();
            configuration.WithMonitoringEnabledOutsideTesting();

            Exception actualException = null;
            Type exceptionType = typeof(TException);
            Assert.True(exceptionType.IsSubclassOf(typeof(Exception)), "Please configure the test correctly. " +
                $"Type '{exceptionType}' is not an exception type.");

            using var logger = new TestOutputLogger(this.TestOutput);
            try
            {
                var runtime = ActorRuntimeFactory.Create(configuration);
                var errorCompletion = new TaskCompletionSource<Exception>();
                runtime.OnFailure += (e) =>
                {
                    errorCompletion.TrySetResult(e);
                };

                runtime.Logger = logger;
                for (int i = 0; i < configuration.TestingIterations; i++)
                {
                    await test(runtime);

                    if (configuration.TestingIterations is 1)
                    {
                        Assert.True(errorCompletion.Task.Wait(GetErrorWaitingTimeout()), "Timeout waiting for error");
                        actualException = errorCompletion.Task.Result;
                    }
                }
            }
            catch (Exception ex)
            {
                actualException = ex;
            }

            if (actualException is null)
            {
                Assert.True(false, string.Format("Error not found after all {0} test iterations", configuration.TestingIterations));
            }

            Assert.True(actualException.GetType() == exceptionType, actualException.Message + "\n" + actualException.StackTrace);
        }

        protected async Task RunTestWithExceptionAsync<TException>(Func<Task> test, Configuration configuration = null)
        {
            configuration ??= this.GetConfiguration();
            configuration.WithActorQuiescenceCheckingEnabledOutsideTesting();
            configuration.WithMonitoringEnabledOutsideTesting();

            Exception actualException = null;
            Type exceptionType = typeof(TException);
            Assert.True(exceptionType.IsSubclassOf(typeof(Exception)), "Please configure the test correctly. " +
                $"Type '{exceptionType}' is not an exception type.");

            using var logger = new TestOutputLogger(this.TestOutput);
            try
            {
                var runtime = ActorRuntimeFactory.Create(configuration);
                var errorCompletion = new TaskCompletionSource<Exception>();
                runtime.OnFailure += (e) =>
                {
                    errorCompletion.TrySetResult(e);
                };

                runtime.Logger = logger;
                for (int i = 0; i < configuration.TestingIterations; i++)
                {
                    await test();

                    if (configuration.TestingIterations is 1)
                    {
                        Assert.True(errorCompletion.Task.Wait(GetErrorWaitingTimeout()), "Timeout waiting for error");
                        actualException = errorCompletion.Task.Result;
                    }
                }
            }
            catch (Exception ex)
            {
                actualException = ex;
            }

            if (actualException is null)
            {
                Assert.True(false, string.Format("Error not found after all {0} test iterations", configuration.TestingIterations));
            }

            Assert.True(actualException.GetType() == exceptionType, actualException.Message + "\n" + actualException.StackTrace);
        }

        /// <summary>
        /// Returns the identity xunit knows this test by, or null if it cannot be recovered.
        /// </summary>
        /// <remarks>
        /// xunit hands each test an <see cref="ITestOutputHelper"/> that holds the test it belongs
        /// to, but exposes it on the concrete helper rather than on the interface, so it is reached
        /// by reflection. Searched for by type rather than by member name so that a rename in xunit
        /// costs nothing. What notices if it stops being reachable at all is RandomSeedTests in
        /// Tests.Runtime: the fallback below is a quieter answer than it looks, since it silently
        /// turns one seed per test into one seed per class.
        /// </remarks>
        private protected string GetTestIdentity()
        {
            if (!this.HasResolvedTestIdentity)
            {
                this.HasResolvedTestIdentity = true;
                this.ResolvedTestIdentity = this.FindTestIdentity();
            }

            return this.ResolvedTestIdentity;
        }

        /// <summary>
        /// Returns everything written to this test's own output so far, or null where xunit does not
        /// expose it.
        /// </summary>
        /// <remarks>
        /// Reached by reflection for the same reason the identity above is: the concrete helper lives
        /// in xunit's execution assembly, which is not referenced here, and the interface it is held
        /// through can only be written to. What reads this is the check that the seed a failing test
        /// explored from is actually written into that test's output, which is where the nightly run
        /// tells people to look for it.
        /// </remarks>
        private protected string GetReportedOutput()
        {
            if (this.TestOutput is null)
            {
                return null;
            }

            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (Type type = this.TestOutput.GetType(); type != null; type = type.BaseType)
            {
                var property = type.GetProperty("Output", Flags);
                if (property != null && property.CanRead && property.PropertyType == typeof(string))
                {
                    try
                    {
                        return property.GetValue(this.TestOutput) as string;
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Searches the output helper for the identity xunit knows this test by.
        /// </summary>
        private string FindTestIdentity()
        {
            if (this.TestOutput is null)
            {
                return null;
            }

            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (Type type = this.TestOutput.GetType(); type != null; type = type.BaseType)
            {
                foreach (var field in type.GetFields(Flags))
                {
                    if (field.GetValue(this.TestOutput) is ITest fieldTest)
                    {
                        return fieldTest.DisplayName;
                    }
                }

                foreach (var property in type.GetProperties(Flags))
                {
                    if (property.CanRead && property.GetIndexParameters().Length is 0)
                    {
                        object value;
                        try
                        {
                            value = property.GetValue(this.TestOutput);
                        }
                        catch (Exception)
                        {
                            // A property that cannot be read here tells us nothing about the test.
                            continue;
                        }

                        if (value is ITest propertyTest)
                        {
                            return propertyTest.DisplayName;
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Returns the seed this test starts its exploration from.
        /// </summary>
        /// <remarks>
        /// Hashed with FNV-1a rather than with <c>string.GetHashCode</c>, which is randomized
        /// per process and would make the derived seed differ between two runs of the same test --
        /// the one thing this exists to prevent.
        /// </remarks>
        private protected uint GetDefaultRandomSeed()
        {
            if (DefaultSeed.Source is RandomSeedSource.Fixed)
            {
                return DefaultSeed.Seed;
            }

            if (DefaultSeed.Source is RandomSeedSource.Random)
            {
                // Drawn here rather than left to the runtime, which would derive one from a fresh
                // guid and keep it. Both explore somewhere new on every run, which is the whole point
                // of asking for 'random'; the difference is that this one is a value the test can
                // report, and a run whose seed was never written down is a bug nobody can reproduce.
                //
                // Memoized so that a test asking twice gets one answer, and so that a test building
                // several configurations explores from one seed rather than a different one each.
                this.RandomModeSeed ??= (uint)Guid.NewGuid().GetHashCode();
                return this.RandomModeSeed.Value;
            }

            // Falling back to the class means every test in it starts from one seed. That is worse
            // than one seed per test, but it is still the same seed on every run, which is what the
            // reproducibility depends on.
            return ComputeFnv1a(this.GetTestIdentity() ?? this.GetType().FullName);
        }

        /// <summary>
        /// Returns the 32 bit FNV-1a hash of the specified value.
        /// </summary>
        /// <remarks>
        /// Spelled out rather than taken from the framework so that it can be checked against the
        /// published vectors for this algorithm. A hash that is only ever compared against itself
        /// would still look right after being changed into something that is not stable at all.
        /// </remarks>
        private protected static uint ComputeFnv1a(string value)
        {
            const uint OffsetBasis = 2166136261;
            const uint Prime = 16777619;

            uint hash = OffsetBasis;
            unchecked
            {
                foreach (byte b in Encoding.UTF8.GetBytes(value))
                {
                    hash = (hash ^ b) * Prime;
                }
            }

            return hash;
        }

        /// <summary>
        /// Returns the specified configuration with a starting seed, unless it already names one.
        /// </summary>
        /// <remarks>
        /// Applied here, at the construction of the engine, rather than in <see cref="GetConfiguration"/>:
        /// a good number of tests build a <see cref="Configuration"/> of their own and never go
        /// through that method, and those are exactly the ones nobody would think to check.
        ///
        /// A configuration that already names a seed keeps it. Those tests are pinned deliberately,
        /// usually because they assert on the exploration a particular seed produces.
        ///
        /// A run asking for 'random' is seeded here too, from a fresh draw rather than from the
        /// test's name. Leaving it unset explored just as widely, but the seed then existed only
        /// inside the runtime's generator, reaching the log as part of a strategy description
        /// written for people to read. What the nightly run needs is the opposite -- a seed written
        /// down in one place, in the same words in both modes, because turning a failure there back
        /// into a test somebody can run is the entire reason that run exists.
        /// </remarks>
        private protected Configuration WithDefaultRandomSeed(Configuration configuration)
        {
            if (configuration is null || configuration.RandomGeneratorSeed.HasValue)
            {
                return configuration;
            }

            uint seed = this.GetDefaultRandomSeed();
            if (!this.HasLoggedRandomSeed)
            {
                this.HasLoggedRandomSeed = true;

                // Written to the test's own output so that a failure carries the seed that produced
                // it, which is what makes the run reproducible rather than merely repeatable.
                try
                {
                    this.TestOutput?.WriteLine($"... Using random generator seed {seed}.");
                }
                catch (Exception)
                {
                    // The helper refuses to write outside a running test, which says nothing about
                    // the seed and is not worth failing over.
                }
            }

            return configuration.WithRandomGeneratorSeed(seed);
        }

        private TestingEngine RunTestingEngine(Delegate test, Configuration configuration,
            Action<uint> startIterationCallBack, Action<uint> endIterationCallBack, TestOutputLogger logger)
        {
            configuration = this.WithDefaultRandomSeed(configuration);
            var logWriter = new LogWriter(configuration);
            var engine = new TestingEngine(configuration, test, logWriter);
            if (!configuration.IsConsoleLoggingEnabled)
            {
                engine.SetLogger(logger);
            }

            if (startIterationCallBack != null)
            {
                engine.RegisterStartIterationCallBack(startIterationCallBack);
            }

            if (endIterationCallBack != null)
            {
                engine.RegisterEndIterationCallBack(endIterationCallBack);
            }

            engine.Run();
            return engine;
        }

        private static void CheckSingleError(string actual, string expected)
        {
            var a = actual.RemoveNonDeterministicValues();
            var b = expected.RemoveNonDeterministicValues();
            Assert.Equal(b, a);
        }

        private static void CheckMultipleErrors(string actual, string[] expectedErrors)
        {
            var stripped = actual.RemoveNonDeterministicValues();
            try
            {
                Assert.Contains(expectedErrors, (e) => e.RemoveNonDeterministicValues() == stripped);
            }
            catch (Exception)
            {
                throw new Exception("Actual string was not in the expected list: " + actual);
            }
        }

        private static void CheckErrors(TestingEngine engine, TestErrorChecker errorChecker)
        {
            Assert.True(engine.TestReport.NumOfFoundBugs > 0, "Expected bugs to be found, but we found none");
            foreach (var bugReport in engine.TestReport.BugReports)
            {
                errorChecker(bugReport);
            }
        }

        private static void CheckErrors(TestingEngine engine, Type exceptionType)
        {
            Assert.Equal(1, engine.TestReport.NumOfFoundBugs);
            Assert.IsType(exceptionType, engine.TestReport.ThrownException);
        }

        protected static void ThrowException<T>()
            where T : Exception, new() =>
            throw new T();

        /// <summary>
        /// Returns the configuration tests run under, already carrying a starting seed.
        /// </summary>
        /// <remarks>
        /// The seed is applied here as well as at the construction of the engine, because a handful
        /// of tests take this configuration and build an engine of their own rather than going
        /// through <see cref="RunSystematicTest(Action, Configuration)"/> and the rest. Applying it
        /// twice costs nothing: a configuration that already names a seed keeps it.
        /// </remarks>
        protected virtual Configuration GetConfiguration() => this.WithDefaultRandomSeed(
            Configuration.Create()
                .WithVerbosityEnabled(VerbosityLevel.Debug)
                .WithTelemetryEnabled(false)
                .WithAtomicOperationRaceCheckingEnabled(false)
                .WithVolatileOperationRaceCheckingEnabled(false)
                .WithLockAccessRaceCheckingEnabled(false)
                .WithPartiallyControlledConcurrencyAllowed(false));

        protected static string GetBugReport(TestingEngine engine)
        {
            string report = string.Empty;
            foreach (var bug in engine.TestReport.BugReports)
            {
                report += bug + "\n";
            }

            return report;
        }

        protected static TimeSpan GetErrorWaitingTimeout(int timeout = 5000) => Debugger.IsAttached ?
            Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(timeout);
    }
}

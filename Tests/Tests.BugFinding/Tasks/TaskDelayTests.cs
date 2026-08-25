// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    public class TaskDelayTests : BaseBugFindingTest
    {
        public TaskDelayTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestInvalidIntegerDelayIsRejected()
        {
            this.Test(() => AssertInvalidDelay(
                () => Task.Delay(-2), "millisecondsDelay"));
        }

        [Fact(Timeout = 5000)]
        public void TestInvalidIntegerDelayIsRejectedBeforeCancellation()
        {
            this.Test(() =>
            {
                using var source = new CancellationTokenSource();
                source.Cancel();
                AssertInvalidDelay(
                    () => Task.Delay(-2, source.Token), "millisecondsDelay");
            });
        }

        [Fact(Timeout = 5000)]
        public void TestInvalidTimeSpanDelayIsRejected()
        {
            this.Test(() => AssertInvalidDelay(
                () => Task.Delay(TimeSpan.FromMilliseconds(-2)), "delay"));
        }

        [Fact(Timeout = 5000)]
        public void TestInvalidTimeSpanDelayIsRejectedBeforeCancellation()
        {
            this.Test(() =>
            {
                using var source = new CancellationTokenSource();
                source.Cancel();
                AssertInvalidDelay(
                    () => Task.Delay(TimeSpan.FromMilliseconds(-2), source.Token), "delay");
            });
        }

#if NET8_0_OR_GREATER
        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestTimeProviderDelayIsControlled()
        {
            this.Test(async () =>
            {
                var provider = new ThrowingTimeProvider();
                Task delay = Task.Delay(TimeSpan.FromMilliseconds(1), provider);
                if (this.SchedulingPolicy is SchedulingPolicy.Interleaving)
                {
                    Specification.Assert(!CoyoteRuntime.Current.IsTaskUncontrolled(delay),
                        "Task.Delay(TimeSpan, TimeProvider) returned an uncontrolled task.");
                }

                await delay;
            }, configuration: this.GetConfiguration()
                .WithTestingIterations(10)
                .WithPartiallyControlledConcurrencyAllowed(false));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestCancellableTimeProviderDelayIsControlled()
        {
            this.Test(async () =>
            {
                var provider = new ThrowingTimeProvider();
                using var source = new CancellationTokenSource();
                Task delay = Task.Delay(TimeSpan.FromMinutes(1), provider, source.Token);
                if (this.SchedulingPolicy is SchedulingPolicy.Interleaving)
                {
                    Specification.Assert(!CoyoteRuntime.Current.IsTaskUncontrolled(delay),
                        "Task.Delay(TimeSpan, TimeProvider, CancellationToken) returned an uncontrolled task.");
                    Specification.Assert(!delay.IsCompleted,
                        "The controlled cancellable delay completed before cancellation.");
                }

                source.Cancel();
                OperationCanceledException failure = null;
                try
                {
                    await delay;
                }
                catch (OperationCanceledException ex)
                {
                    failure = ex;
                }

                if (this.SchedulingPolicy is SchedulingPolicy.Interleaving || failure != null)
                {
                    Specification.Assert(failure != null && failure.CancellationToken == source.Token,
                        "The controlled TimeProvider delay did not preserve its cancellation token.");
                }
            }, configuration: this.GetConfiguration()
                .WithTestingIterations(10)
                .WithPartiallyControlledConcurrencyAllowed(false));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestTimeProviderDelayPreservesValidationOrder()
        {
            this.Test(() =>
            {
                using var source = new CancellationTokenSource();
                source.Cancel();
                TimeSpan invalidDelay = TimeSpan.FromMilliseconds(-2);

                AssertArgumentException<ArgumentNullException>(
                    () => Task.Delay(invalidDelay, null), "timeProvider");
                AssertArgumentException<ArgumentNullException>(
                    () => Task.Delay(invalidDelay, null, source.Token), "timeProvider");

                var provider = new ThrowingTimeProvider();
                AssertInvalidDelay(() => Task.Delay(invalidDelay, provider), "delay");
                AssertInvalidDelay(() => Task.Delay(invalidDelay, provider, source.Token), "delay");
            });
        }
#endif

        [Fact(Timeout = 5000)]
        public void TestZeroDelayObservesPreCanceledToken()
        {
            this.Test(async () =>
            {
                using var source = new CancellationTokenSource();
                source.Cancel();

                Task delay = Task.Delay(TimeSpan.Zero, source.Token);
                Specification.Assert(delay.IsCanceled, "A zero delay ignored its pre-canceled token.");

                OperationCanceledException failure = null;
                try
                {
                    await delay;
                }
                catch (OperationCanceledException ex)
                {
                    failure = ex;
                }

                Specification.Assert(failure != null && failure.CancellationToken == source.Token,
                    "The zero delay did not preserve its cancellation token.");
            });
        }

        [Fact(Timeout = 5000)]
        public void TestPendingDelayObservesCancellation()
        {
            this.Test(async () =>
            {
                using var source = new CancellationTokenSource();
                Task delay = Task.Delay(TimeSpan.FromMinutes(1), source.Token);

                // Let the controlled delay operation start. The exact virtual deadline can win this
                // schedule before cancellation, which is a valid completion race.
                await Task.Yield();
                if (!delay.IsCompleted)
                {
                    source.Cancel();
                    OperationCanceledException failure = null;
                    try
                    {
                        await delay;
                    }
                    catch (OperationCanceledException ex)
                    {
                        failure = ex;
                    }

                    Specification.Assert(failure != null && failure.CancellationToken == source.Token,
                        "A pending delay ignored cancellation or lost its token.");
                }
            }, configuration: this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestCancellationDuringVirtualTimerAdmissionDoesNotLeaveAnOrphan()
        {
            if (this.SchedulingPolicy is not SchedulingPolicy.Interleaving)
            {
                return;
            }

            this.TestWithError(async () =>
            {
                using var source = new CancellationTokenSource();
                CoyoteRuntime runtime = CoyoteRuntime.Current;
                runtime.VirtualTimerAdmissionCallback = _ => source.Cancel();
                Task delay;
                try
                {
                    delay = Task.Delay(TimeSpan.FromMilliseconds(1), source.Token);
                }
                finally
                {
                    runtime.VirtualTimerAdmissionCallback = null;
                }

                OperationCanceledException failure = null;
                try
                {
                    await delay;
                }
                catch (OperationCanceledException ex)
                {
                    failure = ex;
                }

                Specification.Assert(failure != null && failure.CancellationToken == source.Token,
                    "Cancellation during virtual timer admission did not preserve its token.");

                await Task.Delay(TimeSpan.FromMilliseconds(10));
                Specification.Assert(runtime.GetVirtualTimeTicksForTesting() ==
                    TimeSpan.FromMilliseconds(10).Ticks,
                    "A canceled timer polluted the virtual clock after its task had completed.");

                // Make the successful path observable to TestWithError and replay it. Before the
                // remediation, the orphaned operation prevents this point from being reached.
                Specification.Assert(false, "Admission-cancellation scenario completed without an orphaned timer.");
            }, errorChecker: error =>
            {
                Assert.Contains("Admission-cancellation scenario completed without an orphaned timer.", error);
                Assert.DoesNotContain("Deadlock detected", error, StringComparison.Ordinal);
            }, configuration: this.GetConfiguration()
                .WithTestingIterations(1)
                .WithPartiallyControlledConcurrencyAllowed(false), replay: true);
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "VirtualTimeRemediation")]
        public void TestFractionalTaskDelaysUseBclMillisecondTruncation()
        {
            if (this.SchedulingPolicy is not SchedulingPolicy.Interleaving)
            {
                return;
            }

            this.Test(async () =>
            {
                TimeSpan halfMillisecond = TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond / 2);
                TimeSpan oneAndHalfMilliseconds = TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond +
                    (TimeSpan.TicksPerMillisecond / 2));
#if NET8_0_OR_GREATER
                var provider = new ThrowingTimeProvider();
#endif

                Task[] immediate =
                {
                    Task.Delay(halfMillisecond),
                    Task.Delay(halfMillisecond, CancellationToken.None),
#if NET8_0_OR_GREATER
                    Task.Delay(halfMillisecond, provider),
                    Task.Delay(halfMillisecond, provider, CancellationToken.None)
#endif
                };
                foreach (Task delay in immediate)
                {
                    Specification.Assert(delay.IsCompleted,
                        "A 0.5ms Task.Delay did not complete as the BCL 0ms timeout.");
                }

                Specification.Assert(CoyoteRuntime.Current.GetVirtualTimeTicksForTesting() is 0,
                    "A 0.5ms Task.Delay advanced virtual time instead of completing synchronously.");

                Task[] fractional =
                {
                    Task.Delay(oneAndHalfMilliseconds),
                    Task.Delay(oneAndHalfMilliseconds, CancellationToken.None),
#if NET8_0_OR_GREATER
                    Task.Delay(oneAndHalfMilliseconds, provider),
                    Task.Delay(oneAndHalfMilliseconds, provider, CancellationToken.None)
#endif
                };
                await Task.WhenAll(fractional);
                Specification.Assert(CoyoteRuntime.Current.GetVirtualTimeTicksForTesting() ==
                    TimeSpan.TicksPerMillisecond,
                    "A 1.5ms Task.Delay did not use the BCL 1ms timeout.");
            }, configuration: this.GetConfiguration().WithTestingIterations(10));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "VirtualTimeRemediation")]
        public void TestNegativeFractionalTaskDelaysAreInfinite()
        {
            if (this.SchedulingPolicy is not SchedulingPolicy.Interleaving)
            {
                return;
            }

            this.Test(async () =>
            {
                TimeSpan negativeOneAndHalfMilliseconds = TimeSpan.FromTicks(-TimeSpan.TicksPerMillisecond -
                    (TimeSpan.TicksPerMillisecond / 2));
#if NET8_0_OR_GREATER
                var provider = new ThrowingTimeProvider();
#endif
                using var source = new CancellationTokenSource();
                Task cancellable = Task.Delay(negativeOneAndHalfMilliseconds, source.Token);
#if NET8_0_OR_GREATER
                Task cancellableWithProvider = Task.Delay(negativeOneAndHalfMilliseconds, provider, source.Token);
#endif
                Task[] infinite =
                {
                    Task.Delay(negativeOneAndHalfMilliseconds),
                    cancellable,
#if NET8_0_OR_GREATER
                    Task.Delay(negativeOneAndHalfMilliseconds, provider),
                    cancellableWithProvider
#endif
                };

                await Task.Delay(TimeSpan.FromMilliseconds(1));
                foreach (Task delay in infinite)
                {
                    Specification.Assert(!delay.IsCompleted,
                        "A -1.5ms Task.Delay did not use the BCL infinite timeout.");
                }

                source.Cancel();
                await AssertCanceledWithTokenAsync(cancellable, source.Token);
#if NET8_0_OR_GREATER
                await AssertCanceledWithTokenAsync(cancellableWithProvider, source.Token);
#endif
            }, configuration: this.GetConfiguration().WithTestingIterations(100));
        }

        private static async Task AssertCanceledWithTokenAsync(Task task, CancellationToken expectedToken)
        {
            OperationCanceledException failure = null;
            try
            {
                await task;
            }
            catch (OperationCanceledException ex)
            {
                failure = ex;
            }

            Specification.Assert(failure != null && failure.CancellationToken == expectedToken,
                "A canceled delay did not preserve its cancellation token.");
        }

        [Fact(Timeout = 5000)]
        public void TestPositiveDelayObservesPreCanceledToken()
        {
            this.Test(async () =>
            {
                using var source = new CancellationTokenSource();
                source.Cancel();

                Task delay = Task.Delay(TimeSpan.FromMinutes(1), source.Token);
                OperationCanceledException failure = null;
                try
                {
                    await delay;
                }
                catch (OperationCanceledException ex)
                {
                    failure = ex;
                }

                Specification.Assert(failure != null && failure.CancellationToken == source.Token,
                    "A positive delay ignored its pre-canceled token.");
            });
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestInfiniteDelayCancellationIsControlled()
        {
            this.Test(async () =>
            {
                using var source = new CancellationTokenSource();
                Task delay = Task.Delay(Timeout.InfiniteTimeSpan, source.Token);
                Specification.Assert(!CoyoteRuntime.Current.IsTaskUncontrolled(delay),
                    "The infinite delay returned an unregistered task.");
                source.Cancel();

                OperationCanceledException failure = null;
                try
                {
                    await delay;
                }
                catch (OperationCanceledException ex)
                {
                    failure = ex;
                }

                Specification.Assert(failure != null && failure.CancellationToken == source.Token,
                    "The controlled infinite delay did not preserve its cancellation token.");
            }, configuration: this.GetConfiguration()
                .WithTestingIterations(1)
                .WithPartiallyControlledConcurrencyAllowed(false));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestUncancellableInfiniteDelayIsDefiniteControlledDeadlock()
        {
            if (this.SchedulingPolicy is not SchedulingPolicy.Interleaving)
            {
                return;
            }

            this.TestWithError(async () =>
            {
                Task delay = Task.Delay(Timeout.InfiniteTimeSpan);
                Specification.Assert(!CoyoteRuntime.Current.IsTaskUncontrolled(delay),
                    "The infinite delay returned an unregistered task.");
                await delay;
            }, errorChecker: error =>
            {
                Assert.StartsWith("Deadlock detected.", error);
                Assert.DoesNotContain("Potential deadlock", error, StringComparison.Ordinal);
                Assert.DoesNotContain("uncontrolled", error, StringComparison.OrdinalIgnoreCase);
            }, configuration: this.GetConfiguration()
                .WithTestingIterations(1)
                .WithPartiallyControlledConcurrencyAllowed(false), replay: true);
        }

        private static void AssertInvalidDelay(Action action, string expectedParameterName) =>
            AssertArgumentException<ArgumentOutOfRangeException>(action, expectedParameterName);

        private static void AssertArgumentException<TException>(Action action, string expectedParameterName)
            where TException : ArgumentException
        {
            Exception failure = null;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            Specification.Assert(
                failure is TException argument &&
                argument.ParamName == expectedParameterName,
                "Expected {0} for parameter '{1}', but received {2} for parameter '{3}'.",
                typeof(TException).Name,
                expectedParameterName,
                failure?.GetType().Name ?? "no exception",
                (failure as ArgumentException)?.ParamName ?? "none");
        }

        private static async Task WriteWithLoopAndDelayAsync(SharedEntry entry, int value, int delay)
        {
            for (int i = 0; i < 2; i++)
            {
                entry.Value = value + i;
                await Task.Delay(delay);
            }
        }

        [Fact(Timeout = 5000)]
        public void TestInterleavingsInLoopWithSynchronousDelays()
        {
            this.Test(async () =>
            {
                SharedEntry entry = new SharedEntry();

                Task[] tasks = new Task[2];
                for (int i = 0; i < 2; i++)
                {
                    tasks[i] = WriteWithLoopAndDelayAsync(entry, i, 0);
                }

                await Task.WhenAll(tasks);

                AssertSharedEntryValue(entry, 2);
            },
            configuration: this.GetConfiguration().WithTestingIterations(200));
        }

        [Fact(Timeout = 5000)]
        public void TestInterleavingsInLoopWithAsynchronousDelays()
        {
            this.TestWithError(async () =>
            {
                SharedEntry entry = new SharedEntry();

                Task[] tasks = new Task[2];
                for (int i = 0; i < 2; i++)
                {
                    tasks[i] = WriteWithLoopAndDelayAsync(entry, i, 1);
                }

                await Task.WhenAll(tasks);

                Specification.Assert(entry.Value is 2, "Value is {0} instead of 2.", entry.Value);
            },
            configuration: this.GetConfiguration().WithTestingIterations(200),
            expectedError: "Value is 1 instead of 2.",
            replay: true);
        }

        private static async Task WriteWithDelayAsync(SharedEntry entry, int value, int delay, bool repeat = false)
        {
            await Task.Delay(delay);
            Task task = null;
            if (repeat)
            {
                task = InvokeWriteWithDelayAsync(entry, value, delay);
            }

            entry.Value = value;
            if (task != null)
            {
                await task;
            }
        }

        private static async Task InvokeWriteWithDelayAsync(SharedEntry entry, int value, int delay, bool repeat = false)
        {
            await WriteWithDelayAsync(entry, value, delay, repeat);
        }

        [Fact(Timeout = 5000)]
        public void TestInterleavingsInNestedSynchronousDelay()
        {
            this.Test(async () =>
            {
                SharedEntry entry = new SharedEntry();
                Task task = InvokeWriteWithDelayAsync(entry, 3, 0);
                entry.Value = 5;
                await task;
                AssertSharedEntryValue(entry, 5);
            },
            configuration: this.GetConfiguration().WithTestingIterations(200));
        }

        [Fact(Timeout = 5000)]
        public void TestInterleavingsInNestedAsynchronousDelay()
        {
            this.TestWithError(async () =>
            {
                SharedEntry entry = new SharedEntry();
                Task task = InvokeWriteWithDelayAsync(entry, 3, 1);
                entry.Value = 5;
                await task;
                AssertSharedEntryValue(entry, 5);
            },
            configuration: this.GetConfiguration().WithTestingIterations(200),
            expectedError: "Value is 3 instead of 5.",
            replay: true);
        }

        [Fact(Timeout = 5000)]
        public void TestInterleavingsInNestedSynchronousDelays()
        {
            this.Test(async () =>
            {
                SharedEntry entry = new SharedEntry();
                Task task1 = InvokeWriteWithDelayAsync(entry, 3, 0);
                Task task2 = InvokeWriteWithDelayAsync(entry, 5, 0);
                await Task.WhenAll(task1, task2);
                AssertSharedEntryValue(entry, 5);
            },
            configuration: this.GetConfiguration().WithTestingIterations(200));
        }

        [Fact(Timeout = 5000)]
        public void TestInterleavingsInNestedAsynchronousDelays()
        {
            this.TestWithError(async () =>
            {
                SharedEntry entry = new SharedEntry();
                Task task1 = InvokeWriteWithDelayAsync(entry, 3, 1);
                Task task2 = InvokeWriteWithDelayAsync(entry, 5, 1);
                await Task.WhenAll(task1, task2);
                AssertSharedEntryValue(entry, 5);
            },
            configuration: this.GetConfiguration().WithTestingIterations(200),
            expectedError: "Value is 3 instead of 5.",
            replay: true);
        }

        [Fact(Timeout = 5000)]
        public void TestInterleavingsInRepeatedNestedSynchronousDelays()
        {
            this.Test(async () =>
            {
                SharedEntry entry = new SharedEntry();
                Task task1 = InvokeWriteWithDelayAsync(entry, 3, 0, true);
                Task task2 = InvokeWriteWithDelayAsync(entry, 5, 0, true);
                await Task.WhenAll(task1, task2);
                Specification.Assert(entry.Value != 3, "Value is 3.");
            },
            configuration: this.GetConfiguration().WithTestingIterations(200));
        }

        [Fact(Timeout = 5000)]
        public void TestInterleavingsInRepeatedNestedAsynchronousDelays()
        {
            this.TestWithError(async () =>
            {
                SharedEntry entry = new SharedEntry();
                Task task1 = InvokeWriteWithDelayAsync(entry, 3, 1, true);
                Task task2 = InvokeWriteWithDelayAsync(entry, 5, 1, true);
                await Task.WhenAll(task1, task2);
                AssertSharedEntryValue(entry, 5);
            },
            configuration: this.GetConfiguration().WithTestingIterations(200),
            expectedError: "Value is 3 instead of 5.",
            replay: true);
        }

        [Fact(Timeout = 5000)]
        public void TestInterleavingsInLambdaSynchronousDelays()
        {
            this.Test(async () =>
            {
                SharedEntry entry = new SharedEntry();
#pragma warning disable IDE0039 // Use local function
                Func<int, int, Task> invokeWriteWithDelayAsync = async (value, delay) =>
#pragma warning restore IDE0039 // Use local function
                {
                    await WriteWithDelayAsync(entry, value, delay);
                };

                Task task1 = invokeWriteWithDelayAsync(3, 0);
                Task task2 = invokeWriteWithDelayAsync(5, 0);
                await Task.WhenAll(task1, task2);
                AssertSharedEntryValue(entry, 5);
            },
            configuration: this.GetConfiguration().WithTestingIterations(200));
        }

        [Fact(Timeout = 5000)]
        public void TestInterleavingsInLambdaAsynchronousDelays()
        {
            this.TestWithError(async () =>
            {
                SharedEntry entry = new SharedEntry();
#pragma warning disable IDE0039 // Use local function
                Func<int, int, Task> invokeWriteWithDelayAsync = async (value, delay) =>
#pragma warning restore IDE0039 // Use local function
                {
                    await WriteWithDelayAsync(entry, value, delay);
                };

                Task task1 = invokeWriteWithDelayAsync(3, 1);
                Task task2 = invokeWriteWithDelayAsync(5, 1);
                await Task.WhenAll(task1, task2);
                AssertSharedEntryValue(entry, 5);
            },
            configuration: this.GetConfiguration().WithTestingIterations(200),
            expectedError: "Value is 3 instead of 5.",
            replay: true);
        }

        [Fact(Timeout = 5000)]
        public void TestInterleavingsInLocalFunctionSynchronousDelays()
        {
            this.Test(async () =>
            {
                SharedEntry entry = new SharedEntry();
                async Task InvokeWriteWithDelayAsync(int value, int delay)
                {
                    await WriteWithDelayAsync(entry, value, delay);
                }

                Task task1 = InvokeWriteWithDelayAsync(3, 0);
                Task task2 = InvokeWriteWithDelayAsync(5, 0);
                await Task.WhenAll(task1, task2);
                AssertSharedEntryValue(entry, 5);
            },
            configuration: this.GetConfiguration().WithTestingIterations(200));
        }

        [Fact(Timeout = 5000)]
        public void TestInterleavingsInLocalFunctionAsynchronousDelays()
        {
            this.TestWithError(async () =>
            {
                SharedEntry entry = new SharedEntry();
                async Task InvokeWriteWithDelayAsync(int value, int delay)
                {
                    await WriteWithDelayAsync(entry, value, delay);
                }

                Task task1 = InvokeWriteWithDelayAsync(3, 1);
                Task task2 = InvokeWriteWithDelayAsync(5, 1);
                await Task.WhenAll(task1, task2);
                AssertSharedEntryValue(entry, 5);
            },
            configuration: this.GetConfiguration().WithTestingIterations(200),
            expectedError: "Value is 3 instead of 5.",
            replay: true);
        }

        private static Task InvokeParallelWriteWithDelayAsync(SharedEntry entry, int delay)
        {
            return Task.Run(async () =>
            {
                Task task1 = WriteWithDelayAsync(entry, 3, delay);
                Task task2 = WriteWithDelayAsync(entry, 5, delay);
                await Task.WhenAll(task1, task2);
            });
        }

        [Fact(Timeout = 5000)]
        public void TestParallelInterleavingsInNestedSynchronousDelays()
        {
            this.Test(async () =>
            {
                SharedEntry entry = new SharedEntry();
                await InvokeParallelWriteWithDelayAsync(entry, 0);
                AssertSharedEntryValue(entry, 5);
            },
            configuration: this.GetConfiguration().WithTestingIterations(200));
        }

        [Fact(Timeout = 5000)]
        public void TestParallelInterleavingsInNestedAsynchronousDelays()
        {
            this.TestWithError(async () =>
            {
                SharedEntry entry = new SharedEntry();
                await InvokeParallelWriteWithDelayAsync(entry, 1);
                AssertSharedEntryValue(entry, 5);
            },
            configuration: this.GetConfiguration().WithTestingIterations(200),
            expectedError: "Value is 3 instead of 5.",
            replay: true);
        }

#if NET8_0_OR_GREATER
        private sealed class ThrowingTimeProvider : TimeProvider
        {
            public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period) =>
                throw new InvalidOperationException("The controlled Task.Delay model invoked TimeProvider.CreateTimer.");
        }
#endif
    }
}

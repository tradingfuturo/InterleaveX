// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading;
using System.Threading.Tasks;
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

                // Let the controlled delay operation start. If this schedule selected a synchronous
                // timeout there is no pending delay to cancel, which is a valid completion race.
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
            }, configuration: this.GetConfiguration().WithTestingIterations(100).WithTimeoutDelay(10));
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

        private static void AssertInvalidDelay(Action action, string expectedParameterName)
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
                failure is ArgumentOutOfRangeException argument &&
                argument.ParamName == expectedParameterName,
                "Invalid delay produced '{0}' for parameter '{1}' instead of ArgumentOutOfRangeException for '{2}'.",
                failure?.GetType().Name ?? "no exception",
                (failure as ArgumentException)?.ParamName ?? "none",
                expectedParameterName);
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
    }
}

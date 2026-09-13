// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET8_0_OR_GREATER
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// Covers the <see cref="Timer"/> model. The strict configuration refuses partial control, so a callback that ran
    /// on the framework's timer thread would be reported as uncontrolled by every test here.
    /// </summary>
    public class ThreadingTimerTests : BaseBugFindingTest
    {
        public ThreadingTimerTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestOneShotTimerFiresOnceAtItsVirtualDueTime()
        {
            this.Test(async () =>
            {
                CoyoteRuntime runtime = CoyoteRuntime.Current;
                long start = runtime.GetVirtualTimeTicksForTesting();
                var fired = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                int count = 0;
                using var timer = new Timer(_ =>
                {
                    count++;
                    fired.TrySetResult(runtime.GetVirtualTimeTicksForTesting());
                }, null, 250, Timeout.Infinite);

                long elapsed = await fired.Task - start;
                Specification.Assert(elapsed == 250 * TimeSpan.TicksPerMillisecond,
                    "The timer fired {0} ticks after it was created instead of at its due time.", elapsed);
                await Task.Delay(1000);
                Specification.Assert(count is 1, "A one-shot timer fired {0} times.", count);
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestZeroDueTimeCallbackDoesNotRunInsideTheConstructor()
        {
            this.Test(async () =>
            {
                int callerThread = Environment.CurrentManagedThreadId;
                bool isInsideConstructor = true;
                bool ranInline = false;
                var fired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var timer = new Timer(_ =>
                {
                    ranInline = isInsideConstructor && Environment.CurrentManagedThreadId == callerThread;
                    fired.TrySetResult(true);
                }, null, 0, Timeout.Infinite);
                isInsideConstructor = false;

                await fired.Task;
                Specification.Assert(!ranInline, "A zero due-time callback ran inline inside the Timer constructor.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestPeriodicTimerFiresOnEachVirtualPeriodUntilDisposed()
        {
            this.Test(async () =>
            {
                CoyoteRuntime runtime = CoyoteRuntime.Current;
                long start = runtime.GetVirtualTimeTicksForTesting();
                var firedAt = new long[3];
                int count = 0;
                var third = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var timer = new Timer(_ =>
                {
                    if (count < firedAt.Length)
                    {
                        firedAt[count] = runtime.GetVirtualTimeTicksForTesting() - start;
                    }

                    count++;
                    if (count == firedAt.Length)
                    {
                        third.TrySetResult(true);
                    }
                }, null, 100, 50);

                await third.Task;
                await timer.DisposeAsync();
                int countAtDisposal = count;
                await Task.Delay(1000);

                long period = TimeSpan.TicksPerMillisecond;
                Specification.Assert(firedAt[0] == 100 * period && firedAt[1] == 150 * period && firedAt[2] == 200 * period,
                    "The periodic timer fired at {0}, {1} and {2} ticks.", firedAt[0], firedAt[1], firedAt[2]);
                Specification.Assert(count == countAtDisposal, "A disposed periodic timer kept firing.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestChangeToInfiniteStopsTheTimerAndChangeRearmsIt()
        {
            this.Test(async () =>
            {
                int count = 0;
                var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var later = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var timer = new Timer(_ =>
                {
                    count++;
                    if (!first.TrySetResult(true))
                    {
                        later.TrySetResult(true);
                    }
                }, null, 10, 10);

                await first.Task;
                Specification.Assert(timer.Change(Timeout.Infinite, Timeout.Infinite),
                    "Changing a live timer to infinite returned false.");
                int countAtChange = count;
                await Task.Delay(1000);
                Specification.Assert(count == countAtChange, "A timer changed to infinite kept firing.");

                Specification.Assert(timer.Change(5, Timeout.Infinite), "Re-arming a stopped timer returned false.");
                await later.Task;
                await Task.Delay(1000);
                Specification.Assert(count == countAtChange + 1,
                    "A timer re-armed to fire once fired {0} times.", count - countAtChange);
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestDisposeAsyncWaitsForARunningCallback()
        {
            this.Test(async () =>
            {
                var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                bool finished = false;
                var timer = new Timer(_ =>
                {
                    entered.TrySetResult(true);
                    release.Task.Wait();
                    finished = true;
                }, null, 0, Timeout.Infinite);

                await entered.Task;
                ValueTask disposal = timer.DisposeAsync();
                Specification.Assert(!disposal.IsCompleted, "DisposeAsync completed while a callback was still running.");
                release.SetResult(true);
                await disposal;
                Specification.Assert(finished, "DisposeAsync completed before the running callback returned.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestDisposeWhileACallbackRunsStopsLaterCallbacksWithoutWaiting()
        {
            this.Test(async () =>
            {
                int count = 0;
                var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var timer = new Timer(_ =>
                {
                    count++;
                    entered.TrySetResult(true);
                    release.Task.Wait();
                }, null, 0, 10);

                await entered.Task;
                timer.Dispose();
                Specification.Assert(!release.Task.IsCompleted, "Dispose waited for the running callback.");
                Specification.Assert(!timer.Change(0, 10), "Change on a disposed timer returned true.");
                release.SetResult(true);
                await Task.Delay(1000);
                Specification.Assert(count is 1, "A periodic timer disposed during its first callback fired {0} times.", count);
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestDisposeWithAWaitHandleSignalsOnceNoCallbackIsRunning()
        {
            this.Test(async () =>
            {
                var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                bool finished = false;
                var timer = new Timer(_ =>
                {
                    entered.TrySetResult(true);
                    release.Task.Wait();
                    finished = true;
                }, null, 0, Timeout.Infinite);

                await entered.Task;
                using var disposed = new ManualResetEvent(false);
                Specification.Assert(timer.Dispose(disposed), "The first Dispose(WaitHandle) returned false.");
                Specification.Assert(!disposed.WaitOne(0), "Dispose(WaitHandle) signaled while a callback was running.");

                _ = Task.Run(() => release.SetResult(true));
                Specification.Assert(disposed.WaitOne(), "Dispose(WaitHandle) never signaled.");
                Specification.Assert(finished, "Dispose(WaitHandle) signaled before the running callback returned.");

                using var second = new ManualResetEvent(false);
                Specification.Assert(!timer.Dispose(second), "A second Dispose(WaitHandle) returned true.");
                ValueTask late = timer.DisposeAsync();
                Specification.Assert(late.IsFaulted && late.AsTask().Exception?.InnerException is InvalidOperationException,
                    "DisposeAsync after Dispose(WaitHandle) did not fault with InvalidOperationException.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestConstructorsAndChangeValidateTheirArguments()
        {
            this.Test(() =>
            {
                AssertThrows<ArgumentNullException>(() => new Timer(null, null, 0, 0), "callback");
                AssertThrows<ArgumentOutOfRangeException>(() => new Timer(_ => { }, null, -2, 0), "dueTime");
                AssertThrows<ArgumentOutOfRangeException>(() => new Timer(_ => { }, null, 0, -2), "period");
                AssertThrows<ArgumentOutOfRangeException>(() => new Timer(_ => { }, null,
                    TimeSpan.FromMilliseconds(4294967295d), TimeSpan.Zero), "dueTime");
                AssertThrows<ArgumentOutOfRangeException>(() => new Timer(_ => { }, null, 0L, 4294967295L), "period");

                using var timer = new Timer(_ => { });
                AssertThrows<ArgumentOutOfRangeException>(() => timer.Change(-2, 0), "dueTime");
                AssertThrows<ArgumentOutOfRangeException>(() => timer.Change(0L, -2L), "period");
                AssertThrows<ArgumentOutOfRangeException>(() => timer.Change(TimeSpan.Zero,
                    TimeSpan.FromMilliseconds(-2)), "period");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestTimerWithoutStatePassesItselfToItsCallback()
        {
            this.Test(async () =>
            {
                Timer timer = null;
                object observed = null;
                var fired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                timer = new Timer(state =>
                {
                    observed = state;
                    fired.TrySetResult(true);
                });

                Specification.Assert(timer.Change(0, Timeout.Infinite), "Arming an unarmed timer returned false.");
                await fired.Task;
                Specification.Assert(ReferenceEquals(observed, timer), "The callback did not receive its own timer.");
                await timer.DisposeAsync();
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestEveryDueTimeRepresentationFiresInVirtualTimeOrder()
        {
            this.Test(async () =>
            {
                var order = new int[3];
                int next = 0;
                var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                TimerCallback record = state =>
                {
                    order[next++] = (int)state;
                    if (next == order.Length)
                    {
                        done.TrySetResult(true);
                    }
                };

                using var late = new Timer(record, 3, TimeSpan.FromMilliseconds(30), Timeout.InfiniteTimeSpan);
                using var middle = new Timer(record, 2, 20L, -1L);
                using var early = new Timer(record, 1, 10u, uint.MaxValue);
                await done.Task;
                Specification.Assert(order[0] is 1 && order[1] is 2 && order[2] is 3,
                    "Timers fired in the order {0}, {1}, {2}.", order[0], order[1], order[2]);
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestChangeThroughITimerReachesTheModelledSchedule()
        {
            this.Test(async () =>
            {
                var fired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var timer = new Timer(_ => fired.TrySetResult(true));
                ITimer contract = timer;
                Specification.Assert(contract.Change(TimeSpan.FromMilliseconds(5), Timeout.InfiniteTimeSpan),
                    "Arming through ITimer returned false.");
                await fired.Task;
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestUsingAndAwaitUsingDisposeTheModelledTimer()
        {
            this.Test(async () =>
            {
                int count = 0;
                await using (var timer = new Timer(_ => count++, null, 1, 1))
                {
                    await Task.Delay(1);
                }

                using (var timer = new Timer(_ => count++, null, 1, 1))
                {
                    await Task.Delay(1);
                }

                int countAtDisposal = count;
                await Task.Delay(100);
                Specification.Assert(count == countAtDisposal, "A timer disposed by a using statement kept firing.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestUndisposedPeriodicTimerDoesNotKeepTheIterationAlive()
        {
            this.Test(() =>
            {
                var timer = new Timer(_ => { }, null, 0, 1);
                GC.KeepAlive(timer);
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestCallbackExceptionIsReportedAsUnhandled()
        {
            this.TestWithError(async () =>
            {
                using var timer = new Timer(_ => throw new InvalidOperationException("timer callback failure"),
                    null, 0, Timeout.Infinite);
                await Task.Delay(10);
            },
            errorChecker: (e) => Assert.Contains("timer callback failure", e),
            configuration: this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestTimerCallbackRaceWithTheCreatingOperationIsExplored()
        {
            // A witness that a callback due at the same virtual instant as the creating operation's own delay is
            // interleaved with it, in both orders.
            this.TestWithError(async () =>
            {
                bool fired = false;
                using var timer = new Timer(_ => fired = true, null, 1, Timeout.Infinite);
                await Task.Delay(1);
                Specification.Assert(fired, "A 1ms timer had not fired by the end of a 1ms delay.");
            },
            errorChecker: (e) => Assert.StartsWith("A 1ms timer had not fired by the end of a 1ms delay.", e),
            configuration: this.GetStrictConfiguration());
        }

        private static void AssertThrows<TException>(Func<object> action, string parameterName)
            where TException : ArgumentException
        {
            try
            {
                GC.KeepAlive(action());
            }
            catch (TException ex) when (ex.ParamName == parameterName)
            {
                return;
            }

            Specification.Assert(false, "Expected {0} for parameter '{1}'.", typeof(TException).Name, parameterName);
        }

        private Configuration GetStrictConfiguration() => this.GetConfiguration()
            .WithTestingIterations(100)
            .WithPartiallyControlledConcurrencyAllowed(false)
            .WithPartiallyControlledDataNondeterminismAllowed(false)
            .WithSystematicFuzzingFallbackEnabled(false);
    }
}
#endif

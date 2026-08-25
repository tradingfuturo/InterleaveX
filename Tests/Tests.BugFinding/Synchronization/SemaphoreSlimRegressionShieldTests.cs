// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;
using ControlledSemaphoreSlim = Microsoft.Coyote.Rewriting.Types.Threading.SemaphoreSlim;

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// Fail-first shields for SemaphoreSlim behavior that is observable to a rewritten program.
    /// The queue inspection is deliberately limited to establishing that the model has parked the
    /// operation before cancellation; it avoids timing-based tests and does not repair model state.
    /// </summary>
    public class SemaphoreSlimRegressionShieldTests : BaseBugFindingTest
    {
        public SemaphoreSlimRegressionShieldTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestSynchronousCancellationWakesAnAlreadyParkedWaiter()
        {
            this.Test(() =>
            {
                using var semaphore = new SemaphoreSlim(0, 1);
                using var cancellation = new CancellationTokenSource();
                bool cancelled = false;
                Task waiter = Task.Run(() =>
                {
                    try
                    {
                        semaphore.Wait(cancellation.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                    }
                });

                WaitUntilQueued(semaphore, "PausedOperations");
                cancellation.Cancel();
                waiter.Wait();
                Specification.Assert(cancelled, "Cancelling an already-parked synchronous wait did not wake it.");
            });
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestFiniteAsyncCancellationWakesAnAlreadyParkedWaiter()
        {
            this.Test(async () =>
            {
                using var semaphore = new SemaphoreSlim(0, 1);
                using var cancellation = new CancellationTokenSource();
                long clockBeforeWait = CoyoteRuntime.Current.GetVirtualTimeTicksForTesting();
                ControlledSemaphoreSlim.SetWaiterQueuedCallbackForTesting(
                    semaphore, cancellation.Cancel);
                Task<bool> wait = semaphore.WaitAsync(TimeSpan.FromSeconds(1), cancellation.Token);

                bool cancelled = false;
                try
                {
                    await wait;
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }

                Specification.Assert(cancelled, "Cancelling an already-parked finite async wait did not wake it.");
                Specification.Assert(CoyoteRuntime.Current.GetVirtualTimeTicksForTesting() == clockBeforeWait,
                    "A finite async semaphore wait observed cancellation only after advancing to its timeout.");
            });
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestFiniteSynchronousCancellationWakesAnAlreadyParkedWaiter()
        {
            this.Test(() =>
            {
                using var semaphore = new SemaphoreSlim(0, 1);
                using var cancellation = new CancellationTokenSource();
                long clockBeforeWait = CoyoteRuntime.Current.GetVirtualTimeTicksForTesting();
                ControlledSemaphoreSlim.SetWaiterQueuedCallbackForTesting(
                    semaphore, cancellation.Cancel);
                bool cancelled = false;
                Task waiter = Task.Run(() =>
                {
                    try
                    {
                        semaphore.Wait(TimeSpan.FromSeconds(1), cancellation.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                    }
                });

                waiter.Wait();
                Specification.Assert(cancelled,
                    "Cancelling an already-parked finite synchronous wait did not wake it.");
                Specification.Assert(CoyoteRuntime.Current.GetVirtualTimeTicksForTesting() == clockBeforeWait,
                    "A finite synchronous semaphore wait observed cancellation only after advancing to its timeout.");
            });
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestInfiniteAsyncCancellationWakesAnAlreadyParkedWaiter()
        {
            this.Test(async () =>
            {
                using var semaphore = new SemaphoreSlim(0, 1);
                using var cancellation = new CancellationTokenSource();
                Task wait = semaphore.WaitAsync(cancellation.Token);

                WaitUntilQueued(semaphore, "AsyncAwaiters");
                cancellation.Cancel();
                bool cancelled = false;
                try
                {
                    await wait;
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }

                Specification.Assert(cancelled, "Cancelling an already-parked infinite async wait did not wake it.");
            });
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestCancellationWinsAReleaseRaceWhenItIsObservedFirst()
        {
            this.Test(async () =>
            {
                using var semaphore = new SemaphoreSlim(0, 1);
                using var cancellation = new CancellationTokenSource();
                Task wait = semaphore.WaitAsync(cancellation.Token);

                WaitUntilQueued(semaphore, "AsyncAwaiters");
                cancellation.Cancel();
                semaphore.Release();
                bool cancelled = false;
                try
                {
                    await wait;
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }

                Specification.Assert(cancelled,
                    "A cancellation observed before Release was converted into a successful acquisition.");
                Specification.Assert(semaphore.CurrentCount is 1,
                    "A cancelled waiter consumed the release that followed its cancellation.");
            });
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestReleaseWinsACancellationRaceWhenItIsObservedFirst()
        {
            this.Test(async () =>
            {
                using var semaphore = new SemaphoreSlim(0, 1);
                using var cancellation = new CancellationTokenSource();
                Task wait = semaphore.WaitAsync(cancellation.Token);

                WaitUntilQueued(semaphore, "AsyncAwaiters");
                semaphore.Release();
                cancellation.Cancel();
                await wait;
                Specification.Assert(semaphore.CurrentCount is 0,
                    "The successful waiter did not retain the release that won its cancellation race.");
            });
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestSynchronousCancellationWinsAReleaseRaceWhenItIsObservedFirst()
        {
            this.Test(async () =>
            {
                using var semaphore = new SemaphoreSlim(0, 1);
                using var cancellation = new CancellationTokenSource();
                var queued = new TaskCompletionSource<bool>();
                ControlledSemaphoreSlim.SetWaiterQueuedCallbackForTesting(
                    semaphore, () => queued.TrySetResult(true));
                Task wait = Task.Run(() => semaphore.Wait(cancellation.Token));

                await queued.Task;
                cancellation.Cancel();
                semaphore.Release();
                bool cancelled = false;
                try
                {
                    await wait;
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }

                Specification.Assert(cancelled,
                    "Synchronous cancellation observed before Release did not win the wait race.");
                Specification.Assert(semaphore.CurrentCount is 1,
                    "A synchronously cancelled waiter consumed the following release.");
            });
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestSynchronousReleaseWinsACancellationRaceWhenItIsObservedFirst()
        {
            this.Test(async () =>
            {
                using var semaphore = new SemaphoreSlim(0, 1);
                using var cancellation = new CancellationTokenSource();
                var queued = new TaskCompletionSource<bool>();
                ControlledSemaphoreSlim.SetWaiterQueuedCallbackForTesting(
                    semaphore, () => queued.TrySetResult(true));
                Task wait = Task.Run(() => semaphore.Wait(cancellation.Token));

                await queued.Task;
                semaphore.Release();
                cancellation.Cancel();
                await wait;
                Specification.Assert(semaphore.CurrentCount is 0,
                    "The synchronous waiter did not retain the release that won its cancellation race.");
            });
        }

        [Theory(Timeout = 5000)]
        [InlineData(0)]
        [InlineData(-1)]
        [Trait("Category", "ReviewRemediation")]
        public void TestReleaseRequiresAPositiveCount(int releaseCount)
        {
            this.Test(() =>
            {
                using var semaphore = new SemaphoreSlim(0, 1);
                Assert.Throws<ArgumentOutOfRangeException>(() => semaphore.Release(releaseCount));
                Specification.Assert(semaphore.CurrentCount is 0,
                    "Invalid Release({0}) changed the count to {1}.", releaseCount, semaphore.CurrentCount);
            });
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestDisposedSemaphoreRejectsModeledOperations()
        {
            this.Test(() =>
            {
                var semaphore = new SemaphoreSlim(0, 1);
                semaphore.Dispose();

                Assert.Throws<ObjectDisposedException>(() => semaphore.Wait(0));
                Assert.Throws<ObjectDisposedException>(() => semaphore.Release());
            });
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestUncontrolledSynchronousWaiterRegistersAndAcquiresAfterRelease()
        {
            this.Test(() =>
            {
                using var semaphore = new SemaphoreSlim(0, 1);
                using var queued = new ManualResetEventSlim(false);
                ControlledSemaphoreSlim.SetWaiterQueuedCallbackForTesting(semaphore, queued.Set);
                bool acquired = false;
                bool released = false;
                var thread = UncontrolledThreadRunner.Start(() =>
                {
                    semaphore.Wait();
                    acquired = true;
                });

                try
                {
                    WaitUntil(() => queued.IsSet || thread.IsCompleted);
                    Specification.Assert(queued.IsSet,
                        "An uncontrolled synchronous SemaphoreSlim waiter did not register with the modeled semaphore.");

                    semaphore.Release();
                    released = true;
                    thread.Join();
                }
                finally
                {
                    if (!released)
                    {
                        semaphore.Release();
                    }

                    thread.Join();
                }

                thread.ThrowIfFailed();
                Specification.Assert(acquired,
                    "The externally registered SemaphoreSlim waiter did not acquire the released permit.");
            }, this.GetConfiguration().WithPartiallyControlledConcurrencyAllowed().WithTestingIterations(1));
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestReleaseGrantsAQueuedSynchronousWaiterBeforeAnAsyncWaiter()
        {
            this.Test(async () =>
            {
                using var semaphore = new SemaphoreSlim(0, 2);
                var synchronousQueued = new TaskCompletionSource<bool>();
                ControlledSemaphoreSlim.SetWaiterQueuedCallbackForTesting(
                    semaphore, () => synchronousQueued.TrySetResult(true));
                Task synchronousWaiter = Task.Run(() => semaphore.Wait());
                await synchronousQueued.Task;

                Task asynchronousWaiter = semaphore.WaitAsync();
                Assert.NotEqual(0, GetQueueCount(semaphore, "AsyncAwaiters"));

                semaphore.Release();
                bool asyncWonTheFirstPermit = asynchronousWaiter.IsCompleted;
                semaphore.Release();

                await Task.WhenAll(synchronousWaiter, asynchronousWaiter);
                Specification.Assert(!asyncWonTheFirstPermit,
                    "SemaphoreSlim.Release granted an async waiter before an already-queued synchronous waiter.");
            }, this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestStaleCurrentCountGetterIsDetected()
        {
            SharedStaleSemaphore = null;
            try
            {
                this.TestWithError(() =>
                {
                    if (SharedStaleSemaphore is null)
                    {
                        SharedStaleSemaphore = new SemaphoreSlim(1, 1);
                        return;
                    }

                    _ = SharedStaleSemaphore.CurrentCount;
                }, configuration: this.GetConfiguration().WithTestingIterations(2), errorChecker: error =>
                {
                    Assert.Contains("was created in a previous test iteration", error, StringComparison.Ordinal);
                });
            }
            finally
            {
                SharedStaleSemaphore = null;
            }
        }

        private static void WaitUntilQueued(SemaphoreSlim semaphore, string fieldName)
        {
            for (int step = 0; step < 200 && GetQueueCount(semaphore, fieldName) is 0; step++)
            {
                SchedulingPoint.Interleave();
            }

            Assert.NotEqual(0, GetQueueCount(semaphore, fieldName));
        }

        private static int GetQueueCount(SemaphoreSlim semaphore, string fieldName)
        {
            FieldInfo field = semaphore.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return ((ICollection)field.GetValue(semaphore)).Count;
        }

        private static void WaitUntil(Func<bool> condition)
        {
            for (int step = 0; step < 200; step++)
            {
                if (condition())
                {
                    return;
                }

                SchedulingPoint.Interleave();
            }

            Assert.True(condition(), "The bounded synchronization observation did not occur.");
        }
    }
}

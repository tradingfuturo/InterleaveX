// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Threading;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Runtime.Tests
{
    /// <summary>
    /// Tests the thread pool's park, drain and retire protocol directly, without going through a
    /// testing engine.
    /// </summary>
    /// <remarks>
    /// These drive <see cref="ControlledThreadPool"/> itself on purpose. Every engine run already ends
    /// with a <see cref="ControlledThreadPool.Drain"/> in its own finally block, so an engine-mediated
    /// test that merely asserts the pool is empty afterwards passes whether or not the protocol below
    /// is correct, and cannot distinguish a worker that terminated from one that re-parked and was
    /// swept by that drain.
    /// </remarks>
    public class ControlledThreadPoolProtocolTests : BaseRuntimeTest
    {
        public ControlledThreadPoolProtocolTests(ITestOutputHelper output)
            : base(output)
        {
        }

        /// <summary>
        /// How long a test waits for a worker to reach an expected state before failing.
        /// </summary>
        private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

        [Fact(Timeout = 30000)]
        public void TestDrainRetiresWorkerThatCompletesAfterTheDrain()
        {
            // A worker that is executing an operation when a drain happens must not return to the pool
            // afterwards: the drain is the point at which the engine declares the pool empty, and a
            // worker that re-parks after it survives into an unrelated test.
            var pool = ControlledThreadPool.Instance;
            pool.Drain();

            using var gate = new ManualResetEventSlim(false);
            PooledThread worker = pool.Rent();
            try
            {
                worker.Dispatch(() =>
                {
                    gate.Wait();
                    return WorkerDisposition.Reuse;
                });

                // Drain while the worker is still executing, which is what makes it a barrier rather
                // than a sweep of whatever happens to be parked at that instant.
                pool.Drain();
                gate.Set();

                Assert.True(worker.OSThread.Join(WaitTimeout),
                    "The worker was still running after the drain released it, so it re-parked instead " +
                    "of retiring.");
                Assert.True(pool.IdleThreadCount is 0,
                    $"The pool retains {pool.IdleThreadCount} threads after draining, so a worker " +
                    "parked itself after the drain completed.");
            }
            finally
            {
                gate.Set();
                pool.Drain();
            }
        }

        [Fact(Timeout = 30000)]
        public void TestParkedWorkerRetiresAfterIdleTimeout()
        {
            // A pool that only sheds threads when drained retains them for the lifetime of the process
            // if a drain never happens, so a parked worker must also expire on its own.
            var pool = ControlledThreadPool.Instance;
            pool.Drain();

            // The observer is sticky evidence that parking happened, so the test does not have to
            // sample IdleThreadCount during this deliberately short timeout window.
            int originalTimeout = ControlledThreadPool.IdleTimeoutMs;
            Action<PooledThread> originalObserver = ControlledThreadPool.ParkedWorkerObserver;
            ControlledThreadPool.IdleTimeoutMs = 50;
            using var parked = new ManualResetEventSlim(false);
            ControlledThreadPool.ParkedWorkerObserver = _ => parked.Set();
            try
            {
                PooledThread worker = pool.Rent();
                worker.Dispatch(() => WorkerDisposition.Reuse);

                Assert.True(parked.Wait(WaitTimeout),
                    "The worker never parked after completing its operation.");

                long createdBeforeExpiry = ControlledThreadPool.ThreadsCreated;

                // Note there is no drain here: the worker has to expire by itself.
                Assert.True(SpinWait.SpinUntil(() => pool.IdleThreadCount is 0, WaitTimeout),
                    "The parked worker never expired, so it would be retained until the pool is drained.");
                Assert.True(worker.OSThread.Join(WaitTimeout),
                    "The expired worker did not terminate.");

                // An expired worker is a tombstone in the bag rather than a reusable thread, so the
                // next rental has to create one.
                PooledThread next = pool.Rent();
                Assert.True(ControlledThreadPool.ThreadsCreated > createdBeforeExpiry,
                    "Renting after the expiry reused the expired worker.");
                next.Release();
            }
            finally
            {
                ControlledThreadPool.ParkedWorkerObserver = originalObserver;
                ControlledThreadPool.IdleTimeoutMs = originalTimeout;
                pool.Drain();
            }
        }

        [Fact(Timeout = 30000)]
        public void TestWorkerRetiresWhenItsWorkItemDemandsIt()
        {
            // A work item that reports Retire has decided that this thread is unsafe to run anything
            // else, which is what the runtime reports when an operation released its thread while still
            // holding the runtime lock. Parking such a thread would leave every later operation on it
            // running unsynchronized, so the demand has to actually terminate it.
            var pool = ControlledThreadPool.Instance;
            pool.Drain();

            try
            {
                PooledThread worker = pool.Rent();
                worker.Dispatch(() => WorkerDisposition.Retire);

                Assert.True(worker.OSThread.Join(WaitTimeout),
                    "The worker did not terminate after its work item demanded that it retire.");
                Assert.True(pool.IdleThreadCount is 0,
                    $"The pool retains {pool.IdleThreadCount} threads, so a worker that had to retire " +
                    "parked itself for reuse instead.");
            }
            finally
            {
                pool.Drain();
            }
        }

        [Fact(Timeout = 30000)]
        public void TestWorkerIsReusedAfterATornDownOperation()
        {
            // The counterpart of the test above: an operation cut short by its iteration tearing down
            // leaves a thread that is safe to reuse once its latched interrupt is drained, and reusing
            // it is what keeps tests with frequent detaches from minting threads until the desktop heap
            // is exhausted. Retiring on every non-Reuse disposition would silently undo that.
            var pool = ControlledThreadPool.Instance;
            pool.Drain();

            try
            {
                PooledThread worker = pool.Rent();
                worker.Dispatch(() => WorkerDisposition.Drain);

                Assert.True(SpinWait.SpinUntil(() => pool.IdleThreadCount is 1, WaitTimeout),
                    "The worker of a torn-down operation never parked, so it was retired rather than " +
                    "drained and reused.");
                Assert.True(worker.OSThread.IsAlive,
                    "The worker of a torn-down operation terminated instead of parking for reuse.");
            }
            finally
            {
                pool.Drain();
            }
        }

        [Fact(Timeout = 30000)]
        public void TestAbandonedReservationReleasesThreadAndMappings()
        {
            // A thread reserved for an operation is not reachable from the pool, so if the runtime fails
            // between reserving it and giving it the operation, nothing else can ever wake it and the
            // mappings published for the pair describe an operation that never ran.
            var pool = ControlledThreadPool.Instance;
            pool.Drain();

            PooledThread reserved = null;
            bool hasMappings = true;
            bool isFaultObserved = false;

            // The first reservation belongs to the operation that runs the test body, so failing that one
            // would stop the body from running. Failing the next one leaves the iteration alive, which is
            // what allows the body to observe the state the rollback left behind.
            bool isFirstReservation = true;
            ControlledThreadPool.ReservationFaultInjector = worker =>
            {
                if (isFirstReservation)
                {
                    isFirstReservation = false;
                    return;
                }

                if (reserved is null)
                {
                    reserved = worker;
                    throw new ThreadInterruptedException();
                }
            };

            try
            {
                this.RunSystematicTest(() =>
                {
                    try
                    {
                        Rewriting.Types.Threading.Tasks.Task.Run(() => { });
                    }
                    catch (Exception)
                    {
                        // The reservation below was failed on purpose; how that surfaces to the caller
                        // is not what this test is about.
                    }

                    if (reserved != null)
                    {
                        isFaultObserved = true;
                        hasMappings = CoyoteRuntime.Current.HasMappingsForThread(reserved);
                    }
                },
                this.GetConfiguration().WithTestingIterations(1).WithTestIterationsRunToCompletion());

                Assert.True(isFaultObserved, "The reservation was never failed.");
                Assert.NotNull(reserved);
                Assert.True(reserved.OSThread.Join(WaitTimeout),
                    "The reserved thread was never released, so it is blocked for the life of the process.");
                Assert.False(hasMappings,
                    "The runtime still maps the operation and the thread that was never given to it.");
            }
            finally
            {
                ControlledThreadPool.ReservationFaultInjector = null;
                pool.Drain();
            }
        }
    }
}

// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

using Monitor = System.Threading.Monitor;
using SynchronizedBlock = Microsoft.Coyote.Rewriting.Types.Threading.Monitor.SynchronizedBlock;

namespace Microsoft.Coyote.BugFinding.Tests
{
    public class MonitorTests : BaseBugFindingTest
    {
        public MonitorTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestSimpleMonitor()
        {
            this.Test(async () =>
            {
                SignalData signal = new SignalData();
                var t1 = Task.Run(signal.Wait);
                var t2 = Task.Run(signal.Signal);
                await Task.WhenAll(t1, t2);
            },
            this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        public void TestMonitorWithReentrancy1()
        {
            this.Test(() =>
            {
                SignalData signal = new SignalData();
                signal.ReentrantLock();
            },
            this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        public void TestMonitorWithReentrancy2()
        {
            this.Test(async () =>
            {
                SignalData signal = new SignalData();
                Task t1 = Task.Run(signal.ReentrantLock);
                Task t2 = Task.Run(signal.DoLock);
                await Task.WhenAll(t1, t2);
            },
            this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        public void TestMonitorWithReentrancy3()
        {
            this.Test(async () =>
            {
                SignalData signal = new SignalData();
                Task t1 = Task.Run(signal.ReentrantWait);
                Task t2 = Task.Run(signal.Signal);
                await Task.WhenAll(t1, t2);
            },
            this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        public void TestMonitorWithInvalidSyncObject()
        {
            this.TestWithException<ArgumentNullException>(() =>
            {
                using var monitor = SynchronizedBlock.Lock(null);
            },
            replay: true);
        }

        [Fact(Timeout = 5000)]
        public void TestMonitorWithInvalidWaitState()
        {
            this.TestWithException<SynchronizationLockException>(() =>
            {
                SynchronizedBlock monitor;
                using (monitor = SynchronizedBlock.Lock(new object()))
                {
                }

                monitor.Wait();
            },
            replay: true);
        }

        [Fact(Timeout = 5000)]
        public void TestMonitorWithInvalidPulseState()
        {
            this.TestWithException<SynchronizationLockException>(() =>
            {
                SynchronizedBlock monitor;
                using (monitor = SynchronizedBlock.Lock(new object()))
                {
                }

                monitor.Pulse();
            },
            replay: true);
        }

        [Fact(Timeout = 5000)]
        public void TestMonitorWithInvalidPulseAllState()
        {
            this.TestWithException<SynchronizationLockException>(() =>
            {
                SynchronizedBlock monitor;
                using (monitor = SynchronizedBlock.Lock(new object()))
                {
                }

                monitor.PulseAll();
            },
            replay: true);
        }

        [Fact(Timeout = 5000)]
        public void TestMonitorWithInvalidUsage()
        {
            this.TestWithError(async () =>
            {
                try
                {
                    var monitor = SynchronizedBlock.Lock(new object());
                    // We yield to make sure the execution is asynchronous.
                    await Task.Yield();
                    monitor.Pulse();

                    // We do not dispose inside a using statement, because the `SynchronizationLockException`
                    // will trigger the disposal, which will fail because an await statement is not allowed
                    // inside a synchronized block. The C# compiler normally prevents it when using the lock
                    // statement, but we cannot prevent it when directly using the mock.
                    monitor.Dispose();
                }
                catch (SynchronizationLockException)
                {
                    Specification.Assert(false, "Expected exception thrown.");
                }
            },
            expectedError: "Expected exception thrown.",
            replay: true);
        }

        [Fact(Timeout = 5000)]
        public void TestComplexMonitor()
        {
            this.Test(async () =>
            {
                object syncObject = new object();
                bool waiting = false;
                List<string> log = new List<string>();
                Task t1 = Task.Run(() =>
                {
                    Monitor.Enter(syncObject);
                    log.Add("waiting");
                    waiting = true;
                    Monitor.Wait(syncObject);
                    log.Add("received pulse");
                    Monitor.Exit(syncObject);
                });

                Task t2 = Task.Run(async () =>
                {
                    while (!waiting)
                    {
                        await Task.Delay(1);
                    }

                    Monitor.Enter(syncObject);
                    Monitor.Pulse(syncObject);
                    log.Add("pulsed");
                    Monitor.Exit(syncObject);
                });

                await Task.WhenAll(t1, t2);

                string expected = "waiting, pulsed, received pulse";
                string actual = string.Join(", ", log);
                Specification.Assert(expected == actual, "ControlledMonitor out of order, '{0}' instead of '{1}'", actual, expected);
            },
            this.GetConfiguration());
        }

        [Fact(Timeout = 5000)]
        public void TestMonitorCacheResetAcrossIterations()
        {
            // Regression test: verifies the SynchronizedBlock cache is properly reset
            // between iterations, preventing orphaned entries from persisting.
            this.Test(async () =>
            {
                object syncObject = new object();
                bool waiting = false;
                Task t1 = Task.Run(() =>
                {
                    Monitor.Enter(syncObject);
                    waiting = true;
                    Monitor.Wait(syncObject);
                    Monitor.Exit(syncObject);
                });

                Task t2 = Task.Run(async () =>
                {
                    while (!waiting)
                    {
                        await Task.Delay(1);
                    }

                    Monitor.Enter(syncObject);
                    Monitor.Pulse(syncObject);
                    Monitor.Exit(syncObject);
                });

                await Task.WhenAll(t1, t2);
            },
            this.GetConfiguration().WithTestingIterations(10));
        }

        [Fact(Timeout = 5000)]
        public void TestMonitorIsEnteredWhileHeld()
        {
            this.Test(() =>
            {
                object syncObject = new object();
                lock (syncObject)
                {
                    Assert.True(Monitor.IsEntered(syncObject),
                        "IsEntered must report true while the current operation holds the lock.");
                }
            },
            this.GetConfiguration().WithTestingIterations(10));
        }

        [Fact(Timeout = 5000)]
        public void TestMonitorIsEnteredAfterRelease()
        {
            // Regression test: a released lock has no SynchronizedBlock — blocks are evicted once their use
            // count reaches zero — and IsEntered used to resolve through the helper that THROWS on a missing
            // block. Answering "is this held" with SynchronizationLockException made the predicate unusable
            // for its only purpose: asking before deciding whether it is safe to block.
            this.Test(() =>
            {
                object syncObject = new object();
                lock (syncObject)
                {
                }

                Assert.False(Monitor.IsEntered(syncObject),
                    "IsEntered must report false once the lock has been released.");
            },
            this.GetConfiguration().WithTestingIterations(10));
        }

        [Fact(Timeout = 5000)]
        public void TestMonitorIsEnteredOnNeverLockedObject()
        {
            // The same regression from the other side: an object nobody has ever locked is simply absent from
            // the block cache, which is an answer (false), not a contract violation.
            this.Test(() =>
            {
                object syncObject = new object();
                Assert.False(Monitor.IsEntered(syncObject),
                    "IsEntered must report false for an object that was never locked.");
            },
            this.GetConfiguration().WithTestingIterations(10));
        }

        [Fact(Timeout = 5000)]
        public void TestMonitorIsEnteredIsPerOperation()
        {
            // Ownership is per controlled operation, so a lock held by ANOTHER operation must read false here
            // rather than true (or throw) — the block exists, it is simply owned by somebody else.
            //
            // The observer runs from INSIDE the holder's lock and is joined without awaiting, deliberately:
            // holding a lock across an await hands the continuation to a different controlled operation, and
            // the Exit would then run unowned. The observer never wants the lock, so the blocking join cannot
            // deadlock against it.
            this.Test(() =>
            {
                object syncObject = new object();
                lock (syncObject)
                {
                    Task observer = Task.Run(() =>
                    {
                        Assert.False(Monitor.IsEntered(syncObject),
                            "IsEntered must report false for an operation that does not hold the lock.");
                    });

                    observer.Wait();
                }
            },
            this.GetConfiguration().WithTestingIterations(10));
        }

        private class SignalData
        {
            private readonly object SyncObject;
            internal bool Signalled;

            internal SignalData()
            {
                this.SyncObject = new object();
                this.Signalled = false;
            }

            internal void Signal()
            {
                using var monitor = SynchronizedBlock.Lock(this.SyncObject);
                this.Signalled = true;
                monitor.Pulse();
            }

            internal void Wait()
            {
                using var monitor = SynchronizedBlock.Lock(this.SyncObject);
                while (!this.Signalled)
                {
                    bool result = monitor.Wait();
                    Assert.True(result, "Wait returned false.");
                }
            }

            internal void ReentrantLock()
            {
                Debug.WriteLine("Entering lock on task {0}.", GetCurrentTaskId());
                using var monitor = SynchronizedBlock.Lock(this.SyncObject);
                Debug.WriteLine("Entered lock on task {0}.", GetCurrentTaskId());
                this.DoLock();
            }

            internal void DoLock()
            {
                using var monitor = SynchronizedBlock.Lock(this.SyncObject);
                Debug.WriteLine("Re-entered lock from the same task {0}.", GetCurrentTaskId());
            }

            internal void ReentrantWait()
            {
                Debug.WriteLine("Entering lock on task {0}.", GetCurrentTaskId());
                using var monitor = SynchronizedBlock.Lock(this.SyncObject);
                Debug.WriteLine("Entered lock on task {0}.", GetCurrentTaskId());
                this.DoWait();
            }

            internal void DoWait()
            {
                using var monitor = SynchronizedBlock.Lock(this.SyncObject);
                Debug.WriteLine("Re-entered lock from the same task {0}.", GetCurrentTaskId());
                Debug.WriteLine("Task {0} is now waiting...", GetCurrentTaskId());
                this.Wait();
                Debug.WriteLine("Task {0} received the signal.", GetCurrentTaskId());
            }

            internal static int GetCurrentTaskId() => Task.CurrentId ?? 0;
        }

        /// <summary>
        /// TryEnter is a PROBE, not an acquisition: when another operation owns the lock it must report
        /// failure and leave the caller running, so the caller can take its other branch.
        /// </summary>
        /// <remarks>
        /// Routing TryEnter through the blocking Enter model made this unobservable — it queued the probing
        /// operation behind the owner and then reported success unconditionally, so every
        /// "the lock was busy, do something else" branch in a program under test was unreachable and the
        /// probing operation parked inside a call the program wrote as non-blocking. Asserted ACROSS
        /// schedules, because whether the probe lands while the lock is held is itself a scheduling choice.
        /// </remarks>
        [Fact(Timeout = 5000)]
        public void TestMonitorTryEnterCanRefuseALockHeldByAnotherOperation()
        {
            bool refusedAtLeastOnce = false;

            this.Test(
                async () =>
                {
                    object syncObject = new object();

                    Task holder = Task.Run(() =>
                    {
                        lock (syncObject)
                        {
                            // Hold it across a scheduling point, so the prober can run while it is taken.
                            SchedulingPoint.Interleave();
                        }
                    });

                    Task prober = Task.Run(() =>
                    {
                        if (Monitor.TryEnter(syncObject))
                        {
                            Monitor.Exit(syncObject);
                        }
                        else
                        {
                            refusedAtLeastOnce = true;
                        }
                    });

                    await Task.WhenAll(holder, prober);
                },
                this.GetConfiguration().WithTestingIterations(100));

            Assert.True(
                refusedAtLeastOnce,
                "Monitor.TryEnter never refused a lock held by another operation across 100 schedules: it is " +
                "being modelled as a blocking Enter that always succeeds.");
        }

        /// <summary>Monitor is reentrant, so the owner's own probe still succeeds.</summary>
        [Fact(Timeout = 5000)]
        public void TestMonitorTryEnterSucceedsReentrantly()
        {
            this.Test(
                () =>
                {
                    object syncObject = new object();
                    lock (syncObject)
                    {
                        bool taken = Monitor.TryEnter(syncObject);
                        Specification.Assert(taken, "TryEnter refused the operation that already owns the lock.");
                        if (taken)
                        {
                            Monitor.Exit(syncObject);
                        }
                    }
                },
                this.GetConfiguration().WithTestingIterations(100));
        }

        /// <summary>An uncontended probe takes the lock, and releasing it leaves the lock free.</summary>
        [Fact(Timeout = 5000)]
        public void TestMonitorTryEnterAcquiresAFreeLock()
        {
            this.Test(
                () =>
                {
                    object syncObject = new object();

                    bool taken = Monitor.TryEnter(syncObject);
                    Specification.Assert(taken, "TryEnter refused a free lock.");
                    Specification.Assert(Monitor.IsEntered(syncObject), "TryEnter reported success without taking the lock.");
                    Monitor.Exit(syncObject);

                    Specification.Assert(!Monitor.IsEntered(syncObject), "The lock was still held after Exit.");

                    // Reacquiring proves the refused/acquired bookkeeping left nothing behind.
                    bool retaken = Monitor.TryEnter(syncObject);
                    Specification.Assert(retaken, "TryEnter refused a lock that had been released.");
                    Monitor.Exit(syncObject);
                },
                this.GetConfiguration().WithTestingIterations(100));
        }
    }
}

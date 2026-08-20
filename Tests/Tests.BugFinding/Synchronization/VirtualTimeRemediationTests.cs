// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;
using Monitor = System.Threading.Monitor;

namespace Microsoft.Coyote.BugFinding.Tests
{
    public class VirtualTimeRemediationTests : BaseBugFindingTest
    {
        public VirtualTimeRemediationTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 15000)]
        [Trait("Category", "VirtualTimeRemediation")]
        public void TestShorterTaskDelayCannotFinishAfterLongerDelay()
        {
            this.Test(async () =>
            {
                var order = new List<int>();
                Task longer = Task.Delay(20);
                Task shorter = Task.Delay(10);
                Task first = await Task.WhenAny(longer, shorter);
                order.Add(ReferenceEquals(first, shorter) ? 10 : 20);
                Specification.Assert(order[0] is 10,
                    "A 20ms virtual deadline completed before a 10ms deadline.");
                await Task.WhenAll(longer, shorter);
            }, this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 15000)]
        [Trait("Category", "VirtualTimeRemediation")]
        public void TestPositiveMonitorTryEnterCanAcquireBeforeItsDeadline()
        {
            bool observedAcquisition = false;
            this.Test(async () =>
            {
                object sync = new object();
                bool acquired = false;
                var ownerEntered = new TaskCompletionSource<bool>();
                Task owner = Task.Run(() =>
                {
                    Monitor.Enter(sync);
                    ownerEntered.SetResult(true);
                    Thread.Sleep(1);
                    Monitor.Exit(sync);
                });

                await ownerEntered.Task;
                acquired = Monitor.TryEnter(sync, TimeSpan.FromMilliseconds(10));
                if (acquired)
                {
                    observedAcquisition = true;
                    Monitor.Exit(sync);
                }

                await owner;
            }, this.GetConfiguration().WithTestingIterations(100));

            Assert.True(observedAcquisition,
                "No explored schedule allowed a positive-timeout TryEnter to acquire after release.");
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "VirtualTimeRemediation")]
        public void TestSemaphoreSlimFiniteWaitTimesOut()
        {
            this.Test(() =>
            {
                using var semaphore = new SemaphoreSlim(0, 1);
                Specification.Assert(!semaphore.Wait(5),
                    "A finite SemaphoreSlim wait did not report its virtual timeout.");
            });
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "VirtualTimeRemediation")]
        public void TestWaitHandleFiniteWaitTimesOut()
        {
            this.Test(() =>
            {
                using var signal = new ManualResetEvent(false);
                Specification.Assert(!signal.WaitOne(5),
                    "A finite wait handle wait did not report its virtual timeout.");
            });
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "VirtualTimeRemediation")]
        public void TestTaskFiniteWaitTimesOut()
        {
            this.Test(() =>
            {
                var pending = new TaskCompletionSource<bool>();
                Specification.Assert(!pending.Task.Wait(5),
                    "A finite task wait did not report its virtual timeout.");
            });
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "VirtualTimeRemediation")]
        public void TestSpinUntilFiniteWaitTimesOut()
        {
            this.Test(() =>
            {
                Specification.Assert(!SpinWait.SpinUntil(() => false, 5),
                    "A finite SpinUntil did not report its virtual timeout.");
            });
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "VirtualTimeRemediation")]
        public void TestMonitorWaitTimesOutAndReacquiresTheLock()
        {
            this.Test(() =>
            {
                object sync = new object();
                Monitor.Enter(sync);
                bool pulsed = Monitor.Wait(sync, 5);
                Specification.Assert(!pulsed, "Monitor.Wait reported a pulse at its deadline.");
                Specification.Assert(Monitor.IsEntered(sync),
                    "Monitor.Wait did not reacquire the lock after timing out.");
                Monitor.Exit(sync);
            });
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "VirtualTimeRemediation")]
        public void TestBlockingCollectionFiniteTakeTimesOut()
        {
            this.Test(() =>
            {
                using var collection = new BlockingCollection<int>();
                Specification.Assert(!collection.TryTake(out _, 5),
                    "BlockingCollection.TryTake did not report its virtual timeout.");
            });
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "VirtualTimeRemediation")]
        public void TestWaitHandleAllAndAnyFiniteWaitsTimeOut()
        {
            this.Test(() =>
            {
                using var first = new ManualResetEvent(false);
                using var second = new ManualResetEvent(false);
                WaitHandle[] handles = { first, second };
                Specification.Assert(!WaitHandle.WaitAll(handles, 5),
                    "WaitAll did not report its virtual timeout.");
                Specification.Assert(WaitHandle.WaitAny(handles, 5) is WaitHandle.WaitTimeout,
                    "WaitAny did not return WaitTimeout at its virtual deadline.");
            });
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "VirtualTimeRemediation")]
        public void TestTaskAllAndAnyFiniteWaitsTimeOut()
        {
            this.Test(() =>
            {
                var first = new TaskCompletionSource<bool>();
                var second = new TaskCompletionSource<bool>();
                Task[] tasks = { first.Task, second.Task };
                Specification.Assert(!Task.WaitAll(tasks, 5),
                    "Task.WaitAll did not report its virtual timeout.");
                Specification.Assert(Task.WaitAny(tasks, 5) is -1,
                    "Task.WaitAny did not return -1 at its virtual deadline.");
            });
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "VirtualTimeRemediation")]
        public void TestSemaphoreSlimFiniteWaitAsyncTimesOut()
        {
            this.Test(async () =>
            {
                using var semaphore = new SemaphoreSlim(0, 1);
                bool entered = await semaphore.WaitAsync(5);
                Specification.Assert(!entered,
                    "SemaphoreSlim.WaitAsync did not report its virtual timeout.");
            });
        }

        [Fact(Timeout = 15000)]
        [Trait("Category", "VirtualTimeRemediation")]
        public void TestReaderWriterLockSlimFiniteWaitsTimeOutInEveryMode()
        {
            this.Test(() =>
            {
                using var rw = new ReaderWriterLockSlim();
                rw.EnterWriteLock();
                Task<bool> reader = Task.Run(() => rw.TryEnterReadLock(5));
                Task<bool> upgradeable = Task.Run(() => rw.TryEnterUpgradeableReadLock(5));
                Task.WaitAll(reader, upgradeable);
                Specification.Assert(!reader.Result && !upgradeable.Result,
                    "A read mode ignored its virtual deadline while a writer held the lock.");
                rw.ExitWriteLock();

                rw.EnterReadLock();
                Task<bool> writer = Task.Run(() => rw.TryEnterWriteLock(5));
                writer.Wait();
                Specification.Assert(!writer.Result,
                    "Write mode ignored its virtual deadline while a reader held the lock.");
                rw.ExitReadLock();
            });
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "VirtualTimeRemediation")]
        public void TestThreadJoinFiniteWaitTimesOut()
        {
            this.Test(() =>
            {
                var thread = new Thread(() => Thread.Sleep(10));
                thread.Start();
                Specification.Assert(!thread.Join(1),
                    "Thread.Join did not report its virtual timeout.");
                thread.Join();
            });
        }
    }
}

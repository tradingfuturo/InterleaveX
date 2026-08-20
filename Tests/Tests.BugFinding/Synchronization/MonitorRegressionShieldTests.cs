// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System.Reflection;
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
    public class MonitorRegressionShieldTests : BaseBugFindingTest
    {
        public MonitorRegressionShieldTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestRefEnterWaiterIsNotCorruptedByRefusedProbe()
        {
            this.Test(async () =>
            {
                object syncObject = new object();
                Monitor.Enter(syncObject);
                Task waiter = Task.Run(() =>
                {
                    bool lockTaken = false;
                    Monitor.Enter(syncObject, ref lockTaken);
                    Specification.Assert(lockTaken,
                        "Enter(ref lockTaken) reported false after it acquired the lock.");
                    if (lockTaken)
                    {
                        Monitor.Exit(syncObject);
                    }
                });

                while (GetReadyQueueCount(syncObject) is 0)
                {
                    SchedulingPoint.Interleave();
                }

                Task prober = Task.Run(() =>
                {
                    bool taken = Monitor.TryEnter(syncObject);
                    Specification.Assert(!taken, "TryEnter unexpectedly acquired a held lock.");
                });
                prober.Wait();
                Monitor.Exit(syncObject);
                await waiter;
            }, this.GetConfiguration().WithTestingIterations(20));
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestFreeProbeKeepsItsBlockAliveAcrossAcquireSchedulingPoint()
        {
            this.Test(async () =>
            {
                object syncObject = new object();
                Task probe = Task.Run(() =>
                {
                    bool taken = Monitor.TryEnter(syncObject);
                    Specification.Assert(taken, "TryEnter refused a free lock.");
                    if (taken)
                    {
                        Monitor.Exit(syncObject);
                    }
                });
                Task churn = Task.Run(() =>
                {
                    Monitor.Enter(syncObject);
                    Monitor.Exit(syncObject);
                });

                await Task.WhenAll(probe, churn);
            }, this.GetConfiguration().WithLockAccessRaceCheckingEnabled().WithTestingIterations(100));
        }

        private static int GetReadyQueueCount(object syncObject)
        {
            SynchronizedBlock block = SynchronizedBlock.Find(syncObject);
            FieldInfo field = typeof(SynchronizedBlock).GetField(
                "ReadyQueue", BindingFlags.Instance | BindingFlags.NonPublic);
            return ((System.Collections.ICollection)field.GetValue(block)).Count;
        }
    }
}

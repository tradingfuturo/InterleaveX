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

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// BCL-contract shields for the ReaderWriterLockSlim model.  These use the public state
    /// exposed by ReaderWriterLockSlim, rather than model internals, so they also prove rewriting.
    /// </summary>
    public class ReaderWriterLockSlimRegressionShieldTests : BaseBugFindingTest
    {
        public ReaderWriterLockSlimRegressionShieldTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestNoRecursionRejectsRecursiveRead()
        {
            this.TestWithException<LockRecursionException>(() =>
            {
                using var rw = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
                rw.EnterReadLock();
                rw.EnterReadLock();
            }, replay: true);
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestNoRecursionRejectsRecursiveWrite()
        {
            this.TestWithException<LockRecursionException>(() =>
            {
                using var rw = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
                rw.EnterWriteLock();
                rw.EnterWriteLock();
            }, replay: true);
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestSupportsRecursionPreservesReadOwnershipAndCounters()
        {
            this.Test(() =>
            {
                using var rw = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
                Specification.Assert(rw.RecursionPolicy is LockRecursionPolicy.SupportsRecursion,
                    "The configured recursion policy was not exposed.");

                rw.EnterReadLock();
                rw.EnterReadLock();
                Specification.Assert(rw.IsReadLockHeld && rw.RecursiveReadCount is 2 && rw.CurrentReadCount is 1,
                    "Recursive read state did not match the BCL contract.");

                rw.ExitReadLock();
                Specification.Assert(rw.IsReadLockHeld && rw.RecursiveReadCount is 1 && rw.CurrentReadCount is 1,
                    "The first recursive read exit released the lock too early.");
                rw.ExitReadLock();
                Specification.Assert(!rw.IsReadLockHeld && rw.RecursiveReadCount is 0 && rw.CurrentReadCount is 0,
                    "The final recursive read exit did not release the lock.");
            });
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestSupportsRecursionPreservesUpgradeableAndWriteCounts()
        {
            this.Test(() =>
            {
                using var rw = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
                rw.EnterUpgradeableReadLock();
                rw.EnterUpgradeableReadLock();
                Specification.Assert(rw.IsUpgradeableReadLockHeld && rw.RecursiveUpgradeCount is 2,
                    "Recursive upgradeable-read ownership was lost.");

                rw.EnterWriteLock();
                rw.EnterWriteLock();
                Specification.Assert(rw.IsWriteLockHeld && rw.RecursiveWriteCount is 2,
                    "Recursive write ownership was lost during an upgrade.");

                rw.ExitWriteLock();
                Specification.Assert(rw.IsWriteLockHeld && rw.RecursiveWriteCount is 1,
                    "The first recursive write exit released the write lock too early.");
                rw.ExitWriteLock();
                rw.ExitUpgradeableReadLock();
                Specification.Assert(rw.IsUpgradeableReadLockHeld && rw.RecursiveUpgradeCount is 1,
                    "The first recursive upgradeable-read exit released the lock too early.");
                rw.ExitUpgradeableReadLock();
            });
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestUpgradeableOwnerCanUpgradeAndDowngradeWithNoRecursion()
        {
            this.Test(() =>
            {
                using var rw = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
                rw.EnterUpgradeableReadLock();
                rw.EnterWriteLock();
                rw.ExitWriteLock();
                rw.EnterReadLock();
                rw.ExitUpgradeableReadLock();
                Specification.Assert(rw.IsReadLockHeld && rw.RecursiveReadCount is 1,
                    "The permitted upgradeable-to-read downgrade lost its read ownership.");
                rw.ExitReadLock();
            });
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestSupportsRecursionAllowsWriterCrossModeRecursion()
        {
            this.Test(() =>
            {
                using var rw = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
                rw.EnterWriteLock();
                rw.EnterReadLock();
                rw.EnterUpgradeableReadLock();
                Specification.Assert(rw.RecursiveWriteCount is 1 && rw.RecursiveReadCount is 1 &&
                    rw.RecursiveUpgradeCount is 1,
                    "A recursive writer did not retain independent ownership for each permitted mode.");
                rw.ExitUpgradeableReadLock();
                rw.ExitReadLock();
                rw.ExitWriteLock();
            });
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestReadFirstOwnerCannotUpgradeRegardlessOfRecursionPolicy()
        {
            this.Test(() =>
            {
                using var rw = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
                rw.EnterReadLock();
                Assert.Throws<LockRecursionException>(() => rw.EnterWriteLock());
                Assert.Throws<LockRecursionException>(() => rw.EnterUpgradeableReadLock());
                rw.ExitReadLock();
            });
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestWaitingReadCountExposesAnActuallyParkedReader()
        {
            this.Test(() =>
            {
                using var rw = new ReaderWriterLockSlim();
                rw.EnterWriteLock();
                Task reader = Task.Run(() =>
                {
                    rw.EnterReadLock();
                    rw.ExitReadLock();
                });

                WaitUntilQueued(rw, "PausedReaders");
                Specification.Assert(rw.WaitingReadCount is 1,
                    "WaitingReadCount did not expose the reader already parked in the model queue.");
                rw.ExitWriteLock();
                reader.Wait();
            }, this.GetConfiguration().WithTestingIterations(20));
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestWaitingUpgradeCountExposesAnActuallyParkedUpgradeableReader()
        {
            this.Test(() =>
            {
                using var rw = new ReaderWriterLockSlim();
                rw.EnterWriteLock();
                Task upgradeable = Task.Run(() =>
                {
                    rw.EnterUpgradeableReadLock();
                    rw.ExitUpgradeableReadLock();
                });

                WaitUntilQueued(rw, "PausedUpgradeableReaders");
                Specification.Assert(rw.WaitingUpgradeCount is 1,
                    "WaitingUpgradeCount did not expose the upgradeable reader already parked in the model queue.");
                rw.ExitWriteLock();
                upgradeable.Wait();
            }, this.GetConfiguration().WithTestingIterations(20));
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestWaitingWriteCountExposesAnActuallyParkedWriter()
        {
            this.Test(() =>
            {
                using var rw = new ReaderWriterLockSlim();
                rw.EnterReadLock();
                Task writer = Task.Run(() =>
                {
                    rw.EnterWriteLock();
                    rw.ExitWriteLock();
                });

                WaitUntilQueued(rw, "PausedWriters");
                Specification.Assert(rw.WaitingWriteCount is 1,
                    "WaitingWriteCount did not expose the writer already parked in the model queue.");
                rw.ExitReadLock();
                writer.Wait();
            }, this.GetConfiguration().WithTestingIterations(20));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestDisposeRejectsAnActiveModeledOwner()
        {
            this.Test(() =>
            {
                var rw = new ReaderWriterLockSlim();
                rw.EnterWriteLock();
                Assert.Throws<SynchronizationLockException>(() => rw.Dispose());
                rw.ExitWriteLock();
                rw.Dispose();
            });
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestDisposedLockRejectsFurtherAcquisition()
        {
            this.Test(() =>
            {
                var rw = new ReaderWriterLockSlim();
                rw.Dispose();
                Assert.Throws<ObjectDisposedException>(() => rw.EnterReadLock());
            });
        }

        private static void WaitUntilQueued(ReaderWriterLockSlim rw, string fieldName)
        {
            while (GetQueueCount(rw, fieldName) is 0)
            {
                SchedulingPoint.Interleave();
            }
        }

        private static int GetQueueCount(ReaderWriterLockSlim rw, string fieldName)
        {
            FieldInfo field = rw.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return ((ICollection)field.GetValue(rw)).Count;
        }
    }
}

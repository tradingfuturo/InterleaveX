// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    public class ReaderWriterLockSlimTests : BaseBugFindingTest
    {
        public ReaderWriterLockSlimTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestReaderWriterLockSlimWithSequentialAccess()
        {
            this.Test(() =>
            {
                using var rwlock = new ReaderWriterLockSlim();
                int value = 0;

                rwlock.EnterWriteLock();
                value++;
                rwlock.ExitWriteLock();

                rwlock.EnterReadLock();
                int read = value;
                rwlock.ExitReadLock();

                Specification.Assert(read == 1, "Value is {0} instead of {1}.", read, 1);
            });
        }

        [Fact(Timeout = 5000)]
        public void TestReaderWriterLockSlimReportsHeldState()
        {
            this.Test(() =>
            {
                using var rwlock = new ReaderWriterLockSlim();
                Specification.Assert(!rwlock.IsWriteLockHeld && !rwlock.IsReadLockHeld, "Lock unexpectedly held.");

                rwlock.EnterWriteLock();
                Specification.Assert(rwlock.IsWriteLockHeld, "Write lock not reported held.");
                rwlock.ExitWriteLock();

                rwlock.EnterReadLock();
                Specification.Assert(rwlock.IsReadLockHeld, "Read lock not reported held.");
                Specification.Assert(rwlock.CurrentReadCount == 1, "CurrentReadCount is {0} instead of 1.", rwlock.CurrentReadCount);
                rwlock.ExitReadLock();
            });
        }

        [Fact(Timeout = 5000)]
        public void TestParallelWritersAreExclusive()
        {
            this.Test(() =>
            {
                using var rwlock = new ReaderWriterLockSlim();
                int value = 0;
                bool overlap = false;

                var t1 = Task.Run(() =>
                {
                    rwlock.EnterWriteLock();
                    value++;
                    SchedulingPoint.Interleave();
                    overlap |= value is 2;
                    value--;
                    rwlock.ExitWriteLock();
                });

                var t2 = Task.Run(() =>
                {
                    rwlock.EnterWriteLock();
                    value++;
                    SchedulingPoint.Interleave();
                    overlap |= value is 2;
                    value--;
                    rwlock.ExitWriteLock();
                });

                Task.WaitAll(t1, t2);

                // Writers are mutually exclusive: they never co-occupy the critical section.
                Specification.Assert(value == 0, "Value is {0} instead of 0.", value);
                Specification.Assert(!overlap, "Two writers were concurrently active.");
            },
            configuration: this.GetConfiguration().WithTestingIterations(200));
        }

        [Fact(Timeout = 5000)]
        public void TestWriterExcludesReaders()
        {
            this.Test(() =>
            {
                using var rwlock = new ReaderWriterLockSlim();
                bool writerActive = false;
                bool violation = false;

                var writer = Task.Run(() =>
                {
                    rwlock.EnterWriteLock();
                    writerActive = true;
                    SchedulingPoint.Interleave();
                    writerActive = false;
                    rwlock.ExitWriteLock();
                });

                var reader = Task.Run(() =>
                {
                    rwlock.EnterReadLock();
                    violation |= writerActive;
                    SchedulingPoint.Interleave();
                    violation |= writerActive;
                    rwlock.ExitReadLock();
                });

                Task.WaitAll(writer, reader);

                // A reader must never observe an active writer.
                Specification.Assert(!violation, "A reader observed an active writer.");
            },
            configuration: this.GetConfiguration().WithTestingIterations(200));
        }

        [Fact(Timeout = 5000)]
        public void TestConcurrentReadersCanOverlap()
        {
            this.TestWithError(() =>
            {
                using var rwlock = new ReaderWriterLockSlim();
                int concurrent = 0;
                bool bothInside = false;

                var t1 = Task.Run(() =>
                {
                    rwlock.EnterReadLock();
                    concurrent++;
                    SchedulingPoint.Interleave();
                    bothInside |= concurrent is 2;
                    concurrent--;
                    rwlock.ExitReadLock();
                });

                var t2 = Task.Run(() =>
                {
                    rwlock.EnterReadLock();
                    concurrent++;
                    SchedulingPoint.Interleave();
                    bothInside |= concurrent is 2;
                    concurrent--;
                    rwlock.ExitReadLock();
                });

                Task.WaitAll(t1, t2);

                // Readers are NOT mutually exclusive — some schedule has both inside the
                // read lock at once. Asserting otherwise must fail, proving reader concurrency
                // is modelled (a plain mutual-exclusion lock could never reach this).
                Specification.Assert(!bothInside, "Expected assertion failed!");
            },
            configuration: this.GetConfiguration().WithTestingIterations(200),
            expectedError: "Expected assertion failed!",
            replay: true);
        }

        [Fact(Timeout = 5000)]
        public void TestReaderWriterLockSlimThrowsOnInvalidReadExit()
        {
            this.TestWithException<SynchronizationLockException>(() =>
            {
                using var rwlock = new ReaderWriterLockSlim();
                rwlock.ExitReadLock();
            },
            replay: true);
        }

        [Fact(Timeout = 5000)]
        public void TestReaderWriterLockSlimThrowsOnInvalidWriteExit()
        {
            this.TestWithException<SynchronizationLockException>(() =>
            {
                using var rwlock = new ReaderWriterLockSlim();
                rwlock.ExitWriteLock();
            },
            replay: true);
        }

        [Fact(Timeout = 5000)]
        public void TestReaderWriterLockSlimThrowsOnInvalidUpgradeableExit()
        {
            this.TestWithException<SynchronizationLockException>(() =>
            {
                using var rwlock = new ReaderWriterLockSlim();
                rwlock.ExitUpgradeableReadLock();
            },
            replay: true);
        }

        [Fact(Timeout = 5000)]
        public void TestUpgradeableReadLockCanUpgradeToWrite()
        {
            this.Test(() =>
            {
                using var rwlock = new ReaderWriterLockSlim();

                rwlock.EnterUpgradeableReadLock();
                Specification.Assert(rwlock.IsUpgradeableReadLockHeld, "Upgradeable read lock not reported held.");
                Specification.Assert(!rwlock.IsWriteLockHeld, "Write lock unexpectedly held.");

                rwlock.EnterWriteLock();
                Specification.Assert(rwlock.IsUpgradeableReadLockHeld, "Upgradeable read lock not reported held after write upgrade.");
                Specification.Assert(rwlock.IsWriteLockHeld, "Write lock not reported held after upgrade.");

                rwlock.ExitWriteLock();
                Specification.Assert(rwlock.IsUpgradeableReadLockHeld, "Upgradeable read lock not reported held after write exit.");
                Specification.Assert(!rwlock.IsWriteLockHeld, "Write lock still reported held after exit.");
                rwlock.ExitUpgradeableReadLock();
            });
        }

        [Fact(Timeout = 5000)]
        public void TestUpgradeableReadLockAllowsConcurrentReader()
        {
            this.TestWithError(() =>
            {
                using var rwlock = new ReaderWriterLockSlim();
                int concurrent = 0;
                bool bothInside = false;

                var upgradeable = Task.Run(() =>
                {
                    rwlock.EnterUpgradeableReadLock();
                    concurrent++;
                    SchedulingPoint.Interleave();
                    bothInside |= concurrent is 2;
                    concurrent--;
                    rwlock.ExitUpgradeableReadLock();
                });

                var reader = Task.Run(() =>
                {
                    rwlock.EnterReadLock();
                    concurrent++;
                    SchedulingPoint.Interleave();
                    bothInside |= concurrent is 2;
                    concurrent--;
                    rwlock.ExitReadLock();
                });

                Task.WaitAll(upgradeable, reader);

                Specification.Assert(!bothInside, "Expected assertion failed!");
            },
            configuration: this.GetConfiguration().WithTestingIterations(200),
            expectedError: "Expected assertion failed!",
            replay: true);
        }

        [Fact(Timeout = 5000)]
        public void TestReadLockCannotUpgradeToUpgradeableReadLock()
        {
            this.TestWithException<LockRecursionException>(() =>
            {
                using var rwlock = new ReaderWriterLockSlim();
                rwlock.EnterReadLock();
                rwlock.EnterUpgradeableReadLock();
            },
            replay: true);
        }

        [Fact(Timeout = 5000)]
        public void TestReaderWriterLockSlimWithRecursiveWriteDeadlock()
        {
            this.TestWithError(() =>
            {
                using var rwlock = new ReaderWriterLockSlim();
                rwlock.EnterWriteLock();
                rwlock.EnterWriteLock();
            },
            configuration: this.GetConfiguration().WithDeadlockTimeout(10),
            errorChecker: (e) =>
            {
                Assert.StartsWith("Deadlock detected.", e);
            },
            replay: true);
        }
    }
}

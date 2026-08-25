// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;
using ControlledReaderWriterLockSlim = Microsoft.Coyote.Rewriting.Types.Threading.ReaderWriterLockSlim;

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
                var queued = new TaskCompletionSource<bool>();
                ControlledReaderWriterLockSlim.SetWaiterQueuedCallbackForTesting(
                    rw, () => queued.TrySetResult(true));
                Task reader = Task.Run(() =>
                {
                    rw.EnterReadLock();
                    rw.ExitReadLock();
                });

                queued.Task.Wait();
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
                var queued = new TaskCompletionSource<bool>();
                ControlledReaderWriterLockSlim.SetWaiterQueuedCallbackForTesting(
                    rw, () => queued.TrySetResult(true));
                Task upgradeable = Task.Run(() =>
                {
                    rw.EnterUpgradeableReadLock();
                    rw.ExitUpgradeableReadLock();
                });

                queued.Task.Wait();
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
                var queued = new TaskCompletionSource<bool>();
                ControlledReaderWriterLockSlim.SetWaiterQueuedCallbackForTesting(
                    rw, () => queued.TrySetResult(true));
                Task writer = Task.Run(() =>
                {
                    rw.EnterWriteLock();
                    rw.ExitWriteLock();
                });

                queued.Task.Wait();
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

        [Theory(Timeout = 10000)]
        [InlineData("Read")]
        [InlineData("Write")]
        [InlineData("UpgradeableRead")]
        [Trait("Category", "ReviewRemediation")]
        public void TestUncontrolledAcquisitionWaitsForAnIncompatibleControlledOwner(string mode)
        {
            this.Test(() =>
            {
                using var rw = new ReaderWriterLockSlim();
                using var queued = new ManualResetEventSlim(false);
                using var releaseExternalOwner = new ManualResetEventSlim(false);
                ControlledReaderWriterLockSlim.SetWaiterQueuedCallbackForTesting(rw, queued.Set);

                EnterIncompatibleMode(rw, mode);
                bool releasedControlledOwner = false;
                int entered = 0;
                var thread = UncontrolledThreadRunner.Start(() =>
                {
                    EnterMode(rw, mode);
                    Interlocked.Exchange(ref entered, 1);
                    releaseExternalOwner.Wait();
                    ExitMode(rw, mode);
                });

                try
                {
                    WaitUntil(() => queued.IsSet || Volatile.Read(ref entered) != 0 || thread.IsCompleted);
                    Specification.Assert(queued.IsSet && Volatile.Read(ref entered) is 0,
                        "An uncontrolled {0} acquisition bypassed a conflicting controlled owner.", mode);

                    ExitIncompatibleMode(rw, mode);
                    releasedControlledOwner = true;
                    WaitUntil(() => Volatile.Read(ref entered) != 0 || thread.IsCompleted);
                    Specification.Assert(Volatile.Read(ref entered) is 1,
                        "The externally queued {0} acquisition did not proceed after the conflicting owner exited.", mode);
                }
                finally
                {
                    if (!releasedControlledOwner)
                    {
                        ExitIncompatibleMode(rw, mode);
                    }

                    releaseExternalOwner.Set();
                    thread.Join();
                }

                thread.ThrowIfFailed();
            }, this.GetConfiguration().WithPartiallyControlledConcurrencyAllowed().WithTestingIterations(1));
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestUncontrolledWriterOwnershipExcludesAControlledReader()
        {
            this.Test(() =>
            {
                using var rw = new ReaderWriterLockSlim();
                using var entered = new ManualResetEventSlim(false);
                using var releaseWriter = new ManualResetEventSlim(false);
                var thread = UncontrolledThreadRunner.Start(() =>
                {
                    rw.EnterWriteLock();
                    entered.Set();
                    releaseWriter.Wait();
                    rw.ExitWriteLock();
                });

                try
                {
                    WaitUntil(() => entered.IsSet || thread.IsCompleted);
                    Specification.Assert(entered.IsSet, "The uncontrolled writer did not enter the lock.");
                    bool controlledReaderEntered = rw.TryEnterReadLock(0);
                    if (controlledReaderEntered)
                    {
                        rw.ExitReadLock();
                    }

                    Specification.Assert(!controlledReaderEntered,
                        "A controlled reader entered while an uncontrolled writer owned the lock.");
                }
                finally
                {
                    releaseWriter.Set();
                    thread.Join();
                }

                thread.ThrowIfFailed();
            }, this.GetConfiguration().WithPartiallyControlledConcurrencyAllowed().WithTestingIterations(1));
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestQueuedWriterRetainsPriorityUntilItAcquires()
        {
            this.Test(() =>
            {
                using var rw = new ReaderWriterLockSlim();
                rw.EnterReadLock();
                int acquisitionOrder = 0;
                int writerOrder = 0;
                int readerOrder = 0;
                var writerQueued = new TaskCompletionSource<bool>();
                ControlledReaderWriterLockSlim.SetWaiterQueuedCallbackForTesting(
                    rw, () => writerQueued.TrySetResult(true));
                Task writer = Task.Run(() =>
                {
                    rw.EnterWriteLock();
                    writerOrder = Interlocked.Increment(ref acquisitionOrder);
                    rw.ExitWriteLock();
                });

                writerQueued.Task.Wait();
                rw.ExitReadLock();

                Task reader = Task.Run(() =>
                {
                    rw.EnterReadLock();
                    readerOrder = Interlocked.Increment(ref acquisitionOrder);
                    rw.ExitReadLock();
                });

                Task.WaitAll(writer, reader);
                Specification.Assert(writerOrder is 1 && readerOrder is 2,
                    "A reader entered after a queued writer was released but before that writer acquired the lock.");
            }, this.GetConfiguration().WithTestingIterations(100));
        }

        [Theory(Timeout = 10000)]
        [InlineData("CurrentReadCount")]
        [InlineData("RecursionPolicy")]
        [InlineData("WaitingReadCount")]
        [InlineData("WaitingUpgradeCount")]
        [InlineData("WaitingWriteCount")]
        [Trait("Category", "ReviewRemediation")]
        public void TestStaleLockStateGetterIsDetected(string property)
        {
            SharedStaleLock = null;
            try
            {
                this.TestWithError(() =>
                {
                    if (SharedStaleLock is null)
                    {
                        SharedStaleLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
                        return;
                    }

                    switch (property)
                    {
                        case "CurrentReadCount":
                            _ = SharedStaleLock.CurrentReadCount;
                            break;
                        case "RecursionPolicy":
                            _ = SharedStaleLock.RecursionPolicy;
                            break;
                        case "WaitingReadCount":
                            _ = SharedStaleLock.WaitingReadCount;
                            break;
                        case "WaitingUpgradeCount":
                            _ = SharedStaleLock.WaitingUpgradeCount;
                            break;
                        case "WaitingWriteCount":
                            _ = SharedStaleLock.WaitingWriteCount;
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(property));
                    }
                }, configuration: this.GetConfiguration().WithTestingIterations(2), errorChecker: error =>
                {
                    Assert.Contains("was created in a previous test iteration", error, StringComparison.Ordinal);
                });
            }
            finally
            {
                SharedStaleLock = null;
            }
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

        private static void EnterIncompatibleMode(ReaderWriterLockSlim rw, string mode)
        {
            if (mode is "Write")
            {
                rw.EnterReadLock();
            }
            else
            {
                rw.EnterWriteLock();
            }
        }

        private static void ExitIncompatibleMode(ReaderWriterLockSlim rw, string mode)
        {
            if (mode is "Write")
            {
                rw.ExitReadLock();
            }
            else
            {
                rw.ExitWriteLock();
            }
        }

        private static void EnterMode(ReaderWriterLockSlim rw, string mode)
        {
            switch (mode)
            {
                case "Read":
                    rw.EnterReadLock();
                    break;
                case "Write":
                    rw.EnterWriteLock();
                    break;
                case "UpgradeableRead":
                    rw.EnterUpgradeableReadLock();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }

        private static void ExitMode(ReaderWriterLockSlim rw, string mode)
        {
            switch (mode)
            {
                case "Read":
                    rw.ExitReadLock();
                    break;
                case "Write":
                    rw.ExitWriteLock();
                    break;
                case "UpgradeableRead":
                    rw.ExitUpgradeableReadLock();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }
    }
}

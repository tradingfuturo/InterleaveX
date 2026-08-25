// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Coyote.Runtime;
using SystemLockRecursionException = System.Threading.LockRecursionException;
using SystemLockRecursionPolicy = System.Threading.LockRecursionPolicy;
using SystemReaderWriterLockSlim = System.Threading.ReaderWriterLockSlim;
using SystemSynchronizationLockException = System.Threading.SynchronizationLockException;
using SystemThread = System.Threading.Thread;
using SystemTimeout = System.Threading.Timeout;

namespace Microsoft.Coyote.Rewriting.Types.Threading
{
    /// <summary>
    /// Provides methods for creating reader/writer locks that can be controlled during testing.
    /// </summary>
    /// <remarks>
    /// This type is intended for compiler use rather than use directly in code.
    /// </remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class ReaderWriterLockSlim
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ReaderWriterLockSlim"/> class with the
        /// default (no-recursion) policy.
        /// </summary>
        public static SystemReaderWriterLockSlim Create() => Create(SystemLockRecursionPolicy.NoRecursion);

        /// <summary>
        /// Initializes a new instance of the <see cref="ReaderWriterLockSlim"/> class, specifying
        /// the lock recursion policy.
        /// </summary>
        public static SystemReaderWriterLockSlim Create(SystemLockRecursionPolicy recursionPolicy)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving)
            {
                return new Wrapper(runtime, recursionPolicy);
            }

            return new SystemReaderWriterLockSlim(recursionPolicy);
        }

        /// <summary>
        /// Acquires the lock in read mode.
        /// </summary>
        public static void EnterReadLock(SystemReaderWriterLockSlim instance)
        {
            if (instance is Wrapper wrapper)
            {
                wrapper.EnterRead(SystemTimeout.Infinite);
                return;
            }

            instance.EnterReadLock();
        }

        /// <summary>
        /// Tries to acquire the lock in read mode, with an optional time-out.
        /// </summary>
        public static bool TryEnterReadLock(SystemReaderWriterLockSlim instance, int millisecondsTimeout) =>
            instance is Wrapper wrapper ? wrapper.EnterRead(millisecondsTimeout) : instance.TryEnterReadLock(millisecondsTimeout);

        /// <summary>
        /// Tries to acquire the lock in read mode, with an optional time-out.
        /// </summary>
        public static bool TryEnterReadLock(SystemReaderWriterLockSlim instance, TimeSpan timeout) =>
            TryEnterReadLock(instance, ToMilliseconds(timeout));

        /// <summary>
        /// Reduces the recursion count for read mode, and exits read mode if the count reaches zero.
        /// </summary>
        public static void ExitReadLock(SystemReaderWriterLockSlim instance)
        {
            if (instance is Wrapper wrapper)
            {
                wrapper.ExitRead();
                return;
            }

            instance.ExitReadLock();
        }

        /// <summary>
        /// Acquires the lock in write mode.
        /// </summary>
        public static void EnterWriteLock(SystemReaderWriterLockSlim instance)
        {
            if (instance is Wrapper wrapper)
            {
                wrapper.EnterWrite(SystemTimeout.Infinite);
                return;
            }

            instance.EnterWriteLock();
        }

        /// <summary>
        /// Tries to acquire the lock in write mode, with an optional time-out.
        /// </summary>
        public static bool TryEnterWriteLock(SystemReaderWriterLockSlim instance, int millisecondsTimeout) =>
            instance is Wrapper wrapper ? wrapper.EnterWrite(millisecondsTimeout) : instance.TryEnterWriteLock(millisecondsTimeout);

        /// <summary>
        /// Tries to acquire the lock in write mode, with an optional time-out.
        /// </summary>
        public static bool TryEnterWriteLock(SystemReaderWriterLockSlim instance, TimeSpan timeout) =>
            TryEnterWriteLock(instance, ToMilliseconds(timeout));

        /// <summary>
        /// Reduces the recursion count for write mode, and exits write mode if the count reaches zero.
        /// </summary>
        public static void ExitWriteLock(SystemReaderWriterLockSlim instance)
        {
            if (instance is Wrapper wrapper)
            {
                wrapper.ExitWrite();
                return;
            }

            instance.ExitWriteLock();
        }

        /// <summary>
        /// Acquires the lock in upgradeable-read mode (modelled as exclusive).
        /// </summary>
        public static void EnterUpgradeableReadLock(SystemReaderWriterLockSlim instance)
        {
            if (instance is Wrapper wrapper)
            {
                wrapper.EnterUpgradeableRead(SystemTimeout.Infinite);
                return;
            }

            instance.EnterUpgradeableReadLock();
        }

        /// <summary>
        /// Tries to acquire the lock in upgradeable-read mode, with an optional time-out.
        /// </summary>
        public static bool TryEnterUpgradeableReadLock(SystemReaderWriterLockSlim instance, int millisecondsTimeout) =>
            instance is Wrapper wrapper ? wrapper.EnterUpgradeableRead(millisecondsTimeout) :
                instance.TryEnterUpgradeableReadLock(millisecondsTimeout);

        /// <summary>
        /// Tries to acquire the lock in upgradeable-read mode, with an optional time-out.
        /// </summary>
        public static bool TryEnterUpgradeableReadLock(SystemReaderWriterLockSlim instance, TimeSpan timeout) =>
            TryEnterUpgradeableReadLock(instance, ToMilliseconds(timeout));

        /// <summary>
        /// Reduces the recursion count for upgradeable-read mode, and exits the mode if the count reaches zero.
        /// </summary>
        public static void ExitUpgradeableReadLock(SystemReaderWriterLockSlim instance)
        {
            if (instance is Wrapper wrapper)
            {
                wrapper.ExitUpgradeableRead();
                return;
            }

            instance.ExitUpgradeableReadLock();
        }

        /// <summary>
        /// Gets a value that indicates whether the current thread has entered the lock in read mode.
        /// </summary>
#pragma warning disable CA1707 // Identifiers should not contain underscores
#pragma warning disable SA1300 // Element should begin with upper-case letter
#pragma warning disable IDE1006 // Naming Styles
        public static bool get_IsReadLockHeld(SystemReaderWriterLockSlim instance) =>
            instance is Wrapper wrapper ? wrapper.IsCurrentReader() : instance.IsReadLockHeld;

        /// <summary>
        /// Gets a value that indicates whether the current thread has entered the lock in write mode.
        /// </summary>
        public static bool get_IsWriteLockHeld(SystemReaderWriterLockSlim instance) =>
            instance is Wrapper wrapper ? wrapper.IsCurrentWriter() : instance.IsWriteLockHeld;

        /// <summary>
        /// Gets a value that indicates whether the current thread has entered the lock in upgradeable-read mode.
        /// </summary>
        public static bool get_IsUpgradeableReadLockHeld(SystemReaderWriterLockSlim instance) =>
            instance is Wrapper wrapper ? wrapper.IsCurrentUpgradeableReader() : instance.IsUpgradeableReadLockHeld;

        /// <summary>
        /// Gets the total number of threads that have entered the lock in read mode.
        /// </summary>
        public static int get_CurrentReadCount(SystemReaderWriterLockSlim instance) =>
            instance is Wrapper wrapper ? wrapper.ReaderCount() : instance.CurrentReadCount;

        /// <summary>
        /// Gets the recursion policy of the lock.
        /// </summary>
        public static SystemLockRecursionPolicy get_RecursionPolicy(SystemReaderWriterLockSlim instance) =>
            instance is Wrapper wrapper ? wrapper.ModeledRecursionPolicy : instance.RecursionPolicy;

        /// <summary>
        /// Gets the current operation's read-lock recursion count.
        /// </summary>
        public static int get_RecursiveReadCount(SystemReaderWriterLockSlim instance) =>
            instance is Wrapper wrapper ? wrapper.ReadRecursionCount() : instance.RecursiveReadCount;

        /// <summary>
        /// Gets the current operation's upgradeable-read recursion count.
        /// </summary>
        public static int get_RecursiveUpgradeCount(SystemReaderWriterLockSlim instance) =>
            instance is Wrapper wrapper ? wrapper.UpgradeableRecursionCount() : instance.RecursiveUpgradeCount;

        /// <summary>
        /// Gets the current operation's write-lock recursion count.
        /// </summary>
        public static int get_RecursiveWriteCount(SystemReaderWriterLockSlim instance) =>
            instance is Wrapper wrapper ? wrapper.WriteRecursionCount() : instance.RecursiveWriteCount;

        /// <summary>
        /// Gets the number of operations waiting for a read lock.
        /// </summary>
        public static int get_WaitingReadCount(SystemReaderWriterLockSlim instance) =>
            instance is Wrapper wrapper ? wrapper.WaitingReaderCount() : instance.WaitingReadCount;

        /// <summary>
        /// Gets the number of operations waiting for an upgradeable-read lock.
        /// </summary>
        public static int get_WaitingUpgradeCount(SystemReaderWriterLockSlim instance) =>
            instance is Wrapper wrapper ? wrapper.WaitingUpgradeableReaderCount() : instance.WaitingUpgradeCount;

        /// <summary>
        /// Gets the number of operations waiting for a write lock.
        /// </summary>
        public static int get_WaitingWriteCount(SystemReaderWriterLockSlim instance) =>
            instance is Wrapper wrapper ? wrapper.WaitingWriterCount() : instance.WaitingWriteCount;
#pragma warning restore IDE1006 // Naming Styles
#pragma warning restore SA1300 // Element should begin with upper-case letter
#pragma warning restore CA1707 // Identifiers should not contain underscores

        /// <summary>
        /// Releases all resources used by the current instance.
        /// </summary>
        public static void Dispose(SystemReaderWriterLockSlim instance)
        {
            if (instance is Wrapper wrapper)
            {
                wrapper.DisposeControlled();
                return;
            }

            instance.Dispose();
        }

        private static int ToMilliseconds(TimeSpan timeout)
        {
            long totalMilliseconds = (long)timeout.TotalMilliseconds;
            if (totalMilliseconds < -1 || totalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            return (int)totalMilliseconds;
        }

        /// <summary>
        /// Wraps a <see cref="SystemReaderWriterLockSlim"/> so that it can be controlled during testing.
        /// </summary>
        private sealed class Wrapper : SystemReaderWriterLockSlim
        {
            private readonly Guid RuntimeId;
            private readonly Guid ResourceId;
            private readonly string DebugName;

            /// <summary>
            /// Operations currently holding the lock in read mode.
            /// </summary>
            private readonly HashSet<ControlledOperation> Readers;

            /// <summary>
            /// Recursion counts are per controlled operation because systematic scheduling can
            /// move multiple logical operations across the same physical thread.
            /// </summary>
            private readonly Dictionary<ControlledOperation, int> ReadRecursionCounts;
            private readonly Dictionary<ControlledOperation, int> WriteRecursionCounts;
            private readonly Dictionary<ControlledOperation, int> UpgradeableRecursionCounts;

            private readonly SystemLockRecursionPolicy LockRecursionPolicy;

            /// <summary>
            /// Operations paused waiting to acquire the read lock.
            /// </summary>
            private readonly Queue<ControlledOperation> PausedReaders;

            /// <summary>
            /// Operations paused waiting to acquire the write lock.
            /// </summary>
            private readonly Queue<ControlledOperation> PausedWriters;

            /// <summary>
            /// Operations paused waiting to acquire the upgradeable-read lock.
            /// </summary>
            private readonly Queue<ControlledOperation> PausedUpgradeableReaders;

            /// <summary>
            /// The operation currently holding the lock in write mode, if any.
            /// </summary>
            private ControlledOperation Writer;

            /// <summary>
            /// The operation currently holding the lock in upgradeable-read mode, if any.
            /// </summary>
            private ControlledOperation UpgradeableReader;

            private bool IsDisposed;

            internal Wrapper(CoyoteRuntime runtime, SystemLockRecursionPolicy recursionPolicy)
                : base(recursionPolicy)
            {
                this.RuntimeId = runtime.Id;
                this.ResourceId = Guid.NewGuid();
                this.DebugName = $"ReaderWriterLockSlim({this.ResourceId})";
                this.Readers = new HashSet<ControlledOperation>();
                this.ReadRecursionCounts = new Dictionary<ControlledOperation, int>();
                this.WriteRecursionCounts = new Dictionary<ControlledOperation, int>();
                this.UpgradeableRecursionCounts = new Dictionary<ControlledOperation, int>();
                this.LockRecursionPolicy = recursionPolicy;
                this.PausedReaders = new Queue<ControlledOperation>();
                this.PausedWriters = new Queue<ControlledOperation>();
                this.PausedUpgradeableReaders = new Queue<ControlledOperation>();
                this.Writer = null;
                this.UpgradeableReader = null;
            }

            /// <summary>
            /// Acquires the read lock, pausing while a writer holds it.
            /// </summary>
            internal bool EnterRead(int millisecondsTimeout)
            {
                ValidateMillisecondsTimeout(millisecondsTimeout);
                CoyoteRuntime runtime = this.GetRuntime();
                using (runtime.EnterSynchronizedSection())
                {
                    this.ThrowIfDisposed();
                    if (!runtime.TryGetExecutingOperation(out ControlledOperation current))
                    {
                        runtime.NotifyUncontrolledSynchronizationInvocation("ReaderWriterLockSlim.EnterReadLock");
                        return true;
                    }

                    if (this.ReadRecursionCounts.TryGetValue(current, out int readRecursionCount))
                    {
                        this.IncrementRecursion(this.ReadRecursionCounts, current, readRecursionCount,
                            "Recursive read lock acquisitions are not allowed in this mode.");
                        return true;
                    }

                    if (this.Writer == current && this.LockRecursionPolicy is SystemLockRecursionPolicy.NoRecursion)
                    {
                        throw new SystemLockRecursionException(
                            "A read lock may not be acquired with the write lock held in this mode.");
                    }

                    // An upgradeable owner may downgrade by taking a read lock even with
                    // NoRecursion. A recursive writer may also enter read mode.
                    if (this.Writer == current || this.UpgradeableReader == current)
                    {
                        this.Readers.Add(current);
                        this.ReadRecursionCounts[current] = 1;
                        return true;
                    }

                    if (runtime.Configuration.IsLockAccessRaceCheckingEnabled)
                    {
                        runtime.ScheduleNextOperation(current, SchedulingPointType.Acquire);
                    }

                    long deadline = millisecondsTimeout is SystemTimeout.Infinite ? 0 :
                        runtime.CreateVirtualDeadline(TimeSpan.FromMilliseconds(millisecondsTimeout));
                    while (this.Writer != null || this.PausedWriters.Count > 0)
                    {
                        if (millisecondsTimeout is 0)
                        {
                            return false;
                        }

                        runtime.LogWriter.LogDebug(
                            "[coyote::debug] Operation {0} is waiting to read-acquire '{1}' on thread '{2}'.",
                            current.DebugInfo, this.DebugName, SystemThread.CurrentThread.ManagedThreadId);
                        if (millisecondsTimeout is SystemTimeout.Infinite)
                        {
                            current.PauseWithResource(this.ResourceId);
                        }
                        else
                        {
                            current.PauseWithResourcesOrDelay(new[] { this.ResourceId }, deadline);
                        }

                        this.PausedReaders.Enqueue(current);
                        runtime.ScheduleNextOperation(current, SchedulingPointType.Pause);
                        if (current.WakeReason is OperationWakeReason.Deadline)
                        {
                            Remove(this.PausedReaders, current);
                            return false;
                        }
                    }

                    this.Readers.Add(current);
                    this.ReadRecursionCounts[current] = 1;
                    return true;
                }
            }

            /// <summary>
            /// Releases the read lock for the current operation.
            /// </summary>
            internal void ExitRead()
            {
                CoyoteRuntime runtime = this.GetRuntime();
                using (runtime.EnterSynchronizedSection())
                {
                    this.ThrowIfDisposed();
                    if (runtime.TryGetExecutingOperation(out ControlledOperation current))
                    {
                        if (!this.ReadRecursionCounts.TryGetValue(current, out int readRecursionCount))
                        {
                            throw new SystemSynchronizationLockException();
                        }

                        if (readRecursionCount > 1)
                        {
                            this.ReadRecursionCounts[current] = readRecursionCount - 1;
                            return;
                        }

                        this.ReadRecursionCounts.Remove(current);
                        this.Readers.Remove(current);
                    }
                    else
                    {
                        runtime.NotifyUncontrolledSynchronizationInvocation("ReaderWriterLockSlim.ExitReadLock");
                        return;
                    }

                    // A writer can only proceed once every reader has released.
                    if (this.Readers.Count is 0)
                    {
                        this.ReleaseWaiters();
                    }
                }
            }

            /// <summary>
            /// Acquires the write lock, pausing while any reader or another writer holds it.
            /// </summary>
            internal bool EnterWrite(int millisecondsTimeout)
            {
                ValidateMillisecondsTimeout(millisecondsTimeout);
                CoyoteRuntime runtime = this.GetRuntime();
                using (runtime.EnterSynchronizedSection())
                {
                    this.ThrowIfDisposed();
                    if (!runtime.TryGetExecutingOperation(out ControlledOperation current))
                    {
                        runtime.NotifyUncontrolledSynchronizationInvocation("ReaderWriterLockSlim.EnterWriteLock");
                        return true;
                    }

                    if (this.Writer == current)
                    {
                        this.IncrementRecursion(this.WriteRecursionCounts, current,
                            this.WriteRecursionCounts[current],
                            "Recursive write lock acquisitions are not allowed in this mode.");
                        return true;
                    }

                    // A reader that entered read mode first cannot upgrade, regardless of the
                    // recursion policy. An upgradeable owner, however, can upgrade even after
                    // taking an additional read lock to perform a downgrade.
                    if (this.Readers.Contains(current) && this.UpgradeableReader != current)
                    {
                        throw new SystemLockRecursionException(
                            "Write lock may not be acquired with read lock held.");
                    }

                    if (runtime.Configuration.IsLockAccessRaceCheckingEnabled)
                    {
                        runtime.ScheduleNextOperation(current, SchedulingPointType.Acquire);
                    }

                    long deadline = millisecondsTimeout is SystemTimeout.Infinite ? 0 :
                        runtime.CreateVirtualDeadline(TimeSpan.FromMilliseconds(millisecondsTimeout));
                    int otherReaderCount = this.Readers.Count - (this.Readers.Contains(current) ? 1 : 0);
                    while (this.Writer != null || otherReaderCount > 0 ||
                        (this.UpgradeableReader != null && this.UpgradeableReader != current))
                    {
                        if (millisecondsTimeout is 0)
                        {
                            return false;
                        }

                        runtime.LogWriter.LogDebug(
                            "[coyote::debug] Operation {0} is waiting to write-acquire '{1}' on thread '{2}'.",
                            current.DebugInfo, this.DebugName, SystemThread.CurrentThread.ManagedThreadId);
                        if (millisecondsTimeout is SystemTimeout.Infinite)
                        {
                            current.PauseWithResource(this.ResourceId);
                        }
                        else
                        {
                            current.PauseWithResourcesOrDelay(new[] { this.ResourceId }, deadline);
                        }

                        this.PausedWriters.Enqueue(current);
                        runtime.ScheduleNextOperation(current, SchedulingPointType.Pause);
                        if (current.WakeReason is OperationWakeReason.Deadline)
                        {
                            Remove(this.PausedWriters, current);
                            return false;
                        }

                        otherReaderCount = this.Readers.Count - (this.Readers.Contains(current) ? 1 : 0);
                    }

                    this.Writer = current;
                    this.WriteRecursionCounts[current] = 1;
                    return true;
                }
            }

            /// <summary>
            /// Releases the write lock for the current operation.
            /// </summary>
            internal void ExitWrite()
            {
                CoyoteRuntime runtime = this.GetRuntime();
                using (runtime.EnterSynchronizedSection())
                {
                    this.ThrowIfDisposed();
                    if (!runtime.TryGetExecutingOperation(out ControlledOperation current))
                    {
                        runtime.NotifyUncontrolledSynchronizationInvocation("ReaderWriterLockSlim.ExitWriteLock");
                        return;
                    }

                    if (this.Writer != current)
                    {
                        throw new SystemSynchronizationLockException();
                    }

                    int writeRecursionCount = this.WriteRecursionCounts[current];
                    if (writeRecursionCount > 1)
                    {
                        this.WriteRecursionCounts[current] = writeRecursionCount - 1;
                        return;
                    }

                    this.WriteRecursionCounts.Remove(current);
                    this.Writer = null;
                    this.ReleaseWaiters();
                }
            }

            /// <summary>
            /// Acquires the upgradeable-read lock, pausing while another upgradeable reader or writer holds it.
            /// </summary>
            internal bool EnterUpgradeableRead(int millisecondsTimeout)
            {
                ValidateMillisecondsTimeout(millisecondsTimeout);
                CoyoteRuntime runtime = this.GetRuntime();
                using (runtime.EnterSynchronizedSection())
                {
                    this.ThrowIfDisposed();
                    if (!runtime.TryGetExecutingOperation(out ControlledOperation current))
                    {
                        runtime.NotifyUncontrolledSynchronizationInvocation("ReaderWriterLockSlim.EnterUpgradeableReadLock");
                        return true;
                    }

                    if (this.UpgradeableReader == current)
                    {
                        this.IncrementRecursion(this.UpgradeableRecursionCounts, current,
                            this.UpgradeableRecursionCounts[current],
                            "Recursive upgradeable lock acquisitions are not allowed in this mode.");
                        return true;
                    }

                    // A writer that recursively acquires upgradeable mode is permitted with
                    // SupportsRecursion. A read-first owner is never allowed to upgrade.
                    if (this.Readers.Contains(current) && this.Writer != current)
                    {
                        throw new SystemLockRecursionException(
                            "Upgradeable lock may not be acquired with read lock held.");
                    }

                    if (this.Writer == current && this.LockRecursionPolicy is SystemLockRecursionPolicy.NoRecursion)
                    {
                        throw new SystemLockRecursionException(
                            "Upgradeable lock may not be acquired with write lock held in this mode.");
                    }

                    if (this.Writer == current)
                    {
                        this.UpgradeableReader = current;
                        this.UpgradeableRecursionCounts[current] = 1;
                        return true;
                    }

                    if (runtime.Configuration.IsLockAccessRaceCheckingEnabled)
                    {
                        runtime.ScheduleNextOperation(current, SchedulingPointType.Acquire);
                    }

                    long deadline = millisecondsTimeout is SystemTimeout.Infinite ? 0 :
                        runtime.CreateVirtualDeadline(TimeSpan.FromMilliseconds(millisecondsTimeout));
                    while (this.Writer != null || this.UpgradeableReader != null || this.PausedWriters.Count > 0)
                    {
                        if (millisecondsTimeout is 0)
                        {
                            return false;
                        }

                        runtime.LogWriter.LogDebug(
                            "[coyote::debug] Operation {0} is waiting to upgradeable-read-acquire '{1}' on thread '{2}'.",
                            current.DebugInfo, this.DebugName, SystemThread.CurrentThread.ManagedThreadId);
                        if (millisecondsTimeout is SystemTimeout.Infinite)
                        {
                            current.PauseWithResource(this.ResourceId);
                        }
                        else
                        {
                            current.PauseWithResourcesOrDelay(new[] { this.ResourceId }, deadline);
                        }

                        this.PausedUpgradeableReaders.Enqueue(current);
                        runtime.ScheduleNextOperation(current, SchedulingPointType.Pause);
                        if (current.WakeReason is OperationWakeReason.Deadline)
                        {
                            Remove(this.PausedUpgradeableReaders, current);
                            return false;
                        }
                    }

                    this.UpgradeableReader = current;
                    this.UpgradeableRecursionCounts[current] = 1;
                    return true;
                }
            }

            /// <summary>
            /// Releases the upgradeable-read lock for the current operation.
            /// </summary>
            internal void ExitUpgradeableRead()
            {
                CoyoteRuntime runtime = this.GetRuntime();
                using (runtime.EnterSynchronizedSection())
                {
                    this.ThrowIfDisposed();
                    if (!runtime.TryGetExecutingOperation(out ControlledOperation current))
                    {
                        runtime.NotifyUncontrolledSynchronizationInvocation("ReaderWriterLockSlim.ExitUpgradeableReadLock");
                        return;
                    }

                    if (this.UpgradeableReader != current)
                    {
                        throw new SystemSynchronizationLockException();
                    }

                    int upgradeableRecursionCount = this.UpgradeableRecursionCounts[current];
                    if (upgradeableRecursionCount > 1)
                    {
                        this.UpgradeableRecursionCounts[current] = upgradeableRecursionCount - 1;
                        return;
                    }

                    this.UpgradeableRecursionCounts.Remove(current);
                    this.UpgradeableReader = null;
                    this.ReleaseWaiters();
                }
            }

            internal SystemLockRecursionPolicy ModeledRecursionPolicy => this.LockRecursionPolicy;

            internal bool IsCurrentReader() =>
                this.GetRuntime().TryGetExecutingOperation(out ControlledOperation current) &&
                this.ReadRecursionCounts.ContainsKey(current);

            internal bool IsCurrentWriter() =>
                this.GetRuntime().TryGetExecutingOperation(out ControlledOperation current) &&
                this.WriteRecursionCounts.ContainsKey(current);

            internal bool IsCurrentUpgradeableReader() =>
                this.GetRuntime().TryGetExecutingOperation(out ControlledOperation current) &&
                this.UpgradeableRecursionCounts.ContainsKey(current);

            internal int ReaderCount() => this.Readers.Count;

            internal int ReadRecursionCount() => this.GetRecursionCount(this.ReadRecursionCounts);

            internal int WriteRecursionCount() => this.GetRecursionCount(this.WriteRecursionCounts);

            internal int UpgradeableRecursionCount() => this.GetRecursionCount(this.UpgradeableRecursionCounts);

            internal int WaitingReaderCount() => this.PausedReaders.Count;

            internal int WaitingWriterCount() => this.PausedWriters.Count;

            internal int WaitingUpgradeableReaderCount() => this.PausedUpgradeableReaders.Count;

            /// <summary>
            /// Disposes the model only when no operation owns a modeled lock mode. The BCL does
            /// not define concurrent use and disposal, so queued waiters are intentionally not
            /// used to strengthen that contract.
            /// </summary>
            internal void DisposeControlled()
            {
                CoyoteRuntime runtime = this.GetRuntime();
                using (runtime.EnterSynchronizedSection())
                {
                    if (this.IsDisposed)
                    {
                        return;
                    }

                    if (this.Readers.Count > 0 || this.Writer != null || this.UpgradeableReader != null)
                    {
                        throw new SystemSynchronizationLockException();
                    }

                    this.IsDisposed = true;
                    this.Dispose();
                }
            }

            /// <summary>
            /// Re-enables every paused waiter so the scheduler can pick the next holder; each
            /// re-checks its own acquisition condition on resume (the SemaphoreSlim pattern).
            /// </summary>
            private void ReleaseWaiters()
            {
                while (this.PausedWriters.Count > 0)
                {
                    this.PausedWriters.Dequeue().TryEnable(this.ResourceId);
                }

                while (this.PausedUpgradeableReaders.Count > 0)
                {
                    this.PausedUpgradeableReaders.Dequeue().TryEnable(this.ResourceId);
                }

                while (this.PausedReaders.Count > 0)
                {
                    this.PausedReaders.Dequeue().TryEnable(this.ResourceId);
                }
            }

            private static void ValidateMillisecondsTimeout(int millisecondsTimeout)
            {
                if (millisecondsTimeout < SystemTimeout.Infinite)
                {
                    throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));
                }
            }

            private static void Remove(Queue<ControlledOperation> queue, ControlledOperation operation)
            {
                int count = queue.Count;
                for (int idx = 0; idx < count; ++idx)
                {
                    ControlledOperation candidate = queue.Dequeue();
                    if (candidate != operation)
                    {
                        queue.Enqueue(candidate);
                    }
                }
            }

            private void IncrementRecursion(Dictionary<ControlledOperation, int> counts, ControlledOperation current,
                int count, string message)
            {
                if (this.LockRecursionPolicy is SystemLockRecursionPolicy.NoRecursion)
                {
                    throw new SystemLockRecursionException(message);
                }

                counts[current] = count + 1;
            }

            private int GetRecursionCount(Dictionary<ControlledOperation, int> counts)
            {
                return this.GetRuntime().TryGetExecutingOperation(out ControlledOperation current) &&
                    counts.TryGetValue(current, out int count) ? count : 0;
            }

            private void ThrowIfDisposed()
            {
                if (this.IsDisposed)
                {
                    throw new ObjectDisposedException(nameof(SystemReaderWriterLockSlim));
                }
            }

            private CoyoteRuntime GetRuntime()
            {
                var runtime = CoyoteRuntime.Current;
                if (runtime.Id != this.RuntimeId)
                {
                    var trace = new StackTrace();
                    runtime.NotifyAssertionFailure($"Accessing '{this.DebugName}' that was created in a " +
                        $"previous test iteration with runtime id '{this.RuntimeId}':\n{trace}");
                }

                return runtime;
            }
        }
    }
}

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
    /// This type is intended for compiler use rather than use directly in code. The model allows
    /// concurrent readers and an exclusive writer, scheduled by the Coyote runtime. It does not
    /// model lock recursion faithfully: recursive acquisition is treated as a fresh acquisition
    /// unless explicitly rejected to avoid impossible upgrade deadlocks.
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
#pragma warning restore IDE1006 // Naming Styles
#pragma warning restore SA1300 // Element should begin with upper-case letter
#pragma warning restore CA1707 // Identifiers should not contain underscores

        /// <summary>
        /// Releases all resources used by the current instance.
        /// </summary>
        public static void Dispose(SystemReaderWriterLockSlim instance) => instance.Dispose();

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

            internal Wrapper(CoyoteRuntime runtime, SystemLockRecursionPolicy recursionPolicy)
                : base(recursionPolicy)
            {
                this.RuntimeId = runtime.Id;
                this.ResourceId = Guid.NewGuid();
                this.DebugName = $"ReaderWriterLockSlim({this.ResourceId})";
                this.Readers = new HashSet<ControlledOperation>();
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
                    if (!runtime.TryGetExecutingOperation(out ControlledOperation current))
                    {
                        runtime.NotifyUncontrolledSynchronizationInvocation("ReaderWriterLockSlim.EnterReadLock");
                        return true;
                    }

                    if (this.Writer == current)
                    {
                        throw new SystemLockRecursionException(
                            "A read lock may not be acquired with the write lock held in this mode.");
                    }

                    if (runtime.Configuration.IsLockAccessRaceCheckingEnabled)
                    {
                        runtime.ScheduleNextOperation(current, SchedulingPointType.Acquire);
                    }

                    long deadline = millisecondsTimeout is SystemTimeout.Infinite ? 0 :
                        runtime.CreateVirtualDeadline(TimeSpan.FromMilliseconds(millisecondsTimeout));
                    while (this.Writer != null)
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
                    if (runtime.TryGetExecutingOperation(out ControlledOperation current))
                    {
                        if (!this.Readers.Remove(current))
                        {
                            throw new SystemSynchronizationLockException();
                        }
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
                    if (!runtime.TryGetExecutingOperation(out ControlledOperation current))
                    {
                        runtime.NotifyUncontrolledSynchronizationInvocation("ReaderWriterLockSlim.EnterWriteLock");
                        return true;
                    }

                    if (this.Readers.Contains(current))
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
                    while (this.Writer != null || this.Readers.Count > 0 ||
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
                    }

                    this.Writer = current;
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
                    if (!runtime.TryGetExecutingOperation(out ControlledOperation current))
                    {
                        runtime.NotifyUncontrolledSynchronizationInvocation("ReaderWriterLockSlim.ExitWriteLock");
                        return;
                    }

                    if (this.Writer != current)
                    {
                        throw new SystemSynchronizationLockException();
                    }

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
                    if (!runtime.TryGetExecutingOperation(out ControlledOperation current))
                    {
                        runtime.NotifyUncontrolledSynchronizationInvocation("ReaderWriterLockSlim.EnterUpgradeableReadLock");
                        return true;
                    }

                    if (this.Readers.Contains(current))
                    {
                        throw new SystemLockRecursionException(
                            "Upgradeable lock may not be acquired with read lock held.");
                    }

                    if (this.Writer == current)
                    {
                        throw new SystemLockRecursionException(
                            "Upgradeable lock may not be acquired with write lock held in this mode.");
                    }

                    if (this.UpgradeableReader == current)
                    {
                        throw new SystemLockRecursionException(
                            "Recursive upgradeable lock acquisitions not allowed in this mode.");
                    }

                    if (runtime.Configuration.IsLockAccessRaceCheckingEnabled)
                    {
                        runtime.ScheduleNextOperation(current, SchedulingPointType.Acquire);
                    }

                    long deadline = millisecondsTimeout is SystemTimeout.Infinite ? 0 :
                        runtime.CreateVirtualDeadline(TimeSpan.FromMilliseconds(millisecondsTimeout));
                    while (this.Writer != null || this.UpgradeableReader != null)
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
                    if (!runtime.TryGetExecutingOperation(out ControlledOperation current))
                    {
                        runtime.NotifyUncontrolledSynchronizationInvocation("ReaderWriterLockSlim.ExitUpgradeableReadLock");
                        return;
                    }

                    if (this.UpgradeableReader != current)
                    {
                        throw new SystemSynchronizationLockException();
                    }

                    this.UpgradeableReader = null;
                    this.ReleaseWaiters();
                }
            }

            internal bool IsCurrentReader() =>
                this.GetRuntime().TryGetExecutingOperation(out ControlledOperation current) && this.Readers.Contains(current);

            internal bool IsCurrentWriter() =>
                this.GetRuntime().TryGetExecutingOperation(out ControlledOperation current) && this.Writer == current;

            internal bool IsCurrentUpgradeableReader() =>
                this.GetRuntime().TryGetExecutingOperation(out ControlledOperation current) && this.UpgradeableReader == current;

            internal int ReaderCount() => this.Readers.Count;

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

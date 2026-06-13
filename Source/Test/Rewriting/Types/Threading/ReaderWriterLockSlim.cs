// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Coyote.Runtime;
using SystemLockRecursionPolicy = System.Threading.LockRecursionPolicy;
using SystemReaderWriterLockSlim = System.Threading.ReaderWriterLockSlim;
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
    /// model lock recursion or the upgradeable-read role faithfully: recursive acquisition is
    /// treated as a fresh acquisition (a thread that recursively takes the write lock therefore
    /// deadlocks, which the runtime reports), and an upgradeable read lock is modelled as an
    /// exclusive (write-style) acquisition — stricter than the BCL, so it never produces a false
    /// data race, though it does not explore reader concurrency alongside an upgradeable holder.
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
                wrapper.EnterWrite(SystemTimeout.Infinite);
                return;
            }

            instance.EnterUpgradeableReadLock();
        }

        /// <summary>
        /// Tries to acquire the lock in upgradeable-read mode, with an optional time-out.
        /// </summary>
        public static bool TryEnterUpgradeableReadLock(SystemReaderWriterLockSlim instance, int millisecondsTimeout) =>
            instance is Wrapper wrapper ? wrapper.EnterWrite(millisecondsTimeout) : instance.TryEnterUpgradeableReadLock(millisecondsTimeout);

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
                wrapper.ExitWrite();
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
            /// The operation currently holding the lock in write mode, if any.
            /// </summary>
            private ControlledOperation Writer;

            internal Wrapper(CoyoteRuntime runtime, SystemLockRecursionPolicy recursionPolicy)
                : base(recursionPolicy)
            {
                this.RuntimeId = runtime.Id;
                this.ResourceId = Guid.NewGuid();
                this.DebugName = $"ReaderWriterLockSlim({this.ResourceId})";
                this.Readers = new HashSet<ControlledOperation>();
                this.PausedReaders = new Queue<ControlledOperation>();
                this.PausedWriters = new Queue<ControlledOperation>();
                this.Writer = null;
            }

            /// <summary>
            /// Acquires the read lock, pausing while a writer holds it.
            /// </summary>
            internal bool EnterRead(int millisecondsTimeout)
            {
                CoyoteRuntime runtime = this.GetRuntime();
                using (runtime.EnterSynchronizedSection())
                {
                    if (!runtime.TryGetExecutingOperation(out ControlledOperation current))
                    {
                        runtime.NotifyUncontrolledSynchronizationInvocation("ReaderWriterLockSlim.EnterReadLock");
                        return true;
                    }

                    if (runtime.Configuration.IsLockAccessRaceCheckingEnabled)
                    {
                        runtime.ScheduleNextOperation(current, SchedulingPointType.Acquire);
                    }

                    while (this.Writer != null)
                    {
                        if (millisecondsTimeout is 0)
                        {
                            return false;
                        }

                        runtime.LogWriter.LogDebug(
                            "[coyote::debug] Operation {0} is waiting to read-acquire '{1}' on thread '{2}'.",
                            current.DebugInfo, this.DebugName, SystemThread.CurrentThread.ManagedThreadId);
                        current.PauseWithResource(this.ResourceId);
                        this.PausedReaders.Enqueue(current);
                        runtime.ScheduleNextOperation(current, SchedulingPointType.Pause);
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
                        this.Readers.Remove(current);
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
                CoyoteRuntime runtime = this.GetRuntime();
                using (runtime.EnterSynchronizedSection())
                {
                    if (!runtime.TryGetExecutingOperation(out ControlledOperation current))
                    {
                        runtime.NotifyUncontrolledSynchronizationInvocation("ReaderWriterLockSlim.EnterWriteLock");
                        return true;
                    }

                    if (runtime.Configuration.IsLockAccessRaceCheckingEnabled)
                    {
                        runtime.ScheduleNextOperation(current, SchedulingPointType.Acquire);
                    }

                    while (this.Writer != null || this.Readers.Count > 0)
                    {
                        if (millisecondsTimeout is 0)
                        {
                            return false;
                        }

                        runtime.LogWriter.LogDebug(
                            "[coyote::debug] Operation {0} is waiting to write-acquire '{1}' on thread '{2}'.",
                            current.DebugInfo, this.DebugName, SystemThread.CurrentThread.ManagedThreadId);
                        current.PauseWithResource(this.ResourceId);
                        this.PausedWriters.Enqueue(current);
                        runtime.ScheduleNextOperation(current, SchedulingPointType.Pause);
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
                    this.Writer = null;
                    this.ReleaseWaiters();
                }
            }

            internal bool IsCurrentReader() =>
                this.GetRuntime().TryGetExecutingOperation(out ControlledOperation current) && this.Readers.Contains(current);

            internal bool IsCurrentWriter() =>
                this.GetRuntime().TryGetExecutingOperation(out ControlledOperation current) && this.Writer == current;

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

                while (this.PausedReaders.Count > 0)
                {
                    this.PausedReaders.Dequeue().TryEnable(this.ResourceId);
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

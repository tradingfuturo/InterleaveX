// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Runtime.CompilerServices;
using SystemCancellationToken = System.Threading.CancellationToken;
using SystemCancellationTokenRegistration = System.Threading.CancellationTokenRegistration;
using SystemSemaphoreSlim = System.Threading.SemaphoreSlim;
using SystemTask = System.Threading.Tasks.Task;
using SystemTaskCreationOptions = System.Threading.Tasks.TaskCreationOptions;
using SystemTasks = System.Threading.Tasks;
using SystemThread = System.Threading.Thread;
using SystemThreading = System.Threading;
using SystemTimeout = System.Threading.Timeout;
using SystemWaitHandle = System.Threading.WaitHandle;

namespace Microsoft.Coyote.Rewriting.Types.Threading
{
    /// <summary>
    /// Provides methods for creating semaphores that can be controlled during testing.
    /// </summary>
    /// <remarks>This type is intended for compiler use rather than use directly in code.</remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class SemaphoreSlim
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SemaphoreSlim"/> class, specifying
        /// the initial number of requests that can be granted concurrently.
        /// </summary>
        public static SystemSemaphoreSlim Create(int initialCount) => Create(initialCount, int.MaxValue);

        /// <summary>
        /// Initializes a new instance of the <see cref="SemaphoreSlim"/> class, specifying
        /// the initial and maximum number of requests that can be granted concurrently.
        /// </summary>
        public static SystemSemaphoreSlim Create(int initialCount, int maxCount)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving)
            {
                return new Wrapper(runtime, initialCount, maxCount);
            }

            return new SystemSemaphoreSlim(initialCount, maxCount);
        }

        /// <summary>
        /// Returns a <see cref="SystemWaitHandle"/> that can be used to wait on the semaphore.
        /// </summary>
#pragma warning disable CA1707 // Identifiers should not contain underscores
#pragma warning disable SA1300 // Element should begin with upper-case letter
#pragma warning disable IDE1006 // Naming Styles
        public static SystemWaitHandle get_AvailableWaitHandle(SystemSemaphoreSlim instance)
#pragma warning restore IDE1006 // Naming Styles
#pragma warning restore SA1300 // Element should begin with upper-case letter
#pragma warning restore CA1707 // Identifiers should not contain underscores
        {
            if (instance is Wrapper wrapper)
            {
                var runtime = CoyoteRuntime.Current;
                if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving)
                {
                    runtime.NotifyAssertionFailure("Invoking 'SemaphoreSlim.AvailableWaitHandle' is not supported in systematic testing.");
                }
            }

            return instance.AvailableWaitHandle;
        }

        /// <summary>
        /// Gets the number of remaining threads that can enter the <see cref="SystemSemaphoreSlim"/> object.
        /// </summary>
#pragma warning disable CA1707 // Identifiers should not contain underscores
#pragma warning disable SA1300 // Element should begin with upper-case letter
#pragma warning disable IDE1006 // Naming Styles
        public static int get_CurrentCount(SystemSemaphoreSlim instance)
#pragma warning restore IDE1006 // Naming Styles
#pragma warning restore SA1300 // Element should begin with upper-case letter
#pragma warning restore CA1707 // Identifiers should not contain underscores
        {
            if (instance is Wrapper wrapper)
            {
                return wrapper.LockCount;
            }

            return instance.CurrentCount;
        }

        /// <summary>
        /// Blocks the current task until it can enter the semaphore.
        /// </summary>
        public static void Wait(SystemSemaphoreSlim instance) =>
            Wait(instance, SystemTimeout.Infinite, SystemCancellationToken.None);

        /// <summary>
        /// Blocks the current task until it can enter the semaphore, using a <see cref="TimeSpan"/>
        /// that specifies the timeout.
        /// </summary>
        public static bool Wait(SystemSemaphoreSlim instance, TimeSpan timeout) =>
            Wait(instance, timeout, SystemCancellationToken.None);

        /// <summary>
        /// Blocks the current task until it can enter the semaphore, using a 32-bit signed integer
        /// that specifies the timeout.
        /// </summary>
        public static bool Wait(SystemSemaphoreSlim instance, int millisecondsTimeout) =>
            Wait(instance, millisecondsTimeout, SystemCancellationToken.None);

        /// <summary>
        /// Blocks the current task until it can enter the semaphore, while observing a cancellation token.
        /// </summary>
        public static void Wait(SystemSemaphoreSlim instance, SystemCancellationToken cancellationToken) =>
            Wait(instance, SystemTimeout.Infinite, cancellationToken);

        /// <summary>
        /// Blocks the current task until it can enter the semaphore, using a <see cref="TimeSpan"/>
        /// that specifies the timeout, while observing a cancellation token.
        /// </summary>
        public static bool Wait(SystemSemaphoreSlim instance, TimeSpan timeout, SystemCancellationToken cancellationToken)
        {
            long totalMilliseconds = (long)timeout.TotalMilliseconds;
            if (totalMilliseconds < -1 || totalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            return Wait(instance, (int)totalMilliseconds, cancellationToken);
        }

        /// <summary>
        /// Blocks the current task until it can enter the semaphore, using a 32-bit signed integer
        /// that specifies the timeout, while observing a cancellation token.
        /// </summary>
        public static bool Wait(SystemSemaphoreSlim instance, int millisecondsTimeout, SystemCancellationToken cancellationToken)
        {
            if (millisecondsTimeout < SystemTimeout.Infinite)
            {
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (instance is Wrapper wrapper)
            {
                return wrapper.Enter(millisecondsTimeout, cancellationToken);
            }

            var runtime = CoyoteRuntime.Current;
            if (runtime.Configuration.IsLockAccessRaceCheckingEnabled &&
                runtime.SchedulingPolicy is SchedulingPolicy.Fuzzing &&
                runtime.TryGetExecutingOperation(out ControlledOperation current))
            {
                runtime.DelayOperation(current);
            }

            return instance.Wait(millisecondsTimeout, cancellationToken);
        }

        /// <summary>
        /// Asynchronously waits to enter the semaphore.
        /// </summary>
        public static SystemTask WaitAsync(SystemSemaphoreSlim instance) => WaitAsync(instance, SystemTimeout.Infinite, default);

        /// <summary>
        /// Asynchronously waits to enter the semaphore, while observing a cancellation token.
        /// </summary>
        public static SystemTask WaitAsync(SystemSemaphoreSlim instance, SystemCancellationToken cancellationToken) =>
            WaitAsync(instance, SystemTimeout.Infinite, cancellationToken);

        /// <summary>
        /// Asynchronously waits to enter the semaphore, using a 32-bit signed integer
        /// that specifies the timeout.
        /// </summary>
        public static SystemTasks.Task<bool> WaitAsync(SystemSemaphoreSlim instance, int millisecondsTimeout) =>
            WaitAsync(instance, millisecondsTimeout, default);

        /// <summary>
        /// Asynchronously waits to enter the semaphore, using a <see cref="TimeSpan"/> that specifies the timeout.
        /// </summary>
        public static SystemTasks.Task<bool> WaitAsync(SystemSemaphoreSlim instance, TimeSpan timeout) =>
            WaitAsync(instance, timeout, default);

        /// <summary>
        /// Asynchronously waits to enter the semaphore, using a <see cref="TimeSpan"/>
        /// that specifies the timeout, while observing a cancellation token.
        /// </summary>
        public static SystemTasks.Task<bool> WaitAsync(SystemSemaphoreSlim instance, TimeSpan timeout, SystemCancellationToken cancellationToken)
        {
            long totalMilliseconds = (long)timeout.TotalMilliseconds;
            if (totalMilliseconds < -1 || totalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            return WaitAsync(instance, (int)totalMilliseconds, cancellationToken);
        }

        /// <summary>
        /// Asynchronously waits to enter the semaphore, using a 32-bit signed integer
        /// that specifies the timeout, while observing a cancellation token.
        /// </summary>
        public static SystemTasks.Task<bool> WaitAsync(SystemSemaphoreSlim instance, int millisecondsTimeout, SystemCancellationToken cancellationToken)
        {
            if (millisecondsTimeout < SystemTimeout.Infinite)
            {
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Tasks.Task.FromCanceled<bool>(cancellationToken);
            }

            if (instance is Wrapper wrapper)
            {
                return wrapper.EnterAsync(millisecondsTimeout, cancellationToken);
            }

            var runtime = CoyoteRuntime.Current;
            if (runtime.Configuration.IsLockAccessRaceCheckingEnabled &&
                runtime.SchedulingPolicy is SchedulingPolicy.Fuzzing &&
                runtime.TryGetExecutingOperation(out ControlledOperation current))
            {
                runtime.DelayOperation(current);
            }

            return instance.WaitAsync(millisecondsTimeout, cancellationToken);
        }

        /// <summary>
        /// Releases the <see cref="SemaphoreSlim"/> object once.
        /// </summary>
        public static int Release(SystemSemaphoreSlim instance) =>
            instance is Wrapper wrapper ? wrapper.Exit(1) : instance.Release();

        /// <summary>
        /// Releases the <see cref="SemaphoreSlim"/> object a specified number of times.
        /// </summary>
        public static int Release(SystemSemaphoreSlim instance, int releaseCount) =>
            instance is Wrapper wrapper ? wrapper.Exit(releaseCount) : instance.Release(releaseCount);

        /// <summary>
        /// Disposes the <see cref="SemaphoreSlim"/> object.
        /// </summary>
        public static void Dispose(SystemSemaphoreSlim instance)
        {
            if (instance is Wrapper wrapper)
            {
                wrapper.DisposeControlled();
                return;
            }

            instance.Dispose();
        }

        /// <summary>
        /// Installs an instance-scoped callback that is invoked after a controlled waiter is queued.
        /// Used by regression tests to deterministically exercise cancellation after parking.
        /// </summary>
        internal static void SetWaiterQueuedCallbackForTesting(SystemSemaphoreSlim instance, Action callback)
        {
            if (instance is Wrapper wrapper)
            {
                wrapper.WaiterQueuedCallback = callback;
            }
        }

        /// <summary>
        /// Wraps a <see cref="SystemSemaphoreSlim"/> so that it can be controlled during testing.
        /// </summary>
        private class Wrapper : SystemSemaphoreSlim
        {
            /// <summary>
            /// The id of the <see cref="CoyoteRuntime"/> that created this semaphore.
            /// </summary>
            private readonly Guid RuntimeId;

            /// <summary>
            /// The resource id of this semaphore.
            /// </summary>
            private readonly Guid ResourceId;

            /// <summary>
            /// Queue of operations waiting to be released.
            /// </summary>
            private readonly Queue<ControlledOperation> PausedOperations;

            /// <summary>
            /// Synchronous waiters whose cancellation callback won before a release woke them.
            /// </summary>
            private readonly HashSet<ControlledOperation> CancelledOperations;

            /// <summary>
            /// Queue of completion sources that operations are asynchronously awaiting to get released.
            /// </summary>
            private readonly Queue<SystemTasks.TaskCompletionSource<bool>> AsyncAwaiters;

            private readonly Dictionary<SystemTasks.TaskCompletionSource<bool>, SystemCancellationTokenRegistration>
                AsyncCancellationRegistrations;

            private bool IsDisposed;

            /// <summary>
            /// Optional instance-scoped regression-test callback invoked after a waiter is queued.
            /// </summary>
            internal Action WaiterQueuedCallback;

            /// <summary>
            /// The maximum semaphore value.
            /// </summary>
            private readonly int MaxCount;

            /// <summary>
            /// The semaphore lock count.
            /// </summary>
            internal int LockCount { get; private set; }

            /// <summary>
            /// The debug name of this semaphore.
            /// </summary>
            private readonly string DebugName;

            /// <summary>
            /// Initializes a new instance of the <see cref="Wrapper"/> class.
            /// </summary>
            internal Wrapper(CoyoteRuntime runtime, int initialCount, int maxCount)
                : base(initialCount, maxCount)
            {
                this.RuntimeId = runtime.Id;
                this.ResourceId = Guid.NewGuid();
                this.PausedOperations = new Queue<ControlledOperation>();
                this.CancelledOperations = new HashSet<ControlledOperation>();
                this.AsyncAwaiters = new Queue<SystemTasks.TaskCompletionSource<bool>>();
                this.AsyncCancellationRegistrations =
                    new Dictionary<SystemTasks.TaskCompletionSource<bool>, SystemCancellationTokenRegistration>();
                this.LockCount = initialCount;
                this.MaxCount = maxCount;
                this.DebugName = $"SemaphoreSlim({this.ResourceId})";
            }

            /// <summary>
            /// Pauses the current operation until it can enter the semaphore.
            /// </summary>
            internal bool Enter(int millisecondsTimeout, SystemCancellationToken cancellationToken)
            {
                CoyoteRuntime runtime = this.GetRuntime();
                ControlledOperation current = null;
                SystemCancellationTokenRegistration registration = default;
                bool hasRegistration = false;
                try
                {
                    using (runtime.EnterSynchronizedSection())
                    {
                        this.ThrowIfDisposed();
                        if (!runtime.TryGetExecutingOperation(out current))
                        {
                            runtime.NotifyUncontrolledSynchronizationInvocation("SemaphoreSlim.Wait");
                        }
                        else if (runtime.Configuration.IsLockAccessRaceCheckingEnabled)
                        {
                            runtime.ScheduleNextOperation(current, SchedulingPointType.Acquire);
                        }

                        long deadline = millisecondsTimeout is SystemTimeout.Infinite ? 0 :
                            runtime.CreateVirtualDeadline(TimeSpan.FromMilliseconds(millisecondsTimeout));
                        while (true)
                        {
                            if (this.CancelledOperations.Remove(current))
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                            }

                            if (this.LockCount > 0)
                            {
                                this.LockCount--;
                                return true;
                            }

                            cancellationToken.ThrowIfCancellationRequested();
                            if (millisecondsTimeout is 0)
                            {
                                return false;
                            }

                            if (cancellationToken.CanBeCanceled && !hasRegistration)
                            {
                                hasRegistration = true;
                                registration = cancellationToken.Register(() => this.EnableCancelledWaiter(runtime, current));
                                cancellationToken.ThrowIfCancellationRequested();
                            }

                            runtime.LogWriter.LogDebug(
                                "[coyote::debug] Operation {0} is waiting for '{1}' to get released on thread '{2}'.",
                                current.DebugInfo, this.DebugName, SystemThread.CurrentThread.ManagedThreadId);
                            if (millisecondsTimeout is SystemTimeout.Infinite)
                            {
                                current.PauseWithResource(this.ResourceId);
                            }
                            else
                            {
                                current.PauseWithResourcesOrDelay(new[] { this.ResourceId }, deadline);
                            }

                            this.PausedOperations.Enqueue(current);
                            this.WaiterQueuedCallback?.Invoke();
                            runtime.ScheduleNextOperation(current, SchedulingPointType.Pause);
                            if (current.WakeReason is OperationWakeReason.Deadline)
                            {
                                return false;
                            }
                        }
                    }
                }
                finally
                {
                    using (runtime.EnterSynchronizedSection())
                    {
                        if (current != null)
                        {
                            this.RemovePausedOperation(current);
                            this.CancelledOperations.Remove(current);
                        }
                    }

                    registration.Dispose();
                }
            }

            /// <summary>
            /// Pauses the current operation asynchronously until it can enter the semaphore.
            /// </summary>
            internal SystemTasks.Task<bool> EnterAsync(int millisecondsTimeout,
                SystemCancellationToken cancellationToken)
            {
                CoyoteRuntime runtime = this.GetRuntime();
                using (runtime.EnterSynchronizedSection())
                {
                    this.ThrowIfDisposed();
                    if (!runtime.TryGetExecutingOperation(out ControlledOperation current))
                    {
                        runtime.NotifyUncontrolledSynchronizationInvocation("SemaphoreSlim.WaitAsync");
                    }
                    else if (runtime.Configuration.IsLockAccessRaceCheckingEnabled)
                    {
                        runtime.ScheduleNextOperation(current, SchedulingPointType.Acquire);
                    }

                    if (this.LockCount is 0)
                    {
                        if (millisecondsTimeout is 0)
                        {
                            return Tasks.Task.FromResult(false);
                        }

                        if (millisecondsTimeout is SystemTimeout.Infinite)
                        {
                            var tcs = new SystemTasks.TaskCompletionSource<bool>(
                                SystemTaskCreationOptions.RunContinuationsAsynchronously);
                            this.AsyncAwaiters.Enqueue(tcs);
                            this.AsyncCancellationRegistrations.Add(tcs, default);
                            if (cancellationToken.CanBeCanceled)
                            {
                                SystemCancellationTokenRegistration registration = cancellationToken.Register(
                                    () => this.CancelAsyncWaiter(runtime, tcs, cancellationToken));
                                if (this.AsyncCancellationRegistrations.ContainsKey(tcs))
                                {
                                    this.AsyncCancellationRegistrations[tcs] = registration;
                                }
                                else
                                {
                                    registration.Dispose();
                                }
                            }

                            this.WaiterQueuedCallback?.Invoke();
                            runtime.RegisterKnownControlledTask(tcs.Task);
                            return AsyncTaskAwaiterStateMachine<bool>.RunAsync(runtime, tcs.Task, true);
                        }

                        SystemTasks.Task<bool> task = runtime.TaskFactory.StartNew(
                            () => this.Enter(millisecondsTimeout, cancellationToken),
                            cancellationToken,
                            runtime.TaskFactory.CreationOptions,
                            runtime.TaskFactory.Scheduler);
                        runtime.RegisterKnownControlledTask(task);
                        return task;
                    }

                    this.LockCount--;
                    return Tasks.Task.FromResult(true);
                }
            }

            private void RemovePausedOperation(ControlledOperation operation)
            {
                int count = this.PausedOperations.Count;
                for (int idx = 0; idx < count; ++idx)
                {
                    ControlledOperation candidate = this.PausedOperations.Dequeue();
                    if (candidate != operation)
                    {
                        this.PausedOperations.Enqueue(candidate);
                    }
                }
            }

            private static void Remove(Queue<SystemTasks.TaskCompletionSource<bool>> queue,
                SystemTasks.TaskCompletionSource<bool> waiter)
            {
                int count = queue.Count;
                for (int idx = 0; idx < count; ++idx)
                {
                    SystemTasks.TaskCompletionSource<bool> candidate = queue.Dequeue();
                    if (candidate != waiter)
                    {
                        queue.Enqueue(candidate);
                    }
                }
            }

            /// <summary>
            /// Exits the semaphore a specified number of times.
            /// </summary>
            internal int Exit(int releaseCount)
            {
                CoyoteRuntime runtime = this.GetRuntime();
                var registrationsToDispose = new List<SystemCancellationTokenRegistration>();
                int previousCount;
                using (runtime.EnterSynchronizedSection())
                {
                    this.ThrowIfDisposed();
                    if (!runtime.TryGetExecutingOperation(out ControlledOperation current))
                    {
                        runtime.NotifyUncontrolledSynchronizationInvocation("SemaphoreSlim.Release");
                    }

                    if (releaseCount < 1)
                    {
                        throw new ArgumentOutOfRangeException(nameof(releaseCount));
                    }

                    // If the release count would result exceeding the maximum count, throw an exception.
                    if (releaseCount > this.MaxCount - this.LockCount)
                    {
                        throw new SystemThreading.SemaphoreFullException();
                    }

                    previousCount = this.LockCount;
                    int lockCount = previousCount + releaseCount;
                    int remainingReleaseCount = releaseCount;

                    // Release asynchronous awaiters first. Completion reserves exactly one token;
                    // cancellation and release race under the same runtime synchronization lock.
                    while (remainingReleaseCount > 0 && this.AsyncAwaiters.Count > 0)
                    {
                        var tcs = this.AsyncAwaiters.Dequeue();
                        if (this.AsyncCancellationRegistrations.TryGetValue(tcs, out SystemCancellationTokenRegistration registration))
                        {
                            this.AsyncCancellationRegistrations.Remove(tcs);
                            registrationsToDispose.Add(registration);
                            if (tcs.TrySetResult(true))
                            {
                                lockCount--;
                                remainingReleaseCount--;
                            }
                        }
                    }

                    // Wake synchronous waiters without reserving a token for a specific operation.
                    // SemaphoreSlim permits a waiter or a concurrent barging Wait to acquire the
                    // released count; the awakened waiter rechecks LockCount under the runtime lock.
                    int synchronousWakeCount = remainingReleaseCount;
                    while (synchronousWakeCount > 0 && this.PausedOperations.Count > 0)
                    {
                        ControlledOperation operation = this.PausedOperations.Dequeue();
                        if (operation.Status is not (OperationStatus.PausedOnAllResources or
                            OperationStatus.PausedOnResourceOrDelay))
                        {
                            // Cancellation or the absolute deadline already won and made this
                            // waiter runnable. Do not let a later release overwrite that outcome.
                            continue;
                        }

                        if (operation.TryEnable(this.ResourceId))
                        {
                            synchronousWakeCount--;
                        }
                    }

                    this.LockCount = lockCount;
                }

                foreach (SystemCancellationTokenRegistration registration in registrationsToDispose)
                {
                    registration.Dispose();
                }

                return previousCount;
            }

            /// <summary>
            /// Marks a synchronous waiter runnable when cancellation wins its wait race.
            /// </summary>
            private void EnableCancelledWaiter(CoyoteRuntime runtime, ControlledOperation operation)
            {
                using (runtime.EnterSynchronizedSection())
                {
                    this.RemovePausedOperation(operation);
                    if (operation.TryEnable(this.ResourceId))
                    {
                        this.CancelledOperations.Add(operation);
                    }
                }
            }

            /// <summary>
            /// Cancels an asynchronous waiter if it has not already been completed by a release.
            /// </summary>
            private void CancelAsyncWaiter(CoyoteRuntime runtime, SystemTasks.TaskCompletionSource<bool> waiter,
                SystemCancellationToken cancellationToken)
            {
                using (runtime.EnterSynchronizedSection())
                {
                    if (this.AsyncCancellationRegistrations.Remove(waiter))
                    {
                        Remove(this.AsyncAwaiters, waiter);
                        waiter.TrySetCanceled(cancellationToken);
                    }
                }
            }

            /// <summary>
            /// Disposes the controlled semaphore and preserves the BCL post-disposal contract.
            /// </summary>
            internal void DisposeControlled()
            {
                CoyoteRuntime runtime = this.GetRuntime();
                using (runtime.EnterSynchronizedSection())
                {
                    if (!this.IsDisposed)
                    {
                        this.IsDisposed = true;
                        this.Dispose();
                    }
                }
            }

            private void ThrowIfDisposed()
            {
                if (this.IsDisposed)
                {
                    throw new ObjectDisposedException(nameof(SystemSemaphoreSlim));
                }
            }

            /// <summary>
            /// Returns the current runtime, asserting that it is the same runtime that created this resource.
            /// </summary>
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

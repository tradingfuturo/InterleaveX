// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using Microsoft.Coyote.Rewriting.Types.Runtime.CompilerServices;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Runtime.CompilerServices;
using MethodImpl = System.Runtime.CompilerServices.MethodImplAttribute;
using MethodImplOptions = System.Runtime.CompilerServices.MethodImplOptions;
using SystemCancellationToken = System.Threading.CancellationToken;
using SystemCancellationTokenSource = System.Threading.CancellationTokenSource;
using SystemTask = System.Threading.Tasks.Task;
using SystemTaskContinuationOptions = System.Threading.Tasks.TaskContinuationOptions;
using SystemTaskCreationOptions = System.Threading.Tasks.TaskCreationOptions;
using SystemTaskFactory = System.Threading.Tasks.TaskFactory;
using SystemTasks = System.Threading.Tasks;
using SystemTaskScheduler = System.Threading.Tasks.TaskScheduler;
using SystemTimeout = System.Threading.Timeout;

namespace Microsoft.Coyote.Rewriting.Types.Threading.Tasks
{
    /// <summary>
    /// Provides methods for creating tasks that can be controlled during testing.
    /// </summary>
    /// <remarks>This type is intended for compiler use rather than use directly in code.</remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class Task
    {
        /// <summary>
        /// The maximum supported task delay in milliseconds.
        /// </summary>
        private const long MaxSupportedTimeoutMilliseconds = uint.MaxValue - 1;

        /// <summary>
        /// Gets a task that has already completed successfully.
        /// </summary>
        public static SystemTask CompletedTask { get; } = SystemTask.CompletedTask;

        /// <summary>
        /// The default task factory.
        /// </summary>
        private static SystemTaskFactory DefaultFactory = new SystemTaskFactory();

        /// <summary>
        /// Provides access to factory methods for creating controlled task and generic task instances.
        /// </summary>
        public static SystemTaskFactory Factory
        {
            get
            {
                var runtime = CoyoteRuntime.Current;
                if (runtime.SchedulingPolicy is SchedulingPolicy.None)
                {
                    return DefaultFactory;
                }

                return runtime.TaskFactory;
            }
        }

#pragma warning disable CA1068 // CancellationToken parameters must come last
        /// <summary>
        /// Creates a new task instance with the specified action.
        /// </summary>
        public static SystemTask Create(Action action) => new SystemTask(action);

        /// <summary>
        /// Creates a new task instance with the specified action and cancellation token.
        /// </summary>
        public static SystemTask Create(Action action, SystemCancellationToken cancellationToken) =>
            new SystemTask(action, cancellationToken);

        /// <summary>
        /// Creates a new task instance with the specified action and creation options.
        /// </summary>
        public static SystemTask Create(Action action, SystemTaskCreationOptions creationOptions) =>
            new SystemTask(action, creationOptions);

        /// <summary>
        /// Creates a new task instance with the specified action, cancellation token, and creation options.
        /// </summary>
        public static SystemTask Create(Action action, SystemCancellationToken cancellationToken,
            SystemTaskCreationOptions creationOptions) =>
            new SystemTask(action, cancellationToken, creationOptions);

        /// <summary>
        /// Creates a new task instance with the specified action and state.
        /// </summary>
        public static SystemTask Create(Action<object> action, object state) =>
            new SystemTask(action, state);

        /// <summary>
        /// Creates a new task instance with the specified action, state, and cancellation token.
        /// </summary>
        public static SystemTask Create(Action<object> action, object state,
            SystemCancellationToken cancellationToken) =>
            new SystemTask(action, state, cancellationToken);

        /// <summary>
        /// Creates a new task instance with the specified action, state, and creation options.
        /// </summary>
        public static SystemTask Create(Action<object> action, object state,
            SystemTaskCreationOptions creationOptions) =>
            new SystemTask(action, state, creationOptions);

        /// <summary>
        /// Creates a new task instance with the specified action, state, cancellation token, and creation options.
        /// </summary>
        public static SystemTask Create(Action<object> action, object state,
            SystemCancellationToken cancellationToken, SystemTaskCreationOptions creationOptions) =>
            new SystemTask(action, state, cancellationToken, creationOptions);
#pragma warning restore CA1068 // CancellationToken parameters must come last

        /// <summary>
        /// Starts the specified task, scheduling it for execution to the current <see cref="SystemTaskScheduler"/>.
        /// </summary>
        public static void Start(SystemTask task)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                task.Start();
                return;
            }

            // Start the task on the controlled task scheduler, which will queue
            // it to the runtime for controlled execution.
            task.Start(runtime.ControlledTaskScheduler);
        }

        /// <summary>
        /// Starts the specified task, scheduling it for execution to the specified <see cref="SystemTaskScheduler"/>.
        /// </summary>
        public static void Start(SystemTask task, SystemTaskScheduler scheduler)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None ||
                scheduler.GetType() != SystemTaskScheduler.Default.GetType())
            {
                task.Start(scheduler);
                return;
            }

            // Start the task on the controlled task scheduler, which will queue
            // it to the runtime for controlled execution.
            task.Start(runtime.ControlledTaskScheduler);
        }

        /// <summary>
        /// Runs the specified task synchronously on the current <see cref="SystemTaskScheduler"/>.
        /// </summary>
        public static void RunSynchronously(SystemTask task)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                task.RunSynchronously();
                return;
            }

            // Run the task synchronously on the controlled task scheduler.
            task.RunSynchronously(runtime.ControlledTaskScheduler);
        }

        /// <summary>
        /// Runs the specified task synchronously on the specified <see cref="SystemTaskScheduler"/>.
        /// </summary>
        public static void RunSynchronously(SystemTask task, SystemTaskScheduler scheduler)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None ||
                scheduler.GetType() != SystemTaskScheduler.Default.GetType())
            {
                task.RunSynchronously(scheduler);
                return;
            }

            // Run the task synchronously on the controlled task scheduler.
            task.RunSynchronously(runtime.ControlledTaskScheduler);
        }

        /// <summary>
        /// Queues the specified work to run on the thread pool and returns a task object that
        /// represents that work. A cancellation token allows the work to be cancelled.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SystemTask Run(Action action) => Run(action, default);

        /// <summary>
        /// Queues the specified work to run on the thread pool and returns a task
        /// object that represents that work.
        /// </summary>
        public static SystemTask Run(Action action, SystemCancellationToken cancellationToken)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return SystemTask.Run(action, cancellationToken);
            }

            var taskFactory = runtime.TaskFactory;
            return taskFactory.StartNew(action, cancellationToken,
                taskFactory.CreationOptions | SystemTaskCreationOptions.DenyChildAttach,
                taskFactory.Scheduler);
        }

        /// <summary>
        /// Queues the specified work to run on the thread pool and returns a task
        /// object that represents that work.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SystemTasks.Task<TResult> Run<TResult>(Func<TResult> function) => Run(function, default);

        /// <summary>
        /// Queues the specified work to run on the thread pool and returns a task object that
        /// represents that work. A cancellation token allows the work to be cancelled.
        /// </summary>
        public static SystemTasks.Task<TResult> Run<TResult>(Func<TResult> function,
            SystemCancellationToken cancellationToken)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return SystemTask.Run(function, cancellationToken);
            }

            var taskFactory = runtime.TaskFactory;
            return taskFactory.StartNew(function, cancellationToken,
                taskFactory.CreationOptions | SystemTaskCreationOptions.DenyChildAttach,
                taskFactory.Scheduler);
        }

        /// <summary>
        /// Queues the specified work to run on the thread pool and returns a proxy for
        /// the task returned by the function.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SystemTask Run(Func<SystemTask> function) => Run(function, default);

        /// <summary>
        /// Queues the specified work to run on the thread pool and returns a proxy for the task
        /// returned by the function. A cancellation token allows the work to be cancelled.
        /// </summary>
        public static SystemTask Run(Func<SystemTask> function,
            SystemCancellationToken cancellationToken)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return SystemTask.Run(function, cancellationToken);
            }

            var taskFactory = runtime.TaskFactory;
            return taskFactory.StartNew(function, cancellationToken,
                taskFactory.CreationOptions | SystemTaskCreationOptions.DenyChildAttach,
                taskFactory.Scheduler).Unwrap();
        }

        /// <summary>
        /// Queues the specified work to run on the thread pool and returns a proxy for the
        /// generic task returned by the function.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SystemTasks.Task<TResult> Run<TResult>(Func<SystemTasks.Task<TResult>> function) =>
            Run(function, default);

        /// <summary>
        /// Queues the specified work to run on the thread pool and returns a proxy for the generic
        /// task returned by the function. A cancellation token allows the work to be cancelled.
        /// </summary>
        public static SystemTasks.Task<TResult> Run<TResult>(Func<SystemTasks.Task<TResult>> function,
            SystemCancellationToken cancellationToken)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return SystemTask.Run(function, cancellationToken);
            }

            var taskFactory = runtime.TaskFactory;
            return taskFactory.StartNew(function, cancellationToken,
                taskFactory.CreationOptions | SystemTaskCreationOptions.DenyChildAttach,
                taskFactory.Scheduler).Unwrap();
        }

        /// <summary>
        /// Creates a task that completes after a time delay.
        /// </summary>
        public static SystemTask Delay(int millisecondsDelay)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return SystemTask.Delay(millisecondsDelay);
            }

            ValidateDelay(millisecondsDelay);
            return runtime.ScheduleDelay(TimeSpan.FromMilliseconds(millisecondsDelay), default);
        }

        /// <summary>
        /// Creates a task that completes after a time delay.
        /// </summary>
        public static SystemTask Delay(int millisecondsDelay, SystemCancellationToken cancellationToken)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return SystemTask.Delay(millisecondsDelay, cancellationToken);
            }

            ValidateDelay(millisecondsDelay);
            return runtime.ScheduleDelay(TimeSpan.FromMilliseconds(millisecondsDelay), cancellationToken);
        }

        /// <summary>
        /// Creates a task that completes after a specified time interval.
        /// </summary>
        public static SystemTask Delay(TimeSpan delay)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return SystemTask.Delay(delay);
            }

            delay = CoyoteRuntime.NormalizeTimeout(delay, nameof(delay), MaxSupportedTimeoutMilliseconds);
            return runtime.ScheduleDelay(delay, default);
        }

        /// <summary>
        /// Creates a task that completes after a specified time interval.
        /// </summary>
        public static SystemTask Delay(TimeSpan delay, SystemCancellationToken cancellationToken)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return SystemTask.Delay(delay, cancellationToken);
            }

            delay = CoyoteRuntime.NormalizeTimeout(delay, nameof(delay), MaxSupportedTimeoutMilliseconds);
            return runtime.ScheduleDelay(delay, cancellationToken);
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// Creates a task that completes after a specified time interval according to the specified time provider.
        /// </summary>
        public static SystemTask Delay(TimeSpan delay, TimeProvider timeProvider) =>
            Delay(delay, timeProvider, default);

        /// <summary>
        /// Creates a cancellable task that completes after a specified time interval according to the specified
        /// time provider.
        /// </summary>
        public static SystemTask Delay(TimeSpan delay, TimeProvider timeProvider,
            SystemCancellationToken cancellationToken)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return SystemTask.Delay(delay, timeProvider, cancellationToken);
            }

            ArgumentNullException.ThrowIfNull(timeProvider);
            delay = CoyoteRuntime.NormalizeTimeout(delay, nameof(delay), MaxSupportedTimeoutMilliseconds);
            return global::Microsoft.Coyote.Rewriting.Types.Threading.RuntimeTimeProvider.Delay(
                runtime, delay, timeProvider, cancellationToken);
        }
#endif

        /// <summary>
        /// Validates the specified delay in milliseconds.
        /// </summary>
        private static void ValidateDelay(int millisecondsDelay)
        {
            if (millisecondsDelay < SystemTimeout.Infinite)
            {
                throw new ArgumentOutOfRangeException(nameof(millisecondsDelay));
            }
        }

        /// <summary>
        /// Creates a task that will complete when all tasks in the specified array have completed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SystemTask WhenAll(params SystemTask[] tasks)
        {
            SystemTask task = SystemTask.WhenAll(tasks);
            CoyoteRuntime.Current.RegisterKnownControlledTask(task);
            return task;
        }

        /// <summary>
        /// Creates a task that will complete when all tasks in the specified enumerable collection have completed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SystemTask WhenAll(IEnumerable<SystemTask> tasks)
        {
            SystemTask task = SystemTask.WhenAll(tasks);
            CoyoteRuntime.Current.RegisterKnownControlledTask(task);
            return task;
        }

        /// <summary>
        /// Creates a task that will complete when all tasks in the specified array have completed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SystemTasks.Task<TResult[]> WhenAll<TResult>(params SystemTasks.Task<TResult>[] tasks)
        {
            SystemTasks.Task<TResult[]> task = SystemTask.WhenAll(tasks);
            CoyoteRuntime.Current.RegisterKnownControlledTask(task);
            return task;
        }

        /// <summary>
        /// Creates a task that will complete when all tasks in the specified enumerable collection have completed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SystemTasks.Task<TResult[]> WhenAll<TResult>(IEnumerable<SystemTasks.Task<TResult>> tasks)
        {
            SystemTasks.Task<TResult[]> task = SystemTask.WhenAll(tasks);
            CoyoteRuntime.Current.RegisterKnownControlledTask(task);
            return task;
        }

#if NET9_0_OR_GREATER
        /// <summary>
        /// Creates a task that will complete when all tasks in the specified span have completed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SystemTask WhenAll(ReadOnlySpan<SystemTask> tasks)
        {
            SystemTask task = SystemTask.WhenAll(tasks);
            CoyoteRuntime.Current.RegisterKnownControlledTask(task);
            return task;
        }

        /// <summary>
        /// Creates a task that will complete when all tasks in the specified span have completed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SystemTasks.Task<TResult[]> WhenAll<TResult>(ReadOnlySpan<SystemTasks.Task<TResult>> tasks)
        {
            SystemTasks.Task<TResult[]> task = SystemTask.WhenAll(tasks);
            CoyoteRuntime.Current.RegisterKnownControlledTask(task);
            return task;
        }
#endif

        /// <summary>
        /// Creates a task that will complete when any task in the specified array have completed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SystemTasks.Task<SystemTask> WhenAny(params SystemTask[] tasks)
        {
            SystemTasks.Task<SystemTask> task = SystemTask.WhenAny(tasks);
            CoyoteRuntime.Current.RegisterKnownControlledTask(task);
            return task;
        }

        /// <summary>
        /// Creates a task that will complete when any task in the specified enumerable collection have completed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SystemTasks.Task<SystemTask> WhenAny(IEnumerable<SystemTask> tasks)
        {
            SystemTasks.Task<SystemTask> task = SystemTask.WhenAny(tasks);
            CoyoteRuntime.Current.RegisterKnownControlledTask(task);
            return task;
        }

#if NET
        /// <summary>
        /// Creates a task that will complete when either of the two tasks have completed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SystemTasks.Task<SystemTask> WhenAny(SystemTask task1, SystemTask task2)
        {
            SystemTasks.Task<SystemTask> task = SystemTask.WhenAny(task1, task2);
            CoyoteRuntime.Current.RegisterKnownControlledTask(task);
            return task;
        }

        /// <summary>
        /// Creates a task that will complete when either of the two tasks have completed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SystemTasks.Task<SystemTasks.Task<TResult>> WhenAny<TResult>(
            SystemTasks.Task<TResult> task1, SystemTasks.Task<TResult> task2)
        {
            SystemTasks.Task<SystemTasks.Task<TResult>> task = SystemTask.WhenAny(task1, task2);
            CoyoteRuntime.Current.RegisterKnownControlledTask(task);
            return task;
        }
#endif

        /// <summary>
        /// Creates a task that will complete when any task in the specified array have completed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SystemTasks.Task<SystemTasks.Task<TResult>> WhenAny<TResult>(
            params SystemTasks.Task<TResult>[] tasks)
        {
            SystemTasks.Task<SystemTasks.Task<TResult>> task = SystemTask.WhenAny(tasks);
            CoyoteRuntime.Current.RegisterKnownControlledTask(task);
            return task;
        }

        /// <summary>
        /// Creates a task that will complete when any task in the specified
        /// enumerable collection have completed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SystemTasks.Task<SystemTasks.Task<TResult>> WhenAny<TResult>(
            IEnumerable<SystemTasks.Task<TResult>> tasks)
        {
            SystemTasks.Task<SystemTasks.Task<TResult>> task = SystemTask.WhenAny(tasks);
            CoyoteRuntime.Current.RegisterKnownControlledTask(task);
            return task;
        }

#if NET9_0_OR_GREATER
        /// <summary>
        /// Creates a task that will complete when any task in the specified span has completed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SystemTasks.Task<SystemTask> WhenAny(ReadOnlySpan<SystemTask> tasks)
        {
            SystemTasks.Task<SystemTask> task = SystemTask.WhenAny(tasks);
            CoyoteRuntime.Current.RegisterKnownControlledTask(task);
            return task;
        }

        /// <summary>
        /// Creates a task that will complete when any task in the specified span has completed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SystemTasks.Task<SystemTasks.Task<TResult>> WhenAny<TResult>(
            ReadOnlySpan<SystemTasks.Task<TResult>> tasks)
        {
            SystemTasks.Task<SystemTasks.Task<TResult>> task = SystemTask.WhenAny(tasks);
            CoyoteRuntime.Current.RegisterKnownControlledTask(task);
            return task;
        }
#endif

#if NET9_0_OR_GREATER
        /// <summary>
        /// Creates an <see cref="IAsyncEnumerable{T}"/> that will yield the supplied tasks
        /// as those tasks complete.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IAsyncEnumerable<SystemTask> WhenEach(params SystemTask[] tasks)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return SystemTask.WhenEach(tasks);
            }

            return new ControlledWhenEachEnumerable<SystemTask>(runtime, (IEnumerable<SystemTask>)tasks);
        }

        /// <summary>
        /// Creates an <see cref="IAsyncEnumerable{T}"/> that will yield the supplied tasks
        /// as those tasks complete.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IAsyncEnumerable<SystemTask> WhenEach(IEnumerable<SystemTask> tasks)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return SystemTask.WhenEach(tasks);
            }

            return new ControlledWhenEachEnumerable<SystemTask>(runtime, tasks);
        }

        /// <summary>
        /// Creates an <see cref="IAsyncEnumerable{T}"/> that will yield the supplied tasks
        /// as those tasks complete.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IAsyncEnumerable<SystemTask> WhenEach(ReadOnlySpan<SystemTask> tasks)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return SystemTask.WhenEach(tasks);
            }

            return new ControlledWhenEachEnumerable<SystemTask>(runtime, tasks);
        }

        /// <summary>
        /// Creates an <see cref="IAsyncEnumerable{T}"/> that will yield the supplied tasks
        /// as those tasks complete.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IAsyncEnumerable<SystemTasks.Task<TResult>> WhenEach<TResult>(
            params SystemTasks.Task<TResult>[] tasks)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return SystemTask.WhenEach(tasks);
            }

            return new ControlledWhenEachEnumerable<SystemTasks.Task<TResult>>(runtime,
                (IEnumerable<SystemTasks.Task<TResult>>)tasks);
        }

        /// <summary>
        /// Creates an <see cref="IAsyncEnumerable{T}"/> that will yield the supplied tasks
        /// as those tasks complete.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IAsyncEnumerable<SystemTasks.Task<TResult>> WhenEach<TResult>(
            IEnumerable<SystemTasks.Task<TResult>> tasks)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return SystemTask.WhenEach(tasks);
            }

            return new ControlledWhenEachEnumerable<SystemTasks.Task<TResult>>(runtime, tasks);
        }

        /// <summary>
        /// Creates an <see cref="IAsyncEnumerable{T}"/> that will yield the supplied tasks
        /// as those tasks complete.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IAsyncEnumerable<SystemTasks.Task<TResult>> WhenEach<TResult>(
            ReadOnlySpan<SystemTasks.Task<TResult>> tasks)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return SystemTask.WhenEach(tasks);
            }

            return new ControlledWhenEachEnumerable<SystemTasks.Task<TResult>>(runtime, tasks);
        }
#endif

        /// <summary>
        /// Waits for all of the provided task objects to complete execution.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WaitAll(params SystemTask[] tasks) =>
            WaitAll(tasks, SystemTimeout.Infinite, default);

#if NET9_0_OR_GREATER
        /// <summary>
        /// Waits for all of the provided task objects in the specified span to complete execution.
        /// </summary>
        public static void WaitAll(ReadOnlySpan<SystemTask> tasks)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy != SchedulingPolicy.None)
            {
                TaskServices.WaitUntilAllTasksComplete(runtime, tasks.ToArray());
            }

            SystemTask.WaitAll(tasks);
        }
#endif

        /// <summary>
        /// Waits for all of the provided task objects to complete execution
        /// within a specified time interval.
        /// </summary>
        public static bool WaitAll(SystemTask[] tasks, TimeSpan timeout)
        {
            long totalMilliseconds = (long)timeout.TotalMilliseconds;
            if (totalMilliseconds < -1 || totalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            return WaitAll(tasks, (int)totalMilliseconds, default);
        }

        /// <summary>
        /// Waits for all of the provided task objects to complete execution within
        /// a specified number of milliseconds.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool WaitAll(SystemTask[] tasks, int millisecondsTimeout) =>
            WaitAll(tasks, millisecondsTimeout, default);

        /// <summary>
        /// Waits for all of the provided task objects to complete execution unless the wait is cancelled.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WaitAll(SystemTask[] tasks, SystemCancellationToken cancellationToken) =>
            WaitAll(tasks, SystemTimeout.Infinite, cancellationToken);

        /// <summary>
        /// Waits for any of the provided task objects to complete execution within a specified
        /// number of milliseconds or until a cancellation token is cancelled.
        /// </summary>
        public static bool WaitAll(SystemTask[] tasks, int millisecondsTimeout,
            SystemCancellationToken cancellationToken)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving && tasks != null &&
                millisecondsTimeout != SystemTimeout.Infinite)
            {
                if (millisecondsTimeout < SystemTimeout.Infinite)
                {
                    throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));
                }

                ValidateTaskArray(tasks, requireAtLeastOne: false);
                cancellationToken.ThrowIfCancellationRequested();
                bool completed = Array.TrueForAll(tasks, task => task.IsCompleted);
                if (!completed && millisecondsTimeout > 0)
                {
                    long deadline = runtime.CreateVirtualDeadline(
                        TimeSpan.FromMilliseconds(millisecondsTimeout));
                    completed = runtime.PauseOperationUntilDeadline(null,
                        () => Array.TrueForAll(tasks, task => task.IsCompleted), deadline,
                        debugMsg: "all tasks to complete", cancellationToken: cancellationToken);
                }

                return completed && SystemTask.WaitAll(tasks, 0, cancellationToken);
            }

            if (runtime.SchedulingPolicy != SchedulingPolicy.None && tasks != null)
            {
                TaskServices.WaitUntilAllTasksComplete(runtime, tasks);
            }

            return SystemTask.WaitAll(tasks, millisecondsTimeout, cancellationToken);
        }

        /// <summary>
        /// Waits for any of the provided task objects to complete execution.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int WaitAny(params SystemTask[] tasks) =>
            WaitAny(tasks, SystemTimeout.Infinite, default);

        /// <summary>
        /// Waits for any of the provided task objects to complete execution within a specified time interval.
        /// </summary>
        public static int WaitAny(SystemTask[] tasks, TimeSpan timeout)
        {
            long totalMilliseconds = (long)timeout.TotalMilliseconds;
            if (totalMilliseconds < -1 || totalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            return WaitAny(tasks, (int)totalMilliseconds, default);
        }

        /// <summary>
        /// Waits for any of the provided task objects to complete execution within
        /// a specified number of milliseconds.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int WaitAny(SystemTask[] tasks, int millisecondsTimeout) =>
            WaitAny(tasks, millisecondsTimeout, default);

        /// <summary>
        /// Waits for any of the provided task objects to complete execution unless the wait is cancelled.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int WaitAny(SystemTask[] tasks, SystemCancellationToken cancellationToken) =>
            WaitAny(tasks, SystemTimeout.Infinite, cancellationToken);

        /// <summary>
        /// Waits for any of the provided task objects to complete execution within a specified
        /// number of milliseconds or until a cancellation token is cancelled.
        /// </summary>
        public static int WaitAny(SystemTask[] tasks, int millisecondsTimeout,
            SystemCancellationToken cancellationToken)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving && tasks != null &&
                millisecondsTimeout != SystemTimeout.Infinite)
            {
                if (millisecondsTimeout < SystemTimeout.Infinite)
                {
                    throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));
                }

                ValidateTaskArray(tasks, requireAtLeastOne: true);
                cancellationToken.ThrowIfCancellationRequested();
                int index = Array.FindIndex(tasks, task => task.IsCompleted);
                if (index < 0 && millisecondsTimeout > 0)
                {
                    long deadline = runtime.CreateVirtualDeadline(
                        TimeSpan.FromMilliseconds(millisecondsTimeout));
                    _ = runtime.PauseOperationUntilDeadline(null,
                        () => Array.Exists(tasks, task => task.IsCompleted), deadline,
                        debugMsg: "any task to complete", cancellationToken: cancellationToken);
                    index = Array.FindIndex(tasks, task => task.IsCompleted);
                }

                if (index >= 0)
                {
                    _ = SystemTask.WaitAny(tasks, 0, cancellationToken);
                }

                return index;
            }

            if (runtime.SchedulingPolicy != SchedulingPolicy.None && tasks != null)
            {
                TaskServices.WaitUntilAnyTaskCompletes(runtime, tasks);
            }

            return SystemTask.WaitAny(tasks, millisecondsTimeout, cancellationToken);
        }

        /// <summary>
        /// Performs the eager task-array validation that the finite virtual-time branches otherwise
        /// bypass when they inspect completion state directly.
        /// </summary>
        private static void ValidateTaskArray(SystemTask[] tasks, bool requireAtLeastOne)
        {
            if (tasks is null)
            {
                throw new ArgumentNullException(nameof(tasks));
            }

            if (requireAtLeastOne && tasks.Length is 0)
            {
                throw new ArgumentException("The tasks array must contain at least one task.", nameof(tasks));
            }

            for (int idx = 0; idx < tasks.Length; ++idx)
            {
                if (tasks[idx] is null)
                {
                    throw new ArgumentException("The tasks array contains a null task.", nameof(tasks));
                }
            }
        }

        /// <summary>
        /// Waits for the specified task to complete execution.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Wait(SystemTask task) => Wait(task, SystemTimeout.Infinite, default);

        /// <summary>
        /// Waits for the specified task to complete execution within a specified time interval.
        /// </summary>
        public static bool Wait(SystemTask task, TimeSpan timeout)
        {
            long totalMilliseconds = (long)timeout.TotalMilliseconds;
            if (totalMilliseconds < -1 || totalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            return Wait(task, (int)totalMilliseconds, default);
        }

        /// <summary>
        /// Waits for the specified task to complete execution within a specified number of milliseconds.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Wait(SystemTask task, int millisecondsTimeout) =>
            Wait(task, millisecondsTimeout, default);

        /// <summary>
        /// Waits for the specified task to complete execution. The wait terminates if a cancellation
        /// token is canceled before the task completes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Wait(SystemTask task, SystemCancellationToken cancellationToken) =>
            Wait(task, SystemTimeout.Infinite, cancellationToken);

        /// <summary>
        /// Waits for the specified task to complete execution. The wait terminates if a timeout interval
        /// elapses or a cancellation token is canceled before the task completes.
        /// </summary>
        public static bool Wait(SystemTask task, int millisecondsTimeout,
            SystemCancellationToken cancellationToken)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                millisecondsTimeout != SystemTimeout.Infinite)
            {
                if (millisecondsTimeout < SystemTimeout.Infinite)
                {
                    throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));
                }

                cancellationToken.ThrowIfCancellationRequested();
                bool completed = task.IsCompleted;
                if (!completed && millisecondsTimeout > 0)
                {
                    long deadline = runtime.CreateVirtualDeadline(
                        TimeSpan.FromMilliseconds(millisecondsTimeout));
                    completed = runtime.PauseOperationUntilDeadline(null, () => task.IsCompleted,
                        deadline, debugMsg: $"task '{task.Id}' to complete",
                        cancellationToken: cancellationToken);
                }

                return completed && task.Wait(0, cancellationToken);
            }

            if (runtime.SchedulingPolicy != SchedulingPolicy.None)
            {
                TaskServices.WaitUntilTaskCompletes(runtime, task);
            }

            return task.Wait(millisecondsTimeout, cancellationToken);
        }

#if NET6_0_OR_GREATER
        /// <summary>
        /// Gets a task that completes when the specified task completes or cancellation is requested.
        /// </summary>
        public static SystemTask WaitAsync(SystemTask task, SystemCancellationToken cancellationToken)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return task.WaitAsync(cancellationToken);
            }

            if (task.IsCompleted || !cancellationToken.CanBeCanceled)
            {
                return task;
            }

            return WaitAsyncCore(task, SystemTimeout.InfiniteTimeSpan, static () => true, cancellationToken);
        }

        /// <summary>
        /// Gets a task that completes when the specified task completes or the timeout expires.
        /// </summary>
        public static SystemTask WaitAsync(SystemTask task, TimeSpan timeout)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return task.WaitAsync(timeout);
            }

            TimeSpan normalized = NormalizeWaitAsyncTimeout(timeout);
            if (task.IsCompleted || normalized == SystemTimeout.InfiniteTimeSpan)
            {
                return task;
            }

            return WaitAsyncCore(task, normalized, static () => true, default);
        }

        /// <summary>
        /// Gets a task that completes when the specified task completes, the timeout expires or cancellation is requested.
        /// </summary>
        public static SystemTask WaitAsync(SystemTask task, TimeSpan timeout,
            SystemCancellationToken cancellationToken)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return task.WaitAsync(timeout, cancellationToken);
            }

            TimeSpan normalized = NormalizeWaitAsyncTimeout(timeout);
            if (task.IsCompleted || (!cancellationToken.CanBeCanceled && normalized == SystemTimeout.InfiniteTimeSpan))
            {
                return task;
            }

            return WaitAsyncCore(task, normalized, static () => true, cancellationToken);
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// Gets a task that completes when the specified task completes or the timeout expires according
        /// to the specified time provider.
        /// </summary>
        public static SystemTask WaitAsync(SystemTask task, TimeSpan timeout, TimeProvider timeProvider)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return task.WaitAsync(timeout, timeProvider);
            }

            ArgumentNullException.ThrowIfNull(timeProvider);
            TimeSpan normalized = NormalizeWaitAsyncTimeout(timeout);
            if (task.IsCompleted || normalized == SystemTimeout.InfiniteTimeSpan)
            {
                return task;
            }

            return WaitAsyncCore(task, normalized, static () => true, default, timeProvider);
        }

        /// <summary>
        /// Gets a task that completes when the specified task completes, the timeout expires according
        /// to the specified time provider or cancellation is requested.
        /// </summary>
        public static SystemTask WaitAsync(SystemTask task, TimeSpan timeout, TimeProvider timeProvider,
            SystemCancellationToken cancellationToken)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return task.WaitAsync(timeout, timeProvider, cancellationToken);
            }

            ArgumentNullException.ThrowIfNull(timeProvider);
            TimeSpan normalized = NormalizeWaitAsyncTimeout(timeout);
            if (task.IsCompleted || (!cancellationToken.CanBeCanceled && normalized == SystemTimeout.InfiniteTimeSpan))
            {
                return task;
            }

            return WaitAsyncCore(task, normalized, static () => true, cancellationToken, timeProvider);
        }
#endif

        /// <summary>
        /// Implements the controlled task, timeout and cancellation race shared by generic and
        /// non-generic task waits. Timeout ownership is selected by the overload that reached this core.
        /// </summary>
        internal static SystemTasks.Task<TResult> WaitAsyncCore<TResult>(SystemTask task, TimeSpan timeout,
            Func<TResult> getResult, SystemCancellationToken cancellationToken, object timeProvider = null)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return FromCanceled<TResult>(cancellationToken);
            }

            if (timeout == TimeSpan.Zero)
            {
                return FromException<TResult>(new TimeoutException());
            }

            CoyoteRuntime runtime = CoyoteRuntime.Current;
            runtime.SuppressScheduling();
            try
            {
                // Task.WaitAsync constructs its promise synchronously. Creating the model's delay and
                // projection operations must likewise be atomic with the call: allowing either creation
                // point to schedule would let a timeout win before WaitAsync has even returned.
                return WaitAsyncStateMachine<TResult>.RunAsync(
                    runtime, task, timeout, getResult, timeProvider, cancellationToken);
            }
            finally
            {
                runtime.ResumeScheduling();
            }
        }

        /// <summary>
        /// Normalizes a timeout using the same whole-millisecond range and truncation as Task.WaitAsync.
        /// </summary>
        internal static TimeSpan NormalizeWaitAsyncTimeout(TimeSpan timeout)
        {
            return CoyoteRuntime.NormalizeTimeout(timeout, nameof(timeout), MaxSupportedTimeoutMilliseconds);
        }

        /// <summary>
        /// Returns the token recorded by a canceled task, or a default token if none was recorded.
        /// </summary>
        private static SystemCancellationToken GetCancellationToken(SystemTask task)
        {
            try
            {
                task.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException exception)
            {
                return exception.CancellationToken;
            }
            catch (Exception)
            {
            }

            return default;
        }

        /// <summary>
        /// A controlled promise that records which side of Task.WaitAsync's source/timeout race won.
        /// </summary>
        private sealed class WaitAsyncStateMachine<TResult> : AsyncAwaiterStateMachine<TResult>
        {
            private readonly SystemTask SourceTask;

            private readonly SystemTask TimeoutTask;

            private readonly SystemTasks.Task<SystemTask> WinnerTask;

            private readonly SystemCancellationToken CancellationToken;

            private readonly SystemCancellationTokenSource TimeoutCancellationSource;

            private readonly Func<TResult> GetResult;

            private WaitAsyncStateMachine(CoyoteRuntime runtime, SystemTask sourceTask, TimeSpan timeout,
                Func<TResult> getResult, object timeProvider, SystemCancellationToken cancellationToken)
                : base(runtime, runContinuationAsynchronously: true)
            {
                this.SourceTask = sourceTask;
                this.CancellationToken = cancellationToken;
                this.GetResult = getResult;
                this.TimeoutCancellationSource = cancellationToken.CanBeCanceled ?
                    SystemCancellationTokenSource.CreateLinkedTokenSource(cancellationToken) :
                    new SystemCancellationTokenSource();
#if NET8_0_OR_GREATER
                this.TimeoutTask = timeProvider is TimeProvider provider ?
                    Delay(timeout, provider, this.TimeoutCancellationSource.Token) :
                    Delay(timeout, this.TimeoutCancellationSource.Token);
#else
                this.TimeoutTask = Delay(timeout, this.TimeoutCancellationSource.Token);
#endif
                this.WinnerTask = WhenAny(sourceTask, this.TimeoutTask);
            }

            internal static SystemTasks.Task<TResult> RunAsync(CoyoteRuntime runtime, SystemTask sourceTask,
                TimeSpan timeout, Func<TResult> getResult, object timeProvider,
                SystemCancellationToken cancellationToken)
            {
                var stateMachine = new WaitAsyncStateMachine<TResult>(
                    runtime, sourceTask, timeout, getResult, timeProvider, cancellationToken);
                stateMachine.MoveNext();
                runtime.RegisterKnownControlledTask(stateMachine.CompletionSource.Task);
                return stateMachine.CompletionSource.Task;
            }

            public override void MoveNext()
            {
                if (this.CurrentStatus is Status.Completed)
                {
                    return;
                }

                try
                {
                    ControlledOperation current = this.Runtime.GetExecutingOperation();
                    if (this.CurrentStatus is Status.Running &&
                        !this.WinnerTask.IsCompleted && !this.TimeoutTask.IsCompleted)
                    {
                        this.CurrentStatus = Status.Waiting;
                        this.Runtime.Schedule(this.MoveNext);
                        return;
                    }

                    SystemTask winner;
                    if (this.Runtime.SchedulingPolicy is SchedulingPolicy.Fuzzing)
                    {
                        TaskServices.WaitUntilTaskCompletes(this.Runtime, current, this.WinnerTask);
                        winner = this.WinnerTask.GetAwaiter().GetResult();
                    }
                    else
                    {
                        // Task.WhenAny remains the first-winner oracle. The timeout task is observed as a
                        // narrow fallback because a BCL-owned delay intentionally queues its continuations
                        // asynchronously when cancellation wins: the delay can be terminal while the
                        // projection is not, leaving no controlled operation able to make further progress.
                        TaskServices.WaitUntilAnyTaskCompletes(
                            this.Runtime, new SystemTask[] { this.WinnerTask, this.TimeoutTask });
                        winner = this.WinnerTask.IsCompleted ?
                            this.WinnerTask.GetAwaiter().GetResult() : this.TimeoutTask;
                    }

                    this.CurrentStatus = Status.Completed;
                    if (ReferenceEquals(winner, this.SourceTask))
                    {
                        // Retire the losing virtual timer before publishing the result. Otherwise a
                        // successful wait leaves a timer operation behind to influence liveness and
                        // virtual-time advancement after the wait itself is over.
                        this.TimeoutCancellationSource.Cancel();
                        if (this.SourceTask.IsCanceled)
                        {
                            this.CompletionSource.TrySetCanceled(GetCancellationToken(this.SourceTask));
                        }
                        else if (this.SourceTask.IsFaulted)
                        {
                            this.CompletionSource.SetException(this.SourceTask.Exception.InnerExceptions);
                        }
                        else
                        {
                            this.CompletionSource.SetResult(this.GetResult());
                        }
                    }
                    else if (this.TimeoutTask.IsCanceled)
                    {
                        this.CompletionSource.TrySetCanceled(this.CancellationToken);
                    }
                    else if (this.TimeoutTask.IsFaulted)
                    {
                        this.CompletionSource.SetException(this.TimeoutTask.Exception.InnerExceptions);
                    }
                    else
                    {
                        this.CompletionSource.SetException(new TimeoutException());
                    }
                }
                catch (Exception exception)
                {
                    this.CurrentStatus = Status.Completed;
                    this.CompletionSource.TrySetException(exception);
                }
                finally
                {
                    if (this.CurrentStatus is Status.Completed)
                    {
                        if (!this.TimeoutTask.IsCompleted)
                        {
                            this.TimeoutCancellationSource.Cancel();
                        }

                        this.TimeoutCancellationSource.Dispose();
                    }
                }
            }
        }
#endif

        /// <summary>
        /// Creates a task that has completed successfully with the specified result.
        /// </summary>
        public static SystemTasks.Task<TResult> FromResult<TResult>(TResult result)
        {
            SystemTasks.Task<TResult> task = SystemTask.FromResult(result);
            CoyoteRuntime.Current.RegisterKnownControlledTask(task);
            return task;
        }

        /// <summary>
        /// Creates a task that has completed due to cancellation with the specified cancellation token.
        /// </summary>
        public static SystemTask FromCanceled(SystemCancellationToken cancellationToken)
        {
            SystemTask task = SystemTask.FromCanceled(cancellationToken);
            CoyoteRuntime.Current.RegisterKnownControlledTask(task);
            return task;
        }

        /// <summary>
        /// Creates a task that has completed due to cancellation with the specified cancellation token.
        /// </summary>
        public static SystemTasks.Task<TResult> FromCanceled<TResult>(SystemCancellationToken cancellationToken)
        {
            SystemTasks.Task<TResult> task = SystemTask.FromCanceled<TResult>(cancellationToken);
            CoyoteRuntime.Current.RegisterKnownControlledTask(task);
            return task;
        }

        /// <summary>
        /// Creates a task that has completed with the specified exception.
        /// </summary>
        public static SystemTask FromException(Exception exception)
        {
            SystemTask task = SystemTask.FromException(exception);
            CoyoteRuntime.Current.RegisterKnownControlledTask(task);
            return task;
        }

        /// <summary>
        /// Creates a task that has completed with the specified exception.
        /// </summary>
        public static SystemTasks.Task<TResult> FromException<TResult>(Exception exception)
        {
            SystemTasks.Task<TResult> task = SystemTask.FromException<TResult>(exception);
            CoyoteRuntime.Current.RegisterKnownControlledTask(task);
            return task;
        }

        /// <summary>
        /// Returns a task awaiter for the specified task.
        /// </summary>
        public static TaskAwaiter GetAwaiter(SystemTask task) => new TaskAwaiter(task);

        /// <summary>
        /// Configures an awaiter used to await this task.
        /// </summary>
        public static ConfiguredTaskAwaitable ConfigureAwait(SystemTask task,
            bool continueOnCapturedContext) =>
            new ConfiguredTaskAwaitable(task, continueOnCapturedContext);

#if NET8_0_OR_GREATER
        /// <summary>
        /// Configures an awaiter used to await this task.
        /// </summary>
        /// <remarks>
        /// Redirected for the same reason as the boolean overload: leaving it alone would let the real
        /// task hand a real <see cref="ConfiguredTaskAwaitable"/> to a call site the rewriter has
        /// already retyped to the controlled one.
        /// </remarks>
        public static ConfiguredTaskAwaitable ConfigureAwait(SystemTask task,
            SystemTasks.ConfigureAwaitOptions options) =>
            new ConfiguredTaskAwaitable(task, options);
#endif

        /// <summary>
        /// Creates an awaitable that asynchronously yields back to the current context when awaited.
        /// </summary>
        public static YieldAwaitable Yield() => new YieldAwaitable(default);
    }

    /// <summary>
    /// Provides methods for creating generic tasks that can be controlled during testing.
    /// </summary>
    /// <remarks>This type is intended for compiler use rather than use directly in code.</remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class Task<TResult>
    {
#pragma warning disable CA1000 // Do not declare static members on generic types
        /// <summary>
        /// The default generic task factory.
        /// </summary>
        private static SystemTasks.TaskFactory<TResult> DefaultFactory = new SystemTasks.TaskFactory<TResult>();

        /// <summary>
        /// Provides access to factory methods for creating controlled generic task instances.
        /// </summary>
        public static SystemTasks.TaskFactory<TResult> Factory
        {
            get
            {
                var runtime = CoyoteRuntime.Current;
                if (runtime.SchedulingPolicy is SchedulingPolicy.None)
                {
                    return DefaultFactory;
                }

                // TODO: cache this per runtime.
                return new SystemTasks.TaskFactory<TResult>(SystemCancellationToken.None,
                    SystemTaskCreationOptions.HideScheduler, SystemTaskContinuationOptions.HideScheduler,
                    runtime.ControlledTaskScheduler);
            }
        }

#pragma warning disable CA1068 // CancellationToken parameters must come last
        /// <summary>
        /// Creates a new generic task instance with the specified function.
        /// </summary>
        public static SystemTasks.Task<TResult> Create(Func<TResult> function) =>
            new SystemTasks.Task<TResult>(function);

        /// <summary>
        /// Creates a new generic task instance with the specified function and cancellation token.
        /// </summary>
        public static SystemTasks.Task<TResult> Create(Func<TResult> function,
            SystemCancellationToken cancellationToken) =>
            new SystemTasks.Task<TResult>(function, cancellationToken);

        /// <summary>
        /// Creates a new generic task instance with the specified function and creation options.
        /// </summary>
        public static SystemTasks.Task<TResult> Create(Func<TResult> function,
            SystemTaskCreationOptions creationOptions) =>
            new SystemTasks.Task<TResult>(function, creationOptions);

        /// <summary>
        /// Creates a new generic task instance with the specified function, cancellation token, and creation options.
        /// </summary>
        public static SystemTasks.Task<TResult> Create(Func<TResult> function,
            SystemCancellationToken cancellationToken, SystemTaskCreationOptions creationOptions) =>
            new SystemTasks.Task<TResult>(function, cancellationToken, creationOptions);

        /// <summary>
        /// Creates a new generic task instance with the specified function and state.
        /// </summary>
        public static SystemTasks.Task<TResult> Create(Func<object, TResult> function, object state) =>
            new SystemTasks.Task<TResult>(function, state);

        /// <summary>
        /// Creates a new generic task instance with the specified function, state, and cancellation token.
        /// </summary>
        public static SystemTasks.Task<TResult> Create(Func<object, TResult> function, object state,
            SystemCancellationToken cancellationToken) =>
            new SystemTasks.Task<TResult>(function, state, cancellationToken);

        /// <summary>
        /// Creates a new generic task instance with the specified function, state, and creation options.
        /// </summary>
        public static SystemTasks.Task<TResult> Create(Func<object, TResult> function, object state,
            SystemTaskCreationOptions creationOptions) =>
            new SystemTasks.Task<TResult>(function, state, creationOptions);

        /// <summary>
        /// Creates a new generic task instance with the specified function, state, cancellation token, and creation options.
        /// </summary>
        public static SystemTasks.Task<TResult> Create(Func<object, TResult> function, object state,
            SystemCancellationToken cancellationToken, SystemTaskCreationOptions creationOptions) =>
            new SystemTasks.Task<TResult>(function, state, cancellationToken, creationOptions);
#pragma warning restore CA1068 // CancellationToken parameters must come last

        /// <summary>
        /// Gets the result value of the specified generic task.
        /// </summary>
#pragma warning disable CA1707 // Remove the underscores from member name
#pragma warning disable SA1300 // Element should begin with an uppercase letter
#pragma warning disable IDE1006 // Naming Styles
        public static TResult get_Result(SystemTasks.Task<TResult> task)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy != SchedulingPolicy.None)
            {
                TaskServices.WaitUntilTaskCompletes(runtime, task);
            }

            return task.Result;
        }
#pragma warning restore CA1707 // Remove the underscores from member name
#pragma warning restore SA1300 // Element should begin with an uppercase letter
#pragma warning restore IDE1006 // Naming Styles

#if NET6_0_OR_GREATER
        /// <summary>
        /// Gets a task that completes when the specified generic task completes or cancellation is requested.
        /// </summary>
        public static SystemTasks.Task<TResult> WaitAsync(SystemTasks.Task<TResult> task,
            SystemCancellationToken cancellationToken)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return task.WaitAsync(cancellationToken);
            }

            if (task.IsCompleted || !cancellationToken.CanBeCanceled)
            {
                return task;
            }

            return global::Microsoft.Coyote.Rewriting.Types.Threading.Tasks.Task.WaitAsyncCore(
                task, SystemTimeout.InfiniteTimeSpan, () => task.GetAwaiter().GetResult(), cancellationToken);
        }

        /// <summary>
        /// Gets a task that completes when the specified generic task completes or the timeout expires.
        /// </summary>
        public static SystemTasks.Task<TResult> WaitAsync(SystemTasks.Task<TResult> task, TimeSpan timeout)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return task.WaitAsync(timeout);
            }

            TimeSpan normalized = global::Microsoft.Coyote.Rewriting.Types.Threading.Tasks.Task.NormalizeWaitAsyncTimeout(timeout);
            if (task.IsCompleted || normalized == SystemTimeout.InfiniteTimeSpan)
            {
                return task;
            }

            return global::Microsoft.Coyote.Rewriting.Types.Threading.Tasks.Task.WaitAsyncCore(
                task, normalized, () => task.GetAwaiter().GetResult(), default);
        }

        /// <summary>
        /// Gets a task that completes when the specified generic task completes, the timeout expires or cancellation is requested.
        /// </summary>
        public static SystemTasks.Task<TResult> WaitAsync(SystemTasks.Task<TResult> task, TimeSpan timeout,
            SystemCancellationToken cancellationToken)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return task.WaitAsync(timeout, cancellationToken);
            }

            TimeSpan normalized = global::Microsoft.Coyote.Rewriting.Types.Threading.Tasks.Task.NormalizeWaitAsyncTimeout(timeout);
            if (task.IsCompleted || (!cancellationToken.CanBeCanceled && normalized == SystemTimeout.InfiniteTimeSpan))
            {
                return task;
            }

            return global::Microsoft.Coyote.Rewriting.Types.Threading.Tasks.Task.WaitAsyncCore(
                task, normalized, () => task.GetAwaiter().GetResult(), cancellationToken);
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// Gets a task that completes when the specified generic task completes or the timeout expires
        /// according to the specified time provider.
        /// </summary>
        public static SystemTasks.Task<TResult> WaitAsync(SystemTasks.Task<TResult> task, TimeSpan timeout,
            TimeProvider timeProvider)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return task.WaitAsync(timeout, timeProvider);
            }

            ArgumentNullException.ThrowIfNull(timeProvider);
            TimeSpan normalized = global::Microsoft.Coyote.Rewriting.Types.Threading.Tasks.Task.NormalizeWaitAsyncTimeout(timeout);
            if (task.IsCompleted || normalized == SystemTimeout.InfiniteTimeSpan)
            {
                return task;
            }

            return global::Microsoft.Coyote.Rewriting.Types.Threading.Tasks.Task.WaitAsyncCore(
                task, normalized, () => task.GetAwaiter().GetResult(), default, timeProvider);
        }

        /// <summary>
        /// Gets a task that completes when the specified generic task completes, the timeout expires
        /// according to the specified time provider or cancellation is requested.
        /// </summary>
        public static SystemTasks.Task<TResult> WaitAsync(SystemTasks.Task<TResult> task, TimeSpan timeout,
            TimeProvider timeProvider, SystemCancellationToken cancellationToken)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return task.WaitAsync(timeout, timeProvider, cancellationToken);
            }

            ArgumentNullException.ThrowIfNull(timeProvider);
            TimeSpan normalized = global::Microsoft.Coyote.Rewriting.Types.Threading.Tasks.Task.NormalizeWaitAsyncTimeout(timeout);
            if (task.IsCompleted || (!cancellationToken.CanBeCanceled && normalized == SystemTimeout.InfiniteTimeSpan))
            {
                return task;
            }

            return global::Microsoft.Coyote.Rewriting.Types.Threading.Tasks.Task.WaitAsyncCore(
                task, normalized, () => task.GetAwaiter().GetResult(), cancellationToken, timeProvider);
        }
#endif
#endif

        /// <summary>
        /// Returns a generic task awaiter for the specified generic task.
        /// </summary>
        public static TaskAwaiter<TResult> GetAwaiter(SystemTasks.Task<TResult> task) =>
            new TaskAwaiter<TResult>(task);

        /// <summary>
        /// Configures an awaiter used to await this task.
        /// </summary>
        public static ConfiguredTaskAwaitable<TResult> ConfigureAwait(
            SystemTasks.Task<TResult> task, bool continueOnCapturedContext) =>
            new ConfiguredTaskAwaitable<TResult>(task, continueOnCapturedContext);

#if NET8_0_OR_GREATER
        /// <summary>
        /// Configures an awaiter used to await this task.
        /// </summary>
        /// <remarks>
        /// A task with a result has this overload too, so it needs the same redirection as the
        /// non-generic one: leaving it alone would hand a real <see cref="ConfiguredTaskAwaitable{TResult}"/>
        /// to a call site the rewriter has already retyped to the controlled one.
        /// </remarks>
        public static ConfiguredTaskAwaitable<TResult> ConfigureAwait(
            SystemTasks.Task<TResult> task, SystemTasks.ConfigureAwaitOptions options) =>
            new ConfiguredTaskAwaitable<TResult>(task, options);
#endif

#pragma warning restore CA1000 // Do not declare static members on generic types
    }
}

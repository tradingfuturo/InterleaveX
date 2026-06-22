// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using Microsoft.Coyote.Runtime;

using SystemCancellationToken = System.Threading.CancellationToken;
#if NET
using SystemTask = System.Threading.Tasks.Task;
using SystemTaskCompletionSource = System.Threading.Tasks.TaskCompletionSource;
#endif
using SystemTaskCreationOptions = System.Threading.Tasks.TaskCreationOptions;
using SystemTasks = System.Threading.Tasks;

namespace Microsoft.Coyote.Rewriting.Types.Threading.Tasks
{
    /// <summary>
    /// Completes a controlled <c>TaskCompletionSource</c> while holding the runtime lock, so the completion
    /// is atomic with respect to the controlled scheduler.
    /// </summary>
    /// <remarks>
    /// The underlying completion runs inside <see cref="CoyoteRuntime.EnterSynchronizedSection"/> (mirroring
    /// how <c>SemaphoreSlim</c>/<c>EventWaitHandle</c> signal waiters). Without this, completing a TCS from one
    /// controlled operation while another is paused awaiting its task races the scheduler's deadlock detector:
    /// the detector can observe "no operation enabled, one paused" in the window between the completion and the
    /// awaiter being re-enabled, producing a spurious deadlock. Completing under the lock closes that window —
    /// the scheduler only ever observes the task as already completed (the awaiter's <c>() =&gt; task.IsCompleted</c>
    /// dependency resolves, or its posted continuation is scheduled, atomically). The synchronized section is
    /// reentrant, so a continuation that <c>SetResult</c> posts (and which re-enters the runtime to schedule
    /// itself) is safe.
    /// </remarks>
    internal static class ControlledTaskCompletionSource
    {
        /// <summary>
        /// Normalizes the creation options of a controlled task completion source.
        /// </summary>
        /// <remarks>
        /// Strips <see cref="SystemTaskCreationOptions.RunContinuationsAsynchronously"/> under the interleaving
        /// scheduler: it routes the completion-source task's continuations — including <c>Task.WhenAll</c>'s
        /// internal aggregation continuation, which is not rewritten — onto an uncontrolled thread-pool thread,
        /// which races the scheduler's deadlock detector (the detector can observe "no operation enabled" in the
        /// gap before that continuation runs). Running continuations synchronously keeps them under the
        /// controlled scheduler; rewritten awaiter continuations are still scheduled via the anti-inline context.
        /// </remarks>
        internal static SystemTaskCreationOptions NormalizeOptions(SystemTaskCreationOptions options)
        {
            if (CoyoteRuntime.Current.SchedulingPolicy is SchedulingPolicy.Interleaving)
            {
                return options & ~SystemTaskCreationOptions.RunContinuationsAsynchronously;
            }

            return options;
        }

        /// <summary>
        /// Runs the void completion of a controlled task completion source under the runtime lock.
        /// </summary>
        internal static void Complete(string methodName, Action complete) =>
            Complete<object>(methodName, () =>
            {
                complete();
                return null;
            });

        /// <summary>
        /// Runs the completion of a controlled task completion source under the runtime lock and returns its result.
        /// </summary>
        internal static T Complete<T>(string methodName, Func<T> complete)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy != SchedulingPolicy.Interleaving)
            {
                // Only the interleaving scheduler has the deadlock-detector race; outside it, complete directly.
                return complete();
            }

            using (runtime.EnterSynchronizedSection())
            {
                if (!runtime.TryGetExecutingOperation(out _))
                {
                    runtime.NotifyUncontrolledSynchronizationInvocation(methodName);
                }

                // Complete the underlying task while holding the runtime lock. A throw (e.g. InvalidOperationException
                // on a double-complete) propagates unchanged; the synchronized section is released by the using.
                return complete();
            }
        }
    }

#if NET
    /// <summary>
    /// Represents the producer side of a controlled task unbound to a delegate, providing
    /// access to the consumer side through the task property of the task completion source.
    /// </summary>
    /// <remarks>This type is intended for compiler use rather than use directly in code.</remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class TaskCompletionSource
    {
#pragma warning disable CA1000 // Do not declare static members on generic types
        /// <summary>
        /// Creates a new controlled task completion source with the specified options (see
        /// <see cref="ControlledTaskCompletionSource.NormalizeOptions"/>).
        /// </summary>
        public static SystemTaskCompletionSource Create(SystemTaskCreationOptions creationOptions) =>
            new SystemTaskCompletionSource(ControlledTaskCompletionSource.NormalizeOptions(creationOptions));

        /// <summary>
        /// Creates a new controlled task completion source with the specified state and options.
        /// </summary>
        public static SystemTaskCompletionSource Create(object state, SystemTaskCreationOptions creationOptions) =>
            new SystemTaskCompletionSource(state, ControlledTaskCompletionSource.NormalizeOptions(creationOptions));

        /// <summary>
        /// Gets the task created by this task completion source.
        /// </summary>
#pragma warning disable CA1707 // Remove the underscores from member name
#pragma warning disable SA1300 // Element should begin with an uppercase letter
#pragma warning disable IDE1006 // Naming Styles
        public static SystemTask get_Task(SystemTaskCompletionSource tcs)
        {
            var task = tcs.Task;
            CoyoteRuntime.Current.RegisterKnownControlledTask(task);
            return task;
        }
#pragma warning restore CA1707 // Remove the underscores from member name
#pragma warning restore SA1300 // Element should begin with an uppercase letter
#pragma warning restore IDE1006 // Naming Styles

        /// <summary>
        /// Transitions the underlying task into the <see cref="SystemTasks.TaskStatus.RanToCompletion"/> state.
        /// </summary>
        public static void SetResult(SystemTaskCompletionSource tcs) =>
            ControlledTaskCompletionSource.Complete("TaskCompletionSource.SetResult", () => tcs.SetResult());

        /// <summary>
        /// Transitions the underlying task into the <see cref="SystemTasks.TaskStatus.Faulted"/> state
        /// and binds a collection of exception objects to it.
        /// </summary>
        public static void SetException(SystemTaskCompletionSource tcs,
            IEnumerable<Exception> exceptions) =>
            ControlledTaskCompletionSource.Complete("TaskCompletionSource.SetException", () => tcs.SetException(exceptions));

        /// <summary>
        /// Transitions the underlying task into the <see cref="SystemTasks.TaskStatus.Faulted"/> state
        /// and binds it to a specified exception.
        /// </summary>
        public static void SetException(SystemTaskCompletionSource tcs, Exception exception) =>
            ControlledTaskCompletionSource.Complete("TaskCompletionSource.SetException", () => tcs.SetException(exception));

        /// <summary>
        /// Transitions the underlying task into the <see cref="SystemTasks.TaskStatus.Canceled"/> state.
        /// </summary>
        public static void SetCanceled(SystemTaskCompletionSource tcs) =>
            ControlledTaskCompletionSource.Complete("TaskCompletionSource.SetCanceled", () => tcs.SetCanceled());

        /// <summary>
        /// Attempts to transition the underlying task into the <see cref="SystemTasks.TaskStatus.RanToCompletion"/> state.
        /// </summary>
        public static bool TrySetResult(SystemTaskCompletionSource tcs) =>
            ControlledTaskCompletionSource.Complete("TaskCompletionSource.TrySetResult", () => tcs.TrySetResult());

        /// <summary>
        /// Attempts to transition the underlying task into the <see cref="SystemTasks.TaskStatus.Faulted"/> state
        /// and binds it to a specified exception.
        /// </summary>
        public static bool TrySetException(SystemTaskCompletionSource tcs, Exception exception) =>
            ControlledTaskCompletionSource.Complete("TaskCompletionSource.TrySetException", () => tcs.TrySetException(exception));

        /// <summary>
        /// Attempts to transition the underlying task into the <see cref="SystemTasks.TaskStatus.Faulted"/> state
        /// and binds a collection of exception objects to it.
        /// </summary>
        public static bool TrySetException(SystemTaskCompletionSource tcs, IEnumerable<Exception> exceptions) =>
            ControlledTaskCompletionSource.Complete("TaskCompletionSource.TrySetException", () => tcs.TrySetException(exceptions));

        /// <summary>
        /// Attempts to transition the underlying task into the <see cref="SystemTasks.TaskStatus.Canceled"/> state.
        /// </summary>
        public static bool TrySetCanceled(SystemTaskCompletionSource tcs) =>
            ControlledTaskCompletionSource.Complete("TaskCompletionSource.TrySetCanceled", () => tcs.TrySetCanceled());

        /// <summary>
        /// Attempts to transition the underlying task into the <see cref="SystemTasks.TaskStatus.Canceled"/> state
        /// and enables a cancellation token to be stored in the canceled task.
        /// </summary>
        public static bool TrySetCanceled(SystemTaskCompletionSource tcs, SystemCancellationToken cancellationToken) =>
            ControlledTaskCompletionSource.Complete("TaskCompletionSource.TrySetCanceled", () => tcs.TrySetCanceled(cancellationToken));
#pragma warning restore CA1000 // Do not declare static members on generic types
    }
#endif

    /// <summary>
    /// Represents the producer side of a controlled task unbound to a delegate, providing
    /// access to the consumer side through the task property of the task completion source.
    /// </summary>
    /// <typeparam name="TResult">The type of the result value.</typeparam>
    /// <remarks>This type is intended for compiler use rather than use directly in code.</remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class TaskCompletionSource<TResult>
    {
#pragma warning disable CA1000 // Do not declare static members on generic types
        /// <summary>
        /// Creates a new controlled task completion source with the specified options (see
        /// <see cref="ControlledTaskCompletionSource.NormalizeOptions"/>).
        /// </summary>
        public static SystemTasks.TaskCompletionSource<TResult> Create(SystemTaskCreationOptions creationOptions) =>
            new SystemTasks.TaskCompletionSource<TResult>(ControlledTaskCompletionSource.NormalizeOptions(creationOptions));

        /// <summary>
        /// Creates a new controlled task completion source with the specified state and options.
        /// </summary>
        public static SystemTasks.TaskCompletionSource<TResult> Create(object state, SystemTaskCreationOptions creationOptions) =>
            new SystemTasks.TaskCompletionSource<TResult>(state, ControlledTaskCompletionSource.NormalizeOptions(creationOptions));

        /// <summary>
        /// Gets the task created by this task completion source.
        /// </summary>
#pragma warning disable CA1707 // Remove the underscores from member name
#pragma warning disable SA1300 // Element should begin with an uppercase letter
#pragma warning disable IDE1006 // Naming Styles
        public static SystemTasks.Task<TResult> get_Task(SystemTasks.TaskCompletionSource<TResult> tcs)
        {
            var task = tcs.Task;
            CoyoteRuntime.Current.RegisterKnownControlledTask(task);
            return task;
        }
#pragma warning restore CA1707 // Remove the underscores from member name
#pragma warning restore SA1300 // Element should begin with an uppercase letter
#pragma warning restore IDE1006 // Naming Styles

        /// <summary>
        /// Transitions the underlying task into the <see cref="SystemTasks.TaskStatus.RanToCompletion"/> state.
        /// </summary>
        public static void SetResult(SystemTasks.TaskCompletionSource<TResult> tcs, TResult result) =>
            ControlledTaskCompletionSource.Complete("TaskCompletionSource.SetResult", () => tcs.SetResult(result));

        /// <summary>
        /// Transitions the underlying task into the <see cref="SystemTasks.TaskStatus.Faulted"/> state
        /// and binds a collection of exception objects to it.
        /// </summary>
        public static void SetException(SystemTasks.TaskCompletionSource<TResult> tcs,
            IEnumerable<Exception> exceptions) =>
            ControlledTaskCompletionSource.Complete("TaskCompletionSource.SetException", () => tcs.SetException(exceptions));

        /// <summary>
        /// Transitions the underlying task into the <see cref="SystemTasks.TaskStatus.Faulted"/> state
        /// and binds it to a specified exception.
        /// </summary>
        public static void SetException(SystemTasks.TaskCompletionSource<TResult> tcs, Exception exception) =>
            ControlledTaskCompletionSource.Complete("TaskCompletionSource.SetException", () => tcs.SetException(exception));

        /// <summary>
        /// Transitions the underlying task into the <see cref="SystemTasks.TaskStatus.Canceled"/> state.
        /// </summary>
        public static void SetCanceled(SystemTasks.TaskCompletionSource<TResult> tcs) =>
            ControlledTaskCompletionSource.Complete("TaskCompletionSource.SetCanceled", () => tcs.SetCanceled());

        /// <summary>
        /// Attempts to transition the underlying task into the <see cref="SystemTasks.TaskStatus.RanToCompletion"/> state.
        /// </summary>
        public static bool TrySetResult(SystemTasks.TaskCompletionSource<TResult> tcs, TResult result) =>
            ControlledTaskCompletionSource.Complete("TaskCompletionSource.TrySetResult", () => tcs.TrySetResult(result));

        /// <summary>
        /// Attempts to transition the underlying task into the <see cref="SystemTasks.TaskStatus.Faulted"/> state
        /// and binds it to a specified exception.
        /// </summary>
        public static bool TrySetException(SystemTasks.TaskCompletionSource<TResult> tcs, Exception exception) =>
            ControlledTaskCompletionSource.Complete("TaskCompletionSource.TrySetException", () => tcs.TrySetException(exception));

        /// <summary>
        /// Attempts to transition the underlying task into the <see cref="SystemTasks.TaskStatus.Faulted"/> state
        /// and binds a collection of exception objects to it.
        /// </summary>
        public static bool TrySetException(SystemTasks.TaskCompletionSource<TResult> tcs, IEnumerable<Exception> exceptions) =>
            ControlledTaskCompletionSource.Complete("TaskCompletionSource.TrySetException", () => tcs.TrySetException(exceptions));

        /// <summary>
        /// Attempts to transition the underlying task into the <see cref="SystemTasks.TaskStatus.Canceled"/> state.
        /// </summary>
        public static bool TrySetCanceled(SystemTasks.TaskCompletionSource<TResult> tcs) =>
            ControlledTaskCompletionSource.Complete("TaskCompletionSource.TrySetCanceled", () => tcs.TrySetCanceled());

        /// <summary>
        /// Attempts to transition the underlying task into the <see cref="SystemTasks.TaskStatus.Canceled"/> state
        /// and enables a cancellation token to be stored in the canceled task.
        /// </summary>
        public static bool TrySetCanceled(SystemTasks.TaskCompletionSource<TResult> tcs,
            SystemCancellationToken cancellationToken) =>
            ControlledTaskCompletionSource.Complete("TaskCompletionSource.TrySetCanceled", () => tcs.TrySetCanceled(cancellationToken));
#pragma warning restore CA1000 // Do not declare static members on generic types
    }
}

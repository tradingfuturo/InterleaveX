// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#if NET9_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Coyote.Runtime;
using SystemTask = System.Threading.Tasks.Task;
using SystemTaskCompletionSource = System.Threading.Tasks.TaskCompletionSource<bool>;
using SystemTaskContinuationOptions = System.Threading.Tasks.TaskContinuationOptions;
using SystemTaskCreationOptions = System.Threading.Tasks.TaskCreationOptions;
using SystemTaskScheduler = System.Threading.Tasks.TaskScheduler;
using SystemValueTask = System.Threading.Tasks.ValueTask;

namespace Microsoft.Coyote.Rewriting.Types.Threading.Tasks
{
    /// <summary>
    /// Implements a controlled replacement for <see cref="SystemTask.WhenEach(SystemTask[])"/>.
    /// </summary>
    /// <typeparam name="TTask">The type of each yielded task.</typeparam>
    internal sealed class ControlledWhenEachEnumerable<TTask> : IAsyncEnumerable<TTask>
        where TTask : SystemTask
    {
        /// <summary>
        /// Responsible for controlling the execution of tasks during systematic testing.
        /// </summary>
        private readonly CoyoteRuntime Runtime;

        /// <summary>
        /// The task sequence to enumerate.
        /// </summary>
        private readonly TTask[] Tasks;

        /// <summary>
        /// Initializes a new instance of the <see cref="ControlledWhenEachEnumerable{TTask}"/> class.
        /// </summary>
        internal ControlledWhenEachEnumerable(CoyoteRuntime runtime, IEnumerable<TTask> tasks)
        {
            this.Runtime = runtime;
            this.Tasks = ValidateAndMaterialize(tasks, nameof(tasks));
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ControlledWhenEachEnumerable{TTask}"/> class.
        /// </summary>
        internal ControlledWhenEachEnumerable(CoyoteRuntime runtime, ReadOnlySpan<TTask> tasks)
        {
            this.Runtime = runtime;
            this.Tasks = ValidateAndMaterialize(tasks, nameof(tasks));
        }

        /// <summary>
        /// Gets the async enumerator.
        /// </summary>
        public IAsyncEnumerator<TTask> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new Enumerator(this.Runtime, this.Tasks);

        /// <summary>
        /// Validates and materializes the specified task sequence.
        /// </summary>
        private static TTask[] ValidateAndMaterialize(IEnumerable<TTask> tasks, string parameterName)
        {
            if (tasks is null)
            {
                throw new ArgumentNullException(parameterName);
            }

            var list = new List<TTask>();
            foreach (var task in tasks)
            {
                if (task is null)
                {
                    throw new ArgumentException("The tasks collection cannot contain a null task.", parameterName);
                }

                list.Add(task);
            }

            return list.ToArray();
        }

        /// <summary>
        /// Validates and materializes the specified task span.
        /// </summary>
        private static TTask[] ValidateAndMaterialize(ReadOnlySpan<TTask> tasks, string parameterName)
        {
            var list = new TTask[tasks.Length];
            for (int idx = 0; idx < tasks.Length; idx++)
            {
                TTask task = tasks[idx];
                if (task is null)
                {
                    throw new ArgumentException("The tasks collection cannot contain a null task.", parameterName);
                }

                list[idx] = task;
            }

            return list;
        }

        /// <summary>
        /// Implements a controlled async enumerator for task completion order.
        /// </summary>
        private sealed class Enumerator : IAsyncEnumerator<TTask>
        {
            /// <summary>
            /// Responsible for controlling the execution of tasks during systematic testing.
            /// </summary>
            private readonly CoyoteRuntime Runtime;

            /// <summary>
            /// Synchronizes access to this enumerator state.
            /// </summary>
            private readonly object SyncObject;

            /// <summary>
            /// Queue of tasks that have completed and are ready to be yielded.
            /// </summary>
            private readonly Queue<TTask> CompletedTasks;

            /// <summary>
            /// Number of pending task completions.
            /// </summary>
            private int RemainingCompletions;

            /// <summary>
            /// Completion source of the pending move-next call.
            /// </summary>
            private SystemTaskCompletionSource PendingMoveNext;

            /// <summary>
            /// True if this enumerator has been disposed, else false.
            /// </summary>
            private bool IsDisposed;

            /// <summary>
            /// Gets the current task.
            /// </summary>
            public TTask Current { get; private set; }

            /// <summary>
            /// Initializes a new instance of the <see cref="Enumerator"/> class.
            /// </summary>
            internal Enumerator(CoyoteRuntime runtime, TTask[] tasks)
            {
                this.Runtime = runtime;
                this.SyncObject = new object();
                this.CompletedTasks = new Queue<TTask>(tasks.Length);
                this.RemainingCompletions = tasks.Length;
                this.PendingMoveNext = null;
                this.IsDisposed = false;
                this.Current = null;

                for (int idx = 0; idx < tasks.Length; idx++)
                {
                    this.RegisterCompletionContinuation(tasks[idx]);
                }
            }

            /// <summary>
            /// Moves asynchronously to the next completed task.
            /// </summary>
            public System.Threading.Tasks.ValueTask<bool> MoveNextAsync()
            {
                lock (this.SyncObject)
                {
                    if (this.IsDisposed)
                    {
                        this.Current = null;
                        return new System.Threading.Tasks.ValueTask<bool>(false);
                    }

                    if (this.CompletedTasks.Count > 0)
                    {
                        this.Current = this.CompletedTasks.Dequeue();
                        return new System.Threading.Tasks.ValueTask<bool>(true);
                    }

                    if (this.RemainingCompletions is 0)
                    {
                        this.Current = null;
                        return new System.Threading.Tasks.ValueTask<bool>(false);
                    }

                    if (this.PendingMoveNext is null)
                    {
                        this.PendingMoveNext = new SystemTaskCompletionSource(
                            SystemTaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    System.Threading.Tasks.Task<bool> pendingTask = this.PendingMoveNext.Task;
                    this.Runtime.RegisterKnownControlledTask(pendingTask);
                    return new System.Threading.Tasks.ValueTask<bool>(pendingTask);
                }
            }

            /// <summary>
            /// Disposes this enumerator.
            /// </summary>
            public SystemValueTask DisposeAsync()
            {
                SystemTaskCompletionSource pendingMoveNext = null;
                lock (this.SyncObject)
                {
                    if (this.IsDisposed)
                    {
                        return default;
                    }

                    this.IsDisposed = true;
                    this.Current = null;
                    this.CompletedTasks.Clear();
                    this.RemainingCompletions = 0;
                    pendingMoveNext = this.PendingMoveNext;
                    this.PendingMoveNext = null;
                }

                pendingMoveNext?.TrySetResult(false);
                return default;
            }

            /// <summary>
            /// Registers the continuation for the specified task completion.
            /// </summary>
            private void RegisterCompletionContinuation(TTask task)
            {
                if (task.IsCompleted)
                {
                    this.OnTaskCompleted(task);
                    return;
                }

                SystemTaskScheduler scheduler = this.Runtime?.SchedulingPolicy != SchedulingPolicy.None ?
                    this.Runtime.TaskFactory.Scheduler :
                    SystemTaskScheduler.Default;

                task.ContinueWith(static (completedTask, state) =>
                {
                    ((Enumerator)state).OnTaskCompleted((TTask)completedTask);
                },
                this,
                CancellationToken.None,
                SystemTaskContinuationOptions.ExecuteSynchronously,
                scheduler);
            }

            /// <summary>
            /// Processes the completion of the specified task.
            /// </summary>
            private void OnTaskCompleted(TTask task)
            {
                SystemTaskCompletionSource pendingMoveNext = null;
                bool hasPendingMoveNext = false;
                lock (this.SyncObject)
                {
                    if (this.IsDisposed || this.RemainingCompletions is 0)
                    {
                        return;
                    }

                    this.CompletedTasks.Enqueue(task);
                    this.RemainingCompletions--;
                    if (this.PendingMoveNext != null)
                    {
                        hasPendingMoveNext = true;
                        pendingMoveNext = this.PendingMoveNext;
                        this.PendingMoveNext = null;
                        this.Current = this.CompletedTasks.Dequeue();
                    }
                }

                if (hasPendingMoveNext)
                {
                    pendingMoveNext.TrySetResult(true);
                }
            }
        }
    }
}
#endif

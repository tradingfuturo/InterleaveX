// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Runtime.CompilerServices;
using SystemCompiler = System.Runtime.CompilerServices;
using SystemTask = System.Threading.Tasks.Task;
using SystemTasks = System.Threading.Tasks;

namespace Microsoft.Coyote.Rewriting.Types.Runtime.CompilerServices
{
    /// <summary>
    /// Provides an awaitable object that is the outcome of invoking <see cref="SystemTask.ConfigureAwait(bool)"/>.
    /// </summary>
    /// <remarks>This type is intended for compiler use only.</remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public struct ConfiguredTaskAwaitable
    {
        /// <summary>
        /// The task awaiter.
        /// </summary>
        private readonly ConfiguredTaskAwaiter Awaiter;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConfiguredTaskAwaitable"/> struct.
        /// </summary>
        internal ConfiguredTaskAwaitable(SystemTask awaitedTask, bool continueOnCapturedContext)
        {
            this.Awaiter = new ConfiguredTaskAwaiter(awaitedTask, continueOnCapturedContext);
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// Initializes a new instance of the <see cref="ConfiguredTaskAwaitable"/> struct.
        /// </summary>
        internal ConfiguredTaskAwaitable(SystemTask awaitedTask, SystemTasks.ConfigureAwaitOptions options)
        {
            this.Awaiter = new ConfiguredTaskAwaiter(awaitedTask, options);
        }
#endif

        /// <summary>
        /// Returns an awaiter for this awaitable object.
        /// </summary>
        /// <returns>The awaiter.</returns>
        public ConfiguredTaskAwaiter GetAwaiter() => this.Awaiter;

        /// <summary>
        /// Provides an awaiter for an awaitable object.
        /// </summary>
        /// <remarks>This type is intended for compiler use only.</remarks>
        public struct ConfiguredTaskAwaiter : IControllableAwaiter, SystemCompiler.ICriticalNotifyCompletion, SystemCompiler.INotifyCompletion
        {
            /// <summary>
            /// The task being awaited.
            /// </summary>
            private readonly SystemTask AwaitedTask;

            /// <summary>
            /// The task awaiter.
            /// </summary>
            private readonly SystemCompiler.ConfiguredTaskAwaitable.ConfiguredTaskAwaiter Awaiter;

            /// <summary>
            /// The runtime controlling this awaiter.
            /// </summary>
            private readonly CoyoteRuntime Runtime;

            /// <summary>
            /// True if the await must suspend even when the task has already completed, else false.
            /// </summary>
            private readonly bool IsYieldForced;

            /// <summary>
            /// True if the awaiter has completed, else false.
            /// </summary>
            /// <remarks>
            /// A forced yield must never report completion: the caller asked for a suspension point,
            /// and answering from the awaited task would resume inline and drop the interleaving the
            /// suspension exists to expose.
            /// </remarks>
            public bool IsCompleted =>
                !this.IsYieldForced && (this.AwaitedTask?.IsCompleted ?? this.Awaiter.IsCompleted);

            /// <inheritdoc/>
            bool IControllableAwaiter.IsControlled =>
                !this.Runtime?.IsTaskUncontrolled(this.AwaitedTask) ?? false;

            /// <summary>
            /// True if the continuation must be handed to the runtime rather than to the awaiter.
            /// </summary>
            private bool IsContinuationControlled =>
                this.Runtime != null && this.AwaitedTask != null &&
                (!this.AwaitedTask.IsCompleted || this.IsYieldForced);

            /// <summary>
            /// Initializes a new instance of the <see cref="ConfiguredTaskAwaiter"/> struct.
            /// </summary>
            internal ConfiguredTaskAwaiter(SystemTask awaitedTask, bool continueOnCapturedContext)
            {
                if (RuntimeProvider.TryGetFromSynchronizationContext(out CoyoteRuntime runtime))
                {
                    // Force the continuation to run on the current context so that it can be controlled.
                    continueOnCapturedContext = true;
                }

                this.AwaitedTask = awaitedTask;
                this.Awaiter = awaitedTask.ConfigureAwait(continueOnCapturedContext).GetAwaiter();
                this.Runtime = runtime;
                this.IsYieldForced = false;
            }

#if NET8_0_OR_GREATER
            /// <summary>
            /// Initializes a new instance of the <see cref="ConfiguredTaskAwaiter"/> struct.
            /// </summary>
            /// <remarks>
            /// The options are handed to the real awaiter unchanged apart from the captured context,
            /// so <c>SuppressThrowing</c> keeps deciding what <see cref="GetResult"/> propagates, and
            /// <c>ForceYielding</c> is honored through <see cref="IsCompleted"/>.
            /// </remarks>
            internal ConfiguredTaskAwaiter(SystemTask awaitedTask, SystemTasks.ConfigureAwaitOptions options)
            {
                if (RuntimeProvider.TryGetFromSynchronizationContext(out CoyoteRuntime runtime))
                {
                    // Force the continuation to run on the current context so that it can be controlled.
                    options |= SystemTasks.ConfigureAwaitOptions.ContinueOnCapturedContext;
                }

                this.AwaitedTask = awaitedTask;
                this.Awaiter = awaitedTask.ConfigureAwait(options).GetAwaiter();
                this.Runtime = runtime;
                this.IsYieldForced = options.HasFlag(SystemTasks.ConfigureAwaitOptions.ForceYielding);
            }
#endif

            /// <summary>
            /// Ends asynchronously waiting for the completion of the awaiter.
            /// </summary>
            public void GetResult()
            {
                TaskServices.WaitUntilTaskCompletes(this.Runtime, this.AwaitedTask);
                this.Awaiter.GetResult();
            }

            /// <summary>
            /// Schedules the continuation action for the task associated with this awaiter.
            /// </summary>
            /// <param name="continuation">The action to invoke when the await operation completes.</param>
            public void OnCompleted(Action continuation)
            {
                if (this.IsContinuationControlled)
                {
                    if (!this.Runtime.TryPrepareContinuation(continuation, out SynchronizationContext savedSyncCtx))
                    {
                        // The controlling runtime is gone, so drop this orphaned continuation.
                        return;
                    }

                    try
                    {
                        this.Awaiter.OnCompleted(continuation);
                    }
                    catch (Exception ex) when (this.Runtime.IsContinuationOrphaned(ex))
                    {
                        // Dropped for the reason documented on CoyoteRuntime.TryPrepareContinuation.
                    }
                    finally
                    {
                        SynchronizationContext.SetSynchronizationContext(savedSyncCtx);
                    }
                }
                else
                {
                    this.Awaiter.OnCompleted(continuation);
                }
            }

            /// <summary>
            /// Schedules the continuation action for the task associated with this awaiter.
            /// </summary>
            /// <param name="continuation">The action to invoke when the await operation completes.</param>
            public void UnsafeOnCompleted(Action continuation)
            {
                if (this.IsContinuationControlled)
                {
                    if (!this.Runtime.TryPrepareContinuation(continuation, out SynchronizationContext savedSyncCtx))
                    {
                        // The controlling runtime is gone, so drop this orphaned continuation.
                        return;
                    }

                    try
                    {
                        this.Awaiter.UnsafeOnCompleted(continuation);
                    }
                    catch (Exception ex) when (this.Runtime.IsContinuationOrphaned(ex))
                    {
                        // Dropped for the reason documented on CoyoteRuntime.TryPrepareContinuation.
                    }
                    finally
                    {
                        SynchronizationContext.SetSynchronizationContext(savedSyncCtx);
                    }
                }
                else
                {
                    this.Awaiter.UnsafeOnCompleted(continuation);
                }
            }
        }
    }

    /// <summary>
    /// Provides an awaitable object that enables configured awaits on a <see cref="SystemTasks.Task{TResult}"/>.
    /// </summary>
    /// <remarks>This type is intended for compiler use only.</remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public struct ConfiguredTaskAwaitable<TResult>
    {
        /// <summary>
        /// The task awaiter.
        /// </summary>
        private readonly ConfiguredTaskAwaiter Awaiter;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConfiguredTaskAwaitable{TResult}"/> struct.
        /// </summary>
        internal ConfiguredTaskAwaitable(SystemTasks.Task<TResult> awaitedTask, bool continueOnCapturedContext)
        {
            this.Awaiter = new ConfiguredTaskAwaiter(awaitedTask, continueOnCapturedContext);
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// Initializes a new instance of the <see cref="ConfiguredTaskAwaitable{TResult}"/> struct.
        /// </summary>
        internal ConfiguredTaskAwaitable(SystemTasks.Task<TResult> awaitedTask,
            SystemTasks.ConfigureAwaitOptions options)
        {
            this.Awaiter = new ConfiguredTaskAwaiter(awaitedTask, options);
        }
#endif

        /// <summary>
        /// Returns an awaiter for this awaitable object.
        /// </summary>
        /// <returns>The awaiter.</returns>
        public ConfiguredTaskAwaiter GetAwaiter() => this.Awaiter;

        /// <summary>
        /// Provides an awaiter for an awaitable object.
        /// </summary>
        /// <remarks>This type is intended for compiler use only.</remarks>
        public struct ConfiguredTaskAwaiter : IControllableAwaiter, SystemCompiler.ICriticalNotifyCompletion, SystemCompiler.INotifyCompletion
        {
            /// <summary>
            /// The task being awaited.
            /// </summary>
            private readonly SystemTasks.Task<TResult> AwaitedTask;

            /// <summary>
            /// The task awaiter.
            /// </summary>
            private readonly SystemCompiler.ConfiguredTaskAwaitable<TResult>.ConfiguredTaskAwaiter Awaiter;

            /// <summary>
            /// The runtime controlling this awaiter.
            /// </summary>
            private readonly CoyoteRuntime Runtime;

            /// <summary>
            /// True if the await must suspend even when the task has already completed, else false.
            /// </summary>
            private readonly bool IsYieldForced;

            /// <summary>
            /// True if the awaiter has completed, else false.
            /// </summary>
            /// <remarks>
            /// A forced yield must never report completion: the caller asked for a suspension point,
            /// and answering from the awaited task would resume inline and drop the interleaving the
            /// suspension exists to expose.
            /// </remarks>
            public bool IsCompleted =>
                !this.IsYieldForced && (this.AwaitedTask?.IsCompleted ?? this.Awaiter.IsCompleted);

            /// <inheritdoc/>
            bool IControllableAwaiter.IsControlled =>
                !this.Runtime?.IsTaskUncontrolled(this.AwaitedTask) ?? false;

            /// <summary>
            /// True if the continuation must be handed to the runtime rather than to the awaiter.
            /// </summary>
            private bool IsContinuationControlled =>
                this.Runtime != null && this.AwaitedTask != null &&
                (!this.AwaitedTask.IsCompleted || this.IsYieldForced);

            /// <summary>
            /// Initializes a new instance of the <see cref="ConfiguredTaskAwaiter"/> struct.
            /// </summary>
            internal ConfiguredTaskAwaiter(SystemTasks.Task<TResult> awaitedTask, bool continueOnCapturedContext)
            {
                if (RuntimeProvider.TryGetFromSynchronizationContext(out CoyoteRuntime runtime))
                {
                    // Force the continuation to run on the current context so that it can be controlled.
                    continueOnCapturedContext = true;
                }

                this.AwaitedTask = awaitedTask;
                this.Awaiter = awaitedTask.ConfigureAwait(continueOnCapturedContext).GetAwaiter();
                this.Runtime = runtime;
                this.IsYieldForced = false;
            }

#if NET8_0_OR_GREATER
            /// <summary>
            /// Initializes a new instance of the <see cref="ConfiguredTaskAwaiter"/> struct.
            /// </summary>
            /// <remarks>
            /// The options are handed to the real awaiter, which is also what rejects the
            /// <c>SuppressThrowing</c> that a task with a result cannot honor.
            /// </remarks>
            internal ConfiguredTaskAwaiter(SystemTasks.Task<TResult> awaitedTask,
                SystemTasks.ConfigureAwaitOptions options)
            {
                if (RuntimeProvider.TryGetFromSynchronizationContext(out CoyoteRuntime runtime))
                {
                    // Force the continuation to run on the current context so that it can be controlled.
                    options |= SystemTasks.ConfigureAwaitOptions.ContinueOnCapturedContext;
                }

                this.AwaitedTask = awaitedTask;
                this.Awaiter = awaitedTask.ConfigureAwait(options).GetAwaiter();
                this.Runtime = runtime;
                this.IsYieldForced = options.HasFlag(SystemTasks.ConfigureAwaitOptions.ForceYielding);
            }
#endif

            /// <summary>
            /// Ends asynchronously waiting for the completion of the awaiter.
            /// </summary>
            public TResult GetResult()
            {
                TaskServices.WaitUntilTaskCompletes(this.Runtime, this.AwaitedTask);
                return this.Awaiter.GetResult();
            }

            /// <summary>
            /// Schedules the continuation action for the task associated with this awaiter.
            /// </summary>
            /// <param name="continuation">The action to invoke when the await operation completes.</param>
            public void OnCompleted(Action continuation)
            {
                if (this.IsContinuationControlled)
                {
                    if (!this.Runtime.TryPrepareContinuation(continuation, out SynchronizationContext savedSyncCtx))
                    {
                        // The controlling runtime is gone, so drop this orphaned continuation.
                        return;
                    }

                    try
                    {
                        this.Awaiter.OnCompleted(continuation);
                    }
                    catch (Exception ex) when (this.Runtime.IsContinuationOrphaned(ex))
                    {
                        // Dropped for the reason documented on CoyoteRuntime.TryPrepareContinuation.
                    }
                    finally
                    {
                        SynchronizationContext.SetSynchronizationContext(savedSyncCtx);
                    }
                }
                else
                {
                    this.Awaiter.OnCompleted(continuation);
                }
            }

            /// <summary>
            /// Schedules the continuation action for the task associated with this awaiter.
            /// </summary>
            /// <param name="continuation">The action to invoke when the await operation completes.</param>
            public void UnsafeOnCompleted(Action continuation)
            {
                if (this.IsContinuationControlled)
                {
                    if (!this.Runtime.TryPrepareContinuation(continuation, out SynchronizationContext savedSyncCtx))
                    {
                        // The controlling runtime is gone, so drop this orphaned continuation.
                        return;
                    }

                    try
                    {
                        this.Awaiter.UnsafeOnCompleted(continuation);
                    }
                    catch (Exception ex) when (this.Runtime.IsContinuationOrphaned(ex))
                    {
                        // Dropped for the reason documented on CoyoteRuntime.TryPrepareContinuation.
                    }
                    finally
                    {
                        SynchronizationContext.SetSynchronizationContext(savedSyncCtx);
                    }
                }
                else
                {
                    this.Awaiter.UnsafeOnCompleted(continuation);
                }
            }
        }
    }
}

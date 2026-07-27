// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

using System;
using SystemTasks = System.Threading.Tasks;

namespace Microsoft.Coyote.Runtime.CompilerServices
{
    /// <summary>
    /// Implements a state machine that can be used to control and asynchronously wait
    /// for the completion of a task during testing.
    /// </summary>
    /// <remarks>
    /// We should be able to replace this in certain instances with the "AsyncMethodBuilder override" feature in C# 10.
    /// See: https://docs.microsoft.com/en-us/dotnet/csharp/language-reference/proposals/csharp-10.0/async-method-builders.
    /// </remarks>
    internal class AsyncTaskAwaiterStateMachine<TResult> : AsyncAwaiterStateMachine<TResult>
    {
        /// <summary>
        /// Handle that produces the completion of this state machine.
        /// </summary>
        private readonly SystemTasks.Task<TResult> AwaitedTask;

        /// <summary>
        /// Initializes a new instance of the <see cref="AsyncTaskAwaiterStateMachine{T}"/> class.
        /// </summary>
        private AsyncTaskAwaiterStateMachine(CoyoteRuntime runtime, SystemTasks.Task<TResult> awaitedTask, bool runContinuationAsynchronously)
            : base(runtime, runContinuationAsynchronously)
        {
            this.AwaitedTask = awaitedTask;
        }

        /// <summary>
        /// Runs an asynchronous state machine that will pause the specified operation
        /// until the task completes and return the result.
        /// </summary>
        internal static SystemTasks.Task<TResult> RunAsync(CoyoteRuntime runtime, SystemTasks.Task<TResult> awaitedTask, bool runContinuationAsynchronously)
        {
            var stateMachine = new AsyncTaskAwaiterStateMachine<TResult>(runtime, awaitedTask, runContinuationAsynchronously);
            stateMachine.MoveNext();
            runtime.RegisterKnownControlledTask(stateMachine.CompletionSource.Task);
            return stateMachine.CompletionSource.Task;
        }

        /// <summary>
        /// Moves the state machine to its next state.
        /// </summary>
        public override void MoveNext()
        {
            if (this.CurrentStatus != Status.Completed)
            {
                try
                {
                    ControlledOperation current = this.Runtime.GetExecutingOperation();
                    if (this.CurrentStatus is Status.Running && !this.AwaitedTask.IsCompleted)
                    {
                        // Schedule the continuation to execute asynchronously after the current state completes.
                        this.CurrentStatus = Status.Waiting;
                        if (this.RunContinuationAsynchronously)
                        {
                            this.Runtime.Schedule(this.MoveNext);
                        }
                        else
                        {
                            current.SetContinuationCallback(this.MoveNext);
                        }

                        return;
                    }

                    // Wait for the task processing the result to complete synchronously.
                    TaskServices.WaitUntilTaskCompletes(this.Runtime, current, this.AwaitedTask);

                    // Complete the state machine by mirroring the awaited task's outcome, so that a
                    // faulted or canceled source propagates as-is instead of surfacing an
                    // AggregateException (reading '.Result' on a faulted task would wrap it, and a
                    // completed channel must surface its ChannelClosedException/cancellation intact).
                    this.CurrentStatus = Status.Completed;
                    if (this.AwaitedTask.IsCanceled)
                    {
                        this.CompletionSource.SetCanceled();
                    }
                    else if (this.AwaitedTask.IsFaulted)
                    {
                        this.CompletionSource.SetException(this.AwaitedTask.Exception.InnerExceptions);
                    }
                    else
                    {
                        this.CompletionSource.SetResult(this.AwaitedTask.Result);
                    }
                }
                catch (Exception exception)
                {
                    this.CurrentStatus = Status.Completed;
                    this.CompletionSource.SetException(exception);
                }
            }
        }
    }
}

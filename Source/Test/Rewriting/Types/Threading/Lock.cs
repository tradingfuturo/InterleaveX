// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#if NET9_0_OR_GREATER
using System;
using System.Collections.Generic;
using Microsoft.Coyote.Runtime;
using SystemThreading = System.Threading;

#pragma warning disable CS9216 // Lock objects are intentionally passed as object to SynchronizedBlock

namespace Microsoft.Coyote.Rewriting.Types.Threading
{
    /// <summary>
    /// Provides methods for locks that can be controlled during testing.
    /// </summary>
    /// <remarks>This type is intended for compiler use rather than use directly in code.</remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class Lock
    {
        /// <summary>
        /// Thread-local stack tracking active lock scopes for the current thread.
        /// Used to associate Lock.Scope.Dispose() calls with their corresponding Lock instance.
        /// </summary>
        [ThreadStatic]
        private static Stack<Monitor.SynchronizedBlock> ScopeStack;

        /// <summary>
        /// Clears the lock scopes tracked for the current thread.
        /// </summary>
        /// <remarks>
        /// The stack is only balanced when every <see cref="EnterScope"/> is matched by a
        /// <see cref="Dispose"/>. That does not happen when an operation is terminated part way through
        /// a lock scope, which is how every iteration that detaches ends. On a thread that goes on to
        /// execute another operation, a leftover entry would cause the next <see cref="Dispose"/> to exit
        /// a block belonging to an operation that has already completed.
        /// </remarks>
        internal static void ResetScopeStack() => ScopeStack?.Clear();

        /// <summary>
        /// Creates a new <see cref="SystemThreading.Lock"/> instance.
        /// </summary>
        public static SystemThreading.Lock Create() => new SystemThreading.Lock();

        /// <summary>
        /// Enters the lock and returns a scope that can be disposed to exit the lock.
        /// </summary>
        public static SystemThreading.Lock.Scope EnterScope(SystemThreading.Lock lockObj)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                runtime.TryGetExecutingOperation(out _))
            {
                // A null block means the iteration is tearing down; see Monitor.LockBlock. The real
                // scope is still entered, matching Dispose, which always disposes it.
                var block = Monitor.LockBlock(runtime, lockObj);
                if (block != null)
                {
                    (ScopeStack ??= new Stack<Monitor.SynchronizedBlock>()).Push(block);
                }

                return lockObj.EnterScope();
            }
            else
            {
                if (runtime.SchedulingPolicy is SchedulingPolicy.Fuzzing &&
                    runtime.TryGetExecutingOperation(out ControlledOperation current))
                {
                    runtime.DelayOperation(current);
                }

                return lockObj.EnterScope();
            }
        }

        /// <summary>
        /// Exits the lock scope. Replaces the instance call to <see cref="SystemThreading.Lock.Scope.Dispose"/>.
        /// </summary>
        public static void Dispose(ref SystemThreading.Lock.Scope scope)
        {
            scope.Dispose();

            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                runtime.TryGetExecutingOperation(out _) &&
                ScopeStack?.Count > 0)
            {
                // Pop to keep the scope stack balanced even during teardown, but only touch the block
                // while the iteration is still running; see Monitor.FindBlock.
                var block = ScopeStack.Pop();
                if (!runtime.HasExecutionEnded)
                {
                    block.Exit();
                }
            }
        }

        /// <summary>
        /// Enters the lock.
        /// </summary>
        public static void Enter(SystemThreading.Lock lockObj)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                runtime.TryGetExecutingOperation(out _))
            {
                Monitor.LockBlock(runtime, lockObj);
            }
            else
            {
                if (runtime.SchedulingPolicy is SchedulingPolicy.Fuzzing &&
                    runtime.TryGetExecutingOperation(out ControlledOperation current))
                {
                    runtime.DelayOperation(current);
                }

                lockObj.Enter();
            }
        }

        /// <summary>
        /// Exits the lock.
        /// </summary>
        public static void Exit(SystemThreading.Lock lockObj)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                runtime.TryGetExecutingOperation(out _))
            {
                Monitor.FindBlock(runtime, lockObj)?.Exit();
            }
            else
            {
                lockObj.Exit();
            }
        }

        /// <summary>
        /// Tries to enter the lock without waiting.
        /// </summary>
        public static bool TryEnter(SystemThreading.Lock lockObj)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                runtime.TryGetExecutingOperation(out _))
            {
                // In systematic testing, always succeed to explore all interleavings.
                Monitor.LockBlock(runtime, lockObj);
                return true;
            }
            else
            {
                if (runtime.SchedulingPolicy is SchedulingPolicy.Fuzzing &&
                    runtime.TryGetExecutingOperation(out ControlledOperation current))
                {
                    runtime.DelayOperation(current);
                }

                return lockObj.TryEnter();
            }
        }

        /// <summary>
        /// Tries to enter the lock, waiting for the specified timeout.
        /// </summary>
        public static bool TryEnter(SystemThreading.Lock lockObj, int millisecondsTimeout)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                runtime.TryGetExecutingOperation(out _))
            {
                Monitor.LockBlock(runtime, lockObj);
                return true;
            }
            else
            {
                if (runtime.SchedulingPolicy is SchedulingPolicy.Fuzzing &&
                    runtime.TryGetExecutingOperation(out ControlledOperation current))
                {
                    runtime.DelayOperation(current);
                }

                return lockObj.TryEnter(millisecondsTimeout);
            }
        }

        /// <summary>
        /// Tries to enter the lock, waiting for the specified timeout.
        /// </summary>
        public static bool TryEnter(SystemThreading.Lock lockObj, TimeSpan timeout)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                runtime.TryGetExecutingOperation(out _))
            {
                Monitor.LockBlock(runtime, lockObj);
                return true;
            }
            else
            {
                if (runtime.SchedulingPolicy is SchedulingPolicy.Fuzzing &&
                    runtime.TryGetExecutingOperation(out ControlledOperation current))
                {
                    runtime.DelayOperation(current);
                }

                return lockObj.TryEnter(timeout);
            }
        }

        /// <summary>
        /// Determines whether the lock is held by the current thread.
        /// </summary>
        public static bool IsHeldByCurrentThread(SystemThreading.Lock lockObj)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                runtime.TryGetExecutingOperation(out _))
            {
                if (runtime.HasExecutionEnded)
                {
                    // During teardown the block state is unreliable; the real lock is what EnterScope
                    // actually holds, so answer from it.
                    return lockObj.IsHeldByCurrentThread;
                }

                var block = Monitor.SynchronizedBlock.Find(lockObj);
                return block != null && block.IsEntered();
            }

            return lockObj.IsHeldByCurrentThread;
        }
    }
}

#pragma warning restore CS9216
#endif

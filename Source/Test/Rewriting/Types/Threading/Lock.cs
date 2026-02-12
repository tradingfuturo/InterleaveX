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
        /// Creates a new <see cref="SystemThreading.Lock"/> instance.
        /// </summary>
        public static SystemThreading.Lock Create() => new SystemThreading.Lock();

        /// <summary>
        /// Enters the lock and returns a scope that can be disposed to exit the lock.
        /// </summary>
        public static SystemThreading.Lock.Scope EnterScope(SystemThreading.Lock lockObj)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving)
            {
                var block = Monitor.SynchronizedBlock.Lock(lockObj);
                (ScopeStack ??= new Stack<Monitor.SynchronizedBlock>()).Push(block);
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
                ScopeStack?.Count > 0)
            {
                var block = ScopeStack.Pop();
                block.Exit();
            }
        }

        /// <summary>
        /// Enters the lock.
        /// </summary>
        public static void Enter(SystemThreading.Lock lockObj)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving)
            {
                Monitor.SynchronizedBlock.Lock(lockObj);
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
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving)
            {
                var block = Monitor.SynchronizedBlock.Find(lockObj) ??
                    throw new SystemThreading.SynchronizationLockException();
                block.Exit();
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
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving)
            {
                // In systematic testing, always succeed to explore all interleavings.
                Monitor.SynchronizedBlock.Lock(lockObj);
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
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving)
            {
                Monitor.SynchronizedBlock.Lock(lockObj);
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
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving)
            {
                Monitor.SynchronizedBlock.Lock(lockObj);
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
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving)
            {
                var block = Monitor.SynchronizedBlock.Find(lockObj);
                return block != null && block.IsEntered();
            }

            return lockObj.IsHeldByCurrentThread;
        }
    }
}

#pragma warning restore CS9216
#endif

// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Coyote.Rewriting.Types.Threading.Tasks;
using Microsoft.Coyote.Runtime;
using SystemInterlocked = System.Threading.Interlocked;
using SystemSynchronizationLockException = System.Threading.SynchronizationLockException;
using SystemThreading = System.Threading;

namespace Microsoft.Coyote.Rewriting.Types.Threading
{
    /// <summary>
    /// Provides methods for monitors that can be controlled during testing.
    /// </summary>
    /// <remarks>This type is intended for compiler use rather than use directly in code.</remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class Monitor
    {
        /// <summary>
        /// Acquires an exclusive lock on the specified object.
        /// </summary>
        public static void Enter(object obj)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                runtime.TryGetExecutingOperation(out _))
            {
                LockBlock(runtime, obj);
            }
            else
            {
                if (runtime.SchedulingPolicy is SchedulingPolicy.Fuzzing &&
                    runtime.TryGetExecutingOperation(out ControlledOperation current))
                {
                    runtime.DelayOperation(current);
                }

                SystemThreading.Monitor.Enter(obj);
            }
        }

        /// <summary>
        /// Acquires an exclusive lock on the specified object.
        /// </summary>
        public static void Enter(object obj, ref bool lockTaken)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                runtime.TryGetExecutingOperation(out _))
            {
                lockTaken = LockBlock(runtime, obj)?.IsLockTaken ?? true;
            }
            else
            {
                if (runtime.SchedulingPolicy is SchedulingPolicy.Fuzzing &&
                    runtime.TryGetExecutingOperation(out ControlledOperation current))
                {
                    runtime.DelayOperation(current);
                }

                SystemThreading.Monitor.Enter(obj, ref lockTaken);
            }
        }

        /// <summary>
        /// Releases an exclusive lock on the specified object.
        /// </summary>
        public static void Exit(object obj)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                runtime.TryGetExecutingOperation(out _))
            {
                FindBlock(runtime, obj)?.Exit();
            }
            else
            {
                SystemThreading.Monitor.Exit(obj);
            }
        }

        /// <summary>
        /// Determines whether the current thread holds the lock on the specified object.
        /// </summary>
        public static bool IsEntered(object obj)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                runtime.TryGetExecutingOperation(out _))
            {
                return FindBlock(runtime, obj)?.IsEntered() ?? false;
            }

            return SystemThreading.Monitor.IsEntered(obj);
        }

        /// <summary>
        /// Notifies a thread in the waiting queue of a change in the locked object's state.
        /// </summary>
        public static void Pulse(object obj)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                runtime.TryGetExecutingOperation(out _))
            {
                FindBlock(runtime, obj)?.Pulse();
            }
            else
            {
                SystemThreading.Monitor.Pulse(obj);
            }
        }

        /// <summary>
        /// Notifies all waiting threads of a change in the object's state.
        /// </summary>
        public static void PulseAll(object obj)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                runtime.TryGetExecutingOperation(out _))
            {
                FindBlock(runtime, obj)?.PulseAll();
            }
            else
            {
                SystemThreading.Monitor.PulseAll(obj);
            }
        }

        /// <summary>
        /// Attempts, for the specified amount of time, to acquire an exclusive lock on the specified object,
        /// and atomically sets a value that indicates whether the lock was taken.
        /// </summary>
        public static void TryEnter(object obj, TimeSpan timeout, ref bool lockTaken)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                runtime.TryGetExecutingOperation(out _))
            {
                // TODO: how to implement this timeout?
                lockTaken = LockBlock(runtime, obj)?.IsLockTaken ?? true;
            }
            else
            {
                if (runtime.SchedulingPolicy is SchedulingPolicy.Fuzzing &&
                    runtime.TryGetExecutingOperation(out ControlledOperation current))
                {
                    runtime.DelayOperation(current);
                }

                SystemThreading.Monitor.TryEnter(obj, timeout, ref lockTaken);
            }
        }

        /// <summary>
        /// Attempts, for the specified amount of time, to acquire an exclusive lock on the specified object,
        /// and atomically sets a value that indicates whether the lock was taken.
        /// </summary>
        public static bool TryEnter(object obj, TimeSpan timeout)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                runtime.TryGetExecutingOperation(out _))
            {
                // TODO: how to implement this timeout?
                return LockBlock(runtime, obj)?.IsLockTaken ?? true;
            }
            else if (runtime.SchedulingPolicy is SchedulingPolicy.Fuzzing &&
                runtime.TryGetExecutingOperation(out ControlledOperation current))
            {
                runtime.DelayOperation(current);
            }

            return SystemThreading.Monitor.TryEnter(obj, timeout);
        }

        /// <summary>
        /// Attempts, for the specified number of milliseconds, to acquire an exclusive lock on the specified object,
        /// and atomically sets a value that indicates whether the lock was taken.
        /// </summary>
        public static void TryEnter(object obj, int millisecondsTimeout, ref bool lockTaken)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                runtime.TryGetExecutingOperation(out _))
            {
                // TODO: how to implement this timeout?
                lockTaken = LockBlock(runtime, obj)?.IsLockTaken ?? true;
            }
            else
            {
                if (runtime.SchedulingPolicy is SchedulingPolicy.Fuzzing &&
                    runtime.TryGetExecutingOperation(out ControlledOperation current))
                {
                    runtime.DelayOperation(current);
                }

                SystemThreading.Monitor.TryEnter(obj, millisecondsTimeout, ref lockTaken);
            }
        }

        /// <summary>
        /// Attempts to acquire an exclusive lock on the specified object, and atomically
        /// sets a value that indicates whether the lock was taken.
        /// </summary>
        public static void TryEnter(object obj, ref bool lockTaken)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                runtime.TryGetExecutingOperation(out _))
            {
                // TODO: how to implement this timeout?
                lockTaken = LockBlock(runtime, obj)?.IsLockTaken ?? true;
            }
            else
            {
                if (runtime.SchedulingPolicy is SchedulingPolicy.Fuzzing &&
                    runtime.TryGetExecutingOperation(out ControlledOperation current))
                {
                    runtime.DelayOperation(current);
                }

                SystemThreading.Monitor.TryEnter(obj, ref lockTaken);
            }
        }

        /// <summary>
        /// Attempts to acquire an exclusive lock on the specified object.
        /// </summary>
        public static bool TryEnter(object obj)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                runtime.TryGetExecutingOperation(out _))
            {
                return LockBlock(runtime, obj)?.IsLockTaken ?? true;
            }
            else if (runtime.SchedulingPolicy is SchedulingPolicy.Fuzzing &&
                runtime.TryGetExecutingOperation(out ControlledOperation current))
            {
                runtime.DelayOperation(current);
            }

            return SystemThreading.Monitor.TryEnter(obj);
        }

        /// <summary>
        /// Releases the lock on an object and blocks the current thread until it reacquires the lock.
        /// </summary>
        public static bool Wait(object obj)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                runtime.TryGetExecutingOperation(out _))
            {
                return FindBlock(runtime, obj)?.Wait() ?? true;
            }

            return SystemThreading.Monitor.Wait(obj);
        }

        /// <summary>
        /// Releases the lock on an object and blocks the current thread until it reacquires the lock.
        /// If the specified time-out interval elapses, the thread enters the ready queue.
        /// </summary>
        public static bool Wait(object obj, int millisecondsTimeout)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                runtime.TryGetExecutingOperation(out _))
            {
                return FindBlock(runtime, obj)?.Wait(millisecondsTimeout) ?? true;
            }

            return SystemThreading.Monitor.Wait(obj, millisecondsTimeout);
        }

        /// <summary>
        /// Releases the lock on an object and blocks the current thread until it reacquires the lock. If the
        /// specified time-out interval elapses, the thread enters the ready queue. This method also specifies
        /// whether the synchronization domain for the context (if in a synchronized context) is exited before
        /// the wait and reacquired afterward.
        /// </summary>
        public static bool Wait(object obj, int millisecondsTimeout, bool exitContext)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                runtime.TryGetExecutingOperation(out _))
            {
                // TODO: implement exitContext.
                return FindBlock(runtime, obj)?.Wait(millisecondsTimeout) ?? true;
            }

            return SystemThreading.Monitor.Wait(obj, millisecondsTimeout, exitContext);
        }

        /// <summary>
        /// Releases the lock on an object and blocks the current thread until it reacquires the lock.
        /// If the specified time-out interval elapses, the thread enters the ready queue.
        /// </summary>
        public static bool Wait(object obj, TimeSpan timeout)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                runtime.TryGetExecutingOperation(out _))
            {
                return FindBlock(runtime, obj)?.Wait(timeout) ?? true;
            }

            return SystemThreading.Monitor.Wait(obj, timeout);
        }

        /// <summary>
        /// Releases the lock on an object and blocks the current thread until it reacquires the lock.
        /// If the specified time-out interval elapses, the thread enters the ready queue. Optionally
        /// exits the synchronization domain for the synchronized context before the wait and reacquires
        /// the domain afterward.
        /// </summary>
        public static bool Wait(object obj, TimeSpan timeout, bool exitContext)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                runtime.TryGetExecutingOperation(out _))
            {
                // TODO: implement exitContext.
                return FindBlock(runtime, obj)?.Wait(timeout) ?? true;
            }

            return SystemThreading.Monitor.Wait(obj, timeout, exitContext);
        }

        /// <summary>
        /// Acquires the synchronized block for the specified object on behalf of the executing
        /// operation, or returns null without touching the block cache if the specified runtime has
        /// stopped executing its test iteration.
        /// </summary>
        /// <remarks>
        /// When an iteration detaches, operations that were executing user code keep running until
        /// their interrupt is delivered, while the testing engine concurrently disposes the runtime and
        /// clears the block cache for the next iteration. Such an operation must not create or acquire
        /// blocks: a block it creates would pollute the cache the next iteration is about to use, and a
        /// block it finds may already belong to that iteration. Callers treat null as an immediately
        /// successful acquisition, which lets the unwinding code run to its next interruptible point
        /// instead of failing with an exception the program under test never caused.
        /// </remarks>
        internal static SynchronizedBlock LockBlock(CoyoteRuntime runtime, object obj) =>
            SynchronizedBlock.Lock(runtime, obj);

        /// <summary>
        /// Finds the synchronized block for the specified object on behalf of the executing operation,
        /// throwing if none exists, or returns null without touching the block cache if the specified
        /// runtime has stopped executing its test iteration.
        /// </summary>
        /// <remarks>
        /// The teardown check deliberately happens after the lookup: the engine changes the execution
        /// status before it clears the block cache, so an operation that finds its block missing due to
        /// teardown is guaranteed to observe the ended status here, rather than throwing
        /// <see cref="SystemThreading.SynchronizationLockException"/> into the program under test.
        /// Callers treat null as an immediately successful no-op. See <see cref="LockBlock"/>.
        /// </remarks>
        internal static SynchronizedBlock FindBlock(CoyoteRuntime runtime, object obj)
        {
            var block = SynchronizedBlock.FindForRuntime(runtime, obj);
            if (runtime.HasExecutionEnded)
            {
                return null;
            }

            if (block is null)
            {
                throw new SystemThreading.SynchronizationLockException();
            }

            return block;
        }

        /// <summary>
        /// Provides a mechanism that synchronizes access to objects.
        /// </summary>
        internal class SynchronizedBlock : IDisposable
        {
            /// <summary>
            /// Cache from synchronized objects to synchronized block instances.
            /// </summary>
            private static readonly ConcurrentDictionary<object, Lazy<SynchronizedBlock>> Cache =
                new ConcurrentDictionary<object, Lazy<SynchronizedBlock>>();

            /// <summary>
            /// How many times acquiring a block discards one left behind by an ended iteration before
            /// giving up and reporting that the cache is not converging.
            /// </summary>
            private const int HealAttempts = 16;

            /// <summary>
            /// The id of the <see cref="CoyoteRuntime"/> that created this semaphore.
            /// </summary>
            private readonly Guid RuntimeId;

            /// <summary>
            /// The resource id of this handle.
            /// </summary>
            protected readonly Guid ResourceId;

            /// <summary>
            /// The object used for synchronization.
            /// </summary>
            private readonly object SyncObject;

            /// <summary>
            /// True if the lock was taken, else false.
            /// </summary>
            internal bool IsLockTaken;

            /// <summary>
            /// The current owner of this synchronization object.
            /// </summary>
            private ControlledOperation Owner;

            /// <summary>
            /// Wait queue of asynchronous operations.
            /// </summary>
            private readonly List<ControlledOperation> WaitQueue;

            /// <summary>
            /// Ready queue of asynchronous operations.
            /// </summary>
            private readonly List<ControlledOperation> ReadyQueue;

            /// <summary>
            /// Queue of nondeterministically buffered pulse operations to be performed after releasing
            /// the lock. This allows modeling delayed pulse operations by the operation system.
            /// </summary>
            private readonly Queue<PulseOperation> PulseQueue;

            /// <summary>
            /// The number of times that the lock has been acquired per owner. The lock can only
            /// be acquired more than one times by the same owner. A count > 1 indicates that the
            /// invocation by the current owner is reentrant.
            /// </summary>
            private readonly Dictionary<ControlledOperation, int> LockCountMap;

            /// <summary>
            /// Used to reference count accesses to this synchronized block
            /// so that it can be removed from the cache.
            /// </summary>
            private int UseCount;

            /// <summary>
            /// The debug name of this semaphore.
            /// </summary>
            private readonly string DebugName;

            /// <summary>
            /// Initializes a new instance of the <see cref="SynchronizedBlock"/> class.
            /// </summary>
            private SynchronizedBlock(CoyoteRuntime runtime, object syncObject)
            {
                if (syncObject is null)
                {
                    throw new ArgumentNullException(nameof(syncObject));
                }

                this.RuntimeId = runtime.Id;
                this.ResourceId = Guid.NewGuid();
                this.SyncObject = syncObject;
                this.WaitQueue = new List<ControlledOperation>();
                this.ReadyQueue = new List<ControlledOperation>();
                this.PulseQueue = new Queue<PulseOperation>();
                this.LockCountMap = new Dictionary<ControlledOperation, int>();
                this.UseCount = 0;
                this.DebugName = $"lock({this.ResourceId})";
            }

            /// <summary>
            /// Creates a new <see cref="SynchronizedBlock"/> for synchronizing access
            /// to the specified object and enters the lock.
            /// </summary>
            internal static SynchronizedBlock Lock(object syncObject) =>
                Lock(CoyoteRuntime.Current, syncObject);

            /// <summary>
            /// Creates a new <see cref="SynchronizedBlock"/> for synchronizing access to the specified
            /// object on behalf of the specified runtime, and enters the lock, or returns null if that
            /// runtime has stopped executing its test iteration.
            /// </summary>
            internal static SynchronizedBlock Lock(CoyoteRuntime runtime, object syncObject) =>
                Resolve(runtime, syncObject, create: true)?.EnterLock();

            /// <summary>
            /// Finds the synchronized block associated with the specified synchronization object.
            /// </summary>
            internal static SynchronizedBlock Find(object syncObject) =>
                Cache.TryGetValue(syncObject, out Lazy<SynchronizedBlock> lazyMock) ? lazyMock.Value : null;

            /// <summary>
            /// Finds the synchronized block associated with the specified synchronization object on
            /// behalf of the specified runtime, discarding any block left behind by an iteration that has
            /// already ended and looking again, so that discarding one never reports a miss on an object
            /// this iteration does hold a block for.
            /// </summary>
            internal static SynchronizedBlock FindForRuntime(CoyoteRuntime runtime, object syncObject) =>
                Resolve(runtime, syncObject, create: false);

            /// <summary>
            /// Returns the synchronized block of the specified object for the specified runtime,
            /// creating and caching one if requested, or null if there is none and
            /// <paramref name="create"/> is false, or if the runtime has stopped executing its test
            /// iteration.
            /// </summary>
            /// <remarks>
            /// Any block left behind by an iteration that has already ended is discarded and the
            /// lookup retried, because reporting a miss immediately after discarding an entry would be
            /// wrong whenever this iteration installed its own entry for the object in the meantime:
            /// the discard is matched on the value that was read, and so does nothing at all in
            /// exactly that case. The retry is bounded only to keep a corrupted cache from spinning
            /// forever. Each thread can leave at most one stale entry behind, because publishing it
            /// synchronizes with the reset that preceded it, so the next attempt by that thread
            /// observes the ended status up front.
            /// </remarks>
            private static SynchronizedBlock Resolve(CoyoteRuntime runtime, object syncObject, bool create)
            {
                for (int attempt = 0; attempt < HealAttempts; attempt++)
                {
                    if (!Cache.TryGetValue(syncObject, out Lazy<SynchronizedBlock> lazyBlock))
                    {
                        if (!create)
                        {
                            return null;
                        }

                        // The block is created for the runtime that is acquiring it, rather than for
                        // whichever runtime happens to be current on the thread that first forces the
                        // entry, so that ownership of a block is always the ownership the check below
                        // assumes. The lookup above only skips building a factory that a hit would
                        // discard anyway, as GetOrAdd itself starts by looking the key up without
                        // taking a bucket lock.
                        lazyBlock = Cache.GetOrAdd(syncObject,
                            key => new Lazy<SynchronizedBlock>(() => new SynchronizedBlock(runtime, key)));
                    }

                    SynchronizedBlock block = lazyBlock.Value;
                    if (create && runtime.HasExecutionEnded)
                    {
                        // Checked after publishing rather than before, which is what makes it decisive:
                        // adding to the cache takes a bucket lock that the clearing reset also took, so a
                        // block that was added after the reset is guaranteed to observe the ended status
                        // that was written before it. A block added before the reset is removed by it.
                        // A lookup deliberately does not check, so that its caller can tell teardown
                        // apart from a genuine miss afterwards; see FindBlock.
                        if (block.RuntimeId == runtime.Id)
                        {
                            TryEvict(syncObject, lazyBlock);
                        }

                        return null;
                    }

                    if (block.RuntimeId != runtime.Id && IsAbandoned(block.RuntimeId))
                    {
                        // Left behind by an iteration that has ended. The undo above is not guaranteed to
                        // run, because the interrupt that terminates such an operation can be raised
                        // inside the cache itself, so entries are also healed here on the way in.
                        TryEvict(syncObject, lazyBlock);
                        continue;
                    }

                    return block;
                }

                // Reported rather than returned as a miss, which the caller would raise into the program
                // under test as a lock that was never taken, blaming it for an internal invariant.
                runtime.NotifyAssertionFailure(
                    $"Unable to {(create ? "acquire" : "look up")} the synchronized block for an object " +
                    $"after {HealAttempts} attempts, because the block cache keeps being repopulated " +
                    "with blocks from test iterations that have already ended.");
                return null;
            }

            /// <summary>
            /// Returns true if the runtime with the specified id is no longer running a test iteration.
            /// </summary>
            private static bool IsAbandoned(Guid runtimeId) =>
                !RuntimeProvider.TryGetFromId(runtimeId, out CoyoteRuntime owner) || owner.HasExecutionEnded;

            /// <summary>
            /// Removes the specified cache entry, unless it has already been replaced.
            /// </summary>
            /// <remarks>
            /// Matching on the value matters: removing by key alone can delete an entry that the next
            /// test iteration has already installed for the same object, which would leave that iteration
            /// with two blocks for one lock and silently stop it from enforcing mutual exclusion.
            /// </remarks>
            private static void TryEvict(object syncObject, Lazy<SynchronizedBlock> lazyBlock) =>
                (Cache as ICollection<KeyValuePair<object, Lazy<SynchronizedBlock>>>).Remove(
                    new KeyValuePair<object, Lazy<SynchronizedBlock>>(syncObject, lazyBlock));

            /// <summary>
            /// Resets the cache. This should be called after each testing iteration
            /// to prevent orphaned entries from persisting across iterations.
            /// </summary>
            internal static void ResetCache() => Cache.Clear();

            /// <summary>
            /// Determines whether the current thread holds the lock on the sync object.
            /// </summary>
            internal bool IsEntered()
            {
                if (this.Owner != null)
                {
                    CoyoteRuntime runtime = this.GetRuntime();
                    var op = runtime.GetExecutingOperation();
                    return this.Owner == op;
                }

                return false;
            }

            private SynchronizedBlock EnterLock()
            {
                CoyoteRuntime runtime = this.GetRuntime();
                this.IsLockTaken = true;
                SystemInterlocked.Increment(ref this.UseCount);

                if (runtime.Configuration.IsLockAccessRaceCheckingEnabled && this.Owner is null)
                {
                    // If this operation is trying to acquire this lock while it is free, then inject a scheduling
                    // point to give another enabled operation the chance to race and acquire this lock.
                    runtime.ScheduleNextOperation(default, SchedulingPointType.Acquire);
                }

                if (this.Owner != null)
                {
                    var op = runtime.GetExecutingOperation();
                    if (this.Owner == op)
                    {
                        // The owner is re-entering the lock.
                        this.LockCountMap[op]++;
                        return this;
                    }
                    else
                    {
                        // Another op has the lock right now, so add the executing op
                        // to the ready queue and block it.
                        this.WaitQueue.Remove(op);
                        if (!this.ReadyQueue.Contains(op))
                        {
                            this.ReadyQueue.Add(op);
                        }

                        // Pause this operation and schedule the next enabled operation.
                        op.PauseWithResource(this.ResourceId);
                        runtime.ScheduleNextOperation(op, SchedulingPointType.Pause);

                        // This operation can finally take the lock.
                        this.LockCountMap.Add(op, 1);
                        return this;
                    }
                }

                // The executing op acquired the lock and can proceed.
                this.Owner = runtime.GetExecutingOperation();
                this.LockCountMap.Add(this.Owner, 1);
                return this;
            }

            /// <summary>
            /// Notifies a thread in the waiting queue of a change in the locked object's state.
            /// </summary>
            internal void Pulse() => this.Pulse(PulseOperation.Next);

            /// <summary>
            /// Notifies all waiting threads of a change in the object's state.
            /// </summary>
            internal void PulseAll() => this.Pulse(PulseOperation.All);

            /// <summary>
            /// Invokes the specified pulse operation.
            /// </summary>
            private void Pulse(PulseOperation pulseOperation)
            {
                CoyoteRuntime runtime = this.GetRuntime();
                var op = runtime.GetExecutingOperation();
                if (this.Owner != op)
                {
                    throw new SystemSynchronizationLockException();
                }

                if (runtime.Configuration.IsLockAccessRaceCheckingEnabled)
                {
                    // Pulse can be delayed by the operating system, so simulate this by scheduling the pulse
                    // operation to execute either immediately or after the current owner releases the lock.
                    this.PulseQueue.Enqueue(pulseOperation);
                    if (this.PulseQueue.Count is 1)
                    {
                        // Create a task for draining the queue. To optimize for testing performance,
                        // we create and maintain a single task to perform this role.
                        Task.Run(() =>
                        {
                            while (this.PulseQueue.Count > 0)
                            {
                                var pulseOperation = this.PulseQueue.Dequeue();
                                runtime.ScheduleNextOperation(default, SchedulingPointType.Default);
                                this.Pulse(runtime, pulseOperation);
                            }
                        });
                    }
                }
                else
                {
                    this.Pulse(runtime, pulseOperation);
                }
            }

            /// <summary>
            /// Invokes the specified pulse operation.
            /// </summary>
            private void Pulse(CoyoteRuntime runtime, PulseOperation pulseOperation)
            {
                if (pulseOperation is PulseOperation.Next)
                {
                    if (this.WaitQueue.Count > 0)
                    {
                        // System.Threading.Monitor has FIFO semantics.
                        var waitingOp = this.WaitQueue[0];
                        this.WaitQueue.RemoveAt(0);
                        this.ReadyQueue.Add(waitingOp);
                        runtime.LogWriter.LogDebug("[coyote::debug] Operation '{0}' is pulsed by thread '{1}'.",
                            waitingOp.Id, SystemThreading.Thread.CurrentThread.ManagedThreadId);
                    }
                }
                else
                {
                    foreach (var waitingOp in this.WaitQueue)
                    {
                        this.ReadyQueue.Add(waitingOp);
                        runtime.LogWriter.LogDebug("[coyote::debug] Operation '{0}' is pulsed by thread '{1}'.",
                            waitingOp.Id, SystemThreading.Thread.CurrentThread.ManagedThreadId);
                    }

                    this.WaitQueue.Clear();
                }

                if (this.Owner is null)
                {
                    this.UnlockNextReady();
                }
            }

            /// <summary>
            /// Releases the lock on an object and blocks the current thread until it reacquires
            /// the lock.
            /// </summary>
            internal bool Wait()
            {
                CoyoteRuntime runtime = this.GetRuntime();
                var op = runtime.GetExecutingOperation();
                if (this.Owner != op)
                {
                    throw new SystemSynchronizationLockException();
                }

                this.ReadyQueue.Remove(op);
                if (!this.WaitQueue.Contains(op))
                {
                    this.WaitQueue.Add(op);
                }

                this.UnlockNextReady();

                // Pause this operation and schedule the next enabled operation.
                op.PauseWithResource(this.ResourceId);
                runtime.LogWriter.LogDebug("[coyote::debug] Operation '{0}' is waiting on thread '{1}'.",
                    op.Id, SystemThreading.Thread.CurrentThread.ManagedThreadId);
                runtime.ScheduleNextOperation(op, SchedulingPointType.Pause);
                return true;
            }

            /// <summary>
            /// Releases the lock on an object and blocks the current thread until it reacquires
            /// the lock. If the specified time-out interval elapses, the thread enters the ready
            /// queue.
            /// </summary>
#pragma warning disable CA1801 // Parameter not used
            internal bool Wait(int millisecondsTimeout)
            {
                // TODO: how to implement timeout?
                // This is a bit more tricky to model, one way is to have a loop that checks
                // for controlled random boolean choice, and if it becomes true then it fails
                // the wait. This would be similar to timers in actors, so we want to use a
                // lower probability to not fail very frequently during systematic testing.
                // In the future we might want to introduce a RandomTimeout choice (similar to
                // RandomBoolean and RandomInteger), with the benefit being that the underlying
                // testing strategy will know that this is a timeout and perhaps treat it in a
                // more intelligent manner, but for now piggybacking on the other randoms should
                // work (as long as its not with a high probability).
                return this.Wait();
            }
#pragma warning restore CA1801 // Parameter not used

            /// <summary>
            /// Releases the lock on an object and blocks the current thread until it reacquires
            /// the lock. If the specified time-out interval elapses, the thread enters the ready
            /// queue.
            /// </summary>
#pragma warning disable CA1801 // Parameter not used
            internal bool Wait(TimeSpan timeout)
            {
                // TODO: how to implement timeout?
                return this.Wait();
            }
#pragma warning restore CA1801 // Parameter not used

            /// <summary>
            /// Assigns the lock to the next operation waiting in the ready queue, if there is one,
            /// following the FIFO semantics of monitor.
            /// </summary>
            private void UnlockNextReady()
            {
                // Preparing to unlock so give up ownership.
                this.Owner = null;
                if (this.ReadyQueue.Count > 0)
                {
                    // If there is a operation waiting in the ready queue, then awake it.
                    ControlledOperation op = this.ReadyQueue[0];
                    op.TryEnable(this.ResourceId);
                    this.ReadyQueue.RemoveAt(0);
                    this.Owner = op;
                }
            }

            internal void Exit()
            {
                CoyoteRuntime runtime = this.GetRuntime();
                var op = runtime.GetExecutingOperation();
                runtime.Assert(this.LockCountMap.ContainsKey(op),
                    "Cannot invoke Dispose without acquiring the lock.");

                this.LockCountMap[op]--;
                if (this.LockCountMap[op] is 0)
                {
                    // Only release the lock if the invocation is not reentrant.
                    this.LockCountMap.Remove(op);
                    this.UnlockNextReady();
                }

                int useCount = SystemInterlocked.Decrement(ref this.UseCount);
                if (useCount is 0 &&
                    Cache.TryGetValue(this.SyncObject, out Lazy<SynchronizedBlock> lazyBlock) &&
                    lazyBlock.IsValueCreated && ReferenceEquals(lazyBlock.Value, this))
                {
                    // It is safe to remove this instance from the cache. The entry is matched by value
                    // rather than by key, so that an entry a later iteration has already installed for
                    // this object is not removed, and it is only inspected if its block has been created,
                    // so that a block belonging to another iteration is not created by this one.
                    TryEvict(this.SyncObject, lazyBlock);
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

            /// <summary>
            /// Releases resources used by the synchronized block.
            /// </summary>
            protected void Dispose(bool disposing)
            {
                if (disposing)
                {
                    this.Exit();
                }
            }

            /// <summary>
            /// Releases resources used by the synchronized block.
            /// </summary>
            public void Dispose()
            {
                this.Dispose(true);
                GC.SuppressFinalize(this);
            }

            /// <summary>
            /// The type of a pulse operation.
            /// </summary>
            private enum PulseOperation
            {
                /// <summary>
                /// Pulses the next waiting operation.
                /// </summary>
                Next,

                /// <summary>
                /// Pulses all waiting operations.
                /// </summary>
                All
            }
        }
    }
}

// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Microsoft.Coyote.Runtime
{
    /// <summary>
    /// Pool of reusable threads that execute controlled operations during testing.
    /// </summary>
    /// <remarks>
    /// Creating a thread costs far more than executing the short operations that most tests produce, and
    /// a test creates one operation per task and per continuation in every iteration. The pool amortizes
    /// that cost by parking a thread once its operation completes, so that a later operation, including
    /// one from a later testing iteration, can reuse it.
    ///
    /// The pool outlives any single <see cref="CoyoteRuntime"/>, because a runtime is created per testing
    /// iteration and reuse across iterations is the point. It is therefore a process-wide singleton, and
    /// is bounded by <see cref="MaxIdleThreads"/> and by <see cref="Drain"/>.
    ///
    /// Only threads whose operation is never observed by the program under test may be pooled. A thread
    /// created through <see cref="CoyoteRuntime.CreateControlledThread"/> is handed to user code, which
    /// can observe its identity and its state, so it must terminate when its operation completes and is
    /// never pooled.
    ///
    /// Pooling accelerates iteration teardown into a race that predates it: when an iteration
    /// detaches, operations executing user code are not joined — their interrupt latches and they keep
    /// running briefly while the testing engine disposes the runtime and clears the static
    /// SynchronizedBlock cache for the next iteration. Historically the boundary was slowed by thread
    /// creation, so those stragglers effectively always finished first; with pooling the boundary
    /// reliably lands inside their window on workloads that detach mid-operation, such as short
    /// deadlock timeouts over partially controlled WPF code. The rewritten synchronization shims
    /// therefore recognize a runtime whose execution has ended and stop touching shared
    /// synchronization state on its behalf (see Monitor.LockBlock and Monitor.FindBlock in the test
    /// assembly), which is what makes pooling safe to enable by default. A full quiescence barrier is
    /// deliberately not used: controlled threads can be permanently parked inside uncontrolled calls
    /// such as a message pump, so no join can be both sound and bounded.
    /// </remarks>
    internal sealed class ControlledThreadPool
    {
        /// <summary>
        /// The singleton instance of this pool.
        /// </summary>
        internal static readonly ControlledThreadPool Instance = new ControlledThreadPool();

        /// <summary>
        /// The maximum number of parked threads retained by this pool.
        /// </summary>
        /// <remarks>
        /// Under <see cref="SchedulingPolicy.Interleaving"/> only one operation runs at a time, but every
        /// live operation holds a parked thread, so the number of threads a test needs tracks its peak
        /// concurrent operation count rather than its core count. This bound is therefore set well above
        /// what a typical test reaches, so that reuse is not silently lost on tests with many operations.
        /// </remarks>
        internal const int MaxIdleThreads = 1024;

        /// <summary>
        /// The threads that are currently parked and available for reuse.
        /// </summary>
        /// <remarks>
        /// This can also contain retired threads, which <see cref="Rent"/> discards. A thread retires
        /// itself without removing itself from this bag, because doing so would require a scan.
        /// </remarks>
        private readonly ConcurrentBag<PooledThread> IdleThreads;

        /// <summary>
        /// The number of threads that have been added to <see cref="IdleThreads"/> and not yet taken.
        /// </summary>
        private int IdleCount;

        /// <summary>
        /// The total number of threads this pool has created.
        /// </summary>
        private static long CreatedCount;

        /// <summary>
        /// Initializes a new instance of the <see cref="ControlledThreadPool"/> class.
        /// </summary>
        private ControlledThreadPool()
        {
            this.IdleThreads = new ConcurrentBag<PooledThread>();
        }

        /// <summary>
        /// The total number of threads this pool has created. Used to verify that reuse is happening.
        /// </summary>
        internal static long ThreadsCreated => Interlocked.Read(ref CreatedCount);

        /// <summary>
        /// The number of threads currently available for reuse.
        /// </summary>
        internal int IdleThreadCount => Volatile.Read(ref this.IdleCount);

        /// <summary>
        /// Returns a thread that is reserved for the caller, creating one if none is available.
        /// </summary>
        internal PooledThread Rent()
        {
            while (this.IdleThreads.TryTake(out PooledThread worker))
            {
                Interlocked.Decrement(ref this.IdleCount);
                if (worker.TryAssign())
                {
                    return worker;
                }

                // The thread retired itself before it could be assigned, so it is a tombstone that is
                // only reachable from this bag. Dropping it here is what removes it.
            }

            Interlocked.Increment(ref CreatedCount);
            return new PooledThread(this);
        }

        /// <summary>
        /// Parks the specified thread for reuse and returns true, or returns false if this pool is full,
        /// in which case the caller must retire the thread.
        /// </summary>
        /// <remarks>
        /// It is assumed that the caller is the thread being parked, and that it has already reset its
        /// signal and marked itself idle, so that an operation assigned to it between now and it waiting
        /// is not missed.
        /// </remarks>
        internal bool TryPark(PooledThread worker)
        {
            if (Interlocked.Increment(ref this.IdleCount) > MaxIdleThreads)
            {
                Interlocked.Decrement(ref this.IdleCount);
                return false;
            }

            this.IdleThreads.Add(worker);
            return true;
        }

        /// <summary>
        /// Retires every thread that is currently parked. Threads that are executing an operation are
        /// left alone, and retire themselves once that operation completes.
        /// </summary>
        internal void Drain()
        {
            while (this.IdleThreads.TryTake(out PooledThread worker))
            {
                Interlocked.Decrement(ref this.IdleCount);
                worker.Retire();
            }
        }
    }

    /// <summary>
    /// A thread that executes controlled operations on behalf of a <see cref="ControlledThreadPool"/>.
    /// </summary>
    internal sealed class PooledThread
    {
        /// <summary>
        /// This thread is parked and can be assigned an operation.
        /// </summary>
        private const int IdleState = 0;

        /// <summary>
        /// This thread has been reserved and will be given an operation to execute.
        /// </summary>
        private const int AssignedState = 1;

        /// <summary>
        /// This thread is terminating and must never be assigned an operation.
        /// </summary>
        private const int RetiredState = 2;

        /// <summary>
        /// The pool that owns this thread.
        /// </summary>
        private readonly ControlledThreadPool Pool;

        /// <summary>
        /// Signals this thread that an operation has been assigned to it, or that it must retire.
        /// </summary>
        /// <remarks>
        /// Spinning is disabled because a pool of this size can have many threads parked at once, and
        /// each is waiting on an operation that will not be assigned for an unbounded amount of time.
        /// </remarks>
        private readonly ManualResetEventSlim SignalEvent;

        /// <summary>
        /// The operation to execute, which returns true if this thread can be reused afterwards.
        /// </summary>
        private Func<bool> WorkItem;

        /// <summary>
        /// The current state of this thread.
        /// </summary>
        private int State;

        /// <summary>
        /// Initializes a new instance of the <see cref="PooledThread"/> class and starts it.
        /// </summary>
        /// <remarks>
        /// The new thread is created in the assigned state, because the caller is renting it.
        /// </remarks>
        internal PooledThread(ControlledThreadPool pool)
        {
            this.Pool = pool;
            this.SignalEvent = new ManualResetEventSlim(false, 0);
            this.State = AssignedState;

            var thread = new Thread(this.RunLoop);

            // The name is the token that associates this thread with the operation it is executing, and
            // it cannot be reassigned on all supported frameworks, so it is set once and kept for life.
            // The association itself is remapped per operation by the runtime.
            this.Name = Guid.NewGuid().ToString();
            thread.Name = this.Name;
            thread.IsBackground = true;
            this.OSThread = thread;

            // Do not capture the execution context of whichever operation happened to create this
            // thread, because this thread will outlive it. Instead, each work item runs under the
            // execution context that its own creator captured at dispatch time (see
            // CoyoteRuntime.RunOnControlledThread), which is the same ambient state a dedicated
            // thread would have inherited from Thread.Start, and any changes an operation makes to
            // that context are discarded when the operation completes.
            using (ExecutionContext.SuppressFlow())
            {
                thread.Start();
            }
        }

        /// <summary>
        /// The underlying thread, which the runtime interrupts when it detaches.
        /// </summary>
        internal Thread OSThread { get; }

        /// <summary>
        /// The stable name of this thread.
        /// </summary>
        internal string Name { get; }

        /// <summary>
        /// Tries to reserve this thread, and returns false if it has retired itself.
        /// </summary>
        internal bool TryAssign() =>
            Interlocked.CompareExchange(ref this.State, AssignedState, IdleState) is IdleState;

        /// <summary>
        /// Assigns the specified operation to this thread and signals it to execute it.
        /// </summary>
        /// <remarks>
        /// It is assumed that the caller has already reserved this thread through <see cref="TryAssign"/>,
        /// so no other thread can be assigning to it concurrently.
        /// </remarks>
        internal void Dispatch(Func<bool> workItem)
        {
            this.WorkItem = workItem;
            this.SignalEvent.Set();
        }

        /// <summary>
        /// Retires this thread if it is still parked, waking it so that it can terminate.
        /// </summary>
        /// <remarks>
        /// Does nothing if this thread has already been reserved, because the caller that reserved it
        /// owns it and is responsible for either dispatching to it or releasing it.
        /// </remarks>
        internal void Retire()
        {
            if (Interlocked.CompareExchange(ref this.State, RetiredState, IdleState) is IdleState)
            {
                this.SignalEvent.Set();
            }
        }

        /// <summary>
        /// Releases this thread without giving it an operation, terminating it.
        /// </summary>
        /// <remarks>
        /// It is assumed that the caller reserved this thread through <see cref="TryAssign"/> and has
        /// decided not to dispatch to it. The thread is waiting to be signalled and no longer reachable
        /// from the pool, so it must be woken here or it would never terminate.
        /// </remarks>
        internal void Release()
        {
            Volatile.Write(ref this.State, RetiredState);
            this.SignalEvent.Set();
        }

        /// <summary>
        /// Executes assigned operations until this thread retires.
        /// </summary>
        private void RunLoop()
        {
            try
            {
                while (true)
                {
                    // Wait for the operation this thread was reserved for. On the first pass this is the
                    // dispatch that follows construction, and afterwards it is the dispatch that follows
                    // being taken from the pool. The signal is sticky, so a dispatch that happens before
                    // this point completes the wait immediately rather than being missed.
                    this.SignalEvent.Wait();
                    Func<bool> workItem = this.WorkItem;
                    this.WorkItem = null;
                    if (workItem is null)
                    {
                        // Woken with nothing to do, which is how a parked thread is retired.
                        return;
                    }

                    if (!TryExecuteAssignedWork(workItem))
                    {
                        return;
                    }

                    // Reset the signal and mark this thread idle before publishing it, so that it is
                    // never visible to a caller of Rent in a state where a dispatch could be lost.
                    this.SignalEvent.Reset();
                    Volatile.Write(ref this.State, IdleState);
                    if (!this.Pool.TryPark(this))
                    {
                        // The pool is full. This thread was never published, so it cannot have been
                        // assigned, and can terminate without coordinating with anyone.
                        Volatile.Write(ref this.State, RetiredState);
                        return;
                    }
                }
            }
            catch (Exception)
            {
                // Terminate this thread rather than risk reusing it in an unknown state. With work-item
                // failures handled in TryExecuteAssignedWork, reaching here means the failure came from
                // the pool machinery itself, such as an interrupt delivered while parked, which no
                // caller is expected to produce. The pool creates a replacement on demand.
            }
            finally
            {
                this.WorkItem = null;
            }
        }

        /// <summary>
        /// Executes the specified operation, and returns true if this thread can be reused.
        /// </summary>
        /// <remarks>
        /// A thread whose operation was cut short by its iteration tearing down is not discarded. It is
        /// safe to reuse after draining the interrupt, and reuse matters beyond the cost of a thread:
        /// every thread that ever creates a WPF object registers a uniquely named window class whose
        /// cost is paid from the desktop heap that the whole session shares, and is only returned when
        /// the process exits. Retiring workers at every teardown made tests with frequent deadlock
        /// detaches mint thousands of such threads and exhaust that heap, failing unrelated code with
        /// Win32 error 8.
        /// </remarks>
        private static bool TryExecuteAssignedWork(Func<bool> workItem)
        {
            try
            {
                if (workItem())
                {
                    return true;
                }
            }
            catch (ThreadInterruptedException)
            {
                // The interrupt the runtime sent to terminate the operation was consumed by this very
                // exception, most often while the operation was reacquiring the runtime lock on its way
                // out. The operation is finished and its state was already cleaned by its own handlers,
                // so fall through and treat this like any other torn-down operation.
            }
            catch (Exception)
            {
                // Unknown failure: retire rather than risk reusing a thread in an unknown state.
                return false;
            }

            // The operation's iteration has been torn down. The runtime delivers at most one interrupt
            // per controlled thread, under the runtime lock that the release path also takes, so either
            // it was already consumed above or it is still latched and fires at the next blocking wait.
            // Perform one interruptible wait to drain it, after which this thread is clean.
            return DrainPendingInterrupt();
        }

        /// <summary>
        /// Consumes the interrupt that may still be latched on this thread, so that it cannot fire
        /// inside an unrelated operation after this thread is reused. Always returns true.
        /// </summary>
        private static bool DrainPendingInterrupt()
        {
            try
            {
                Thread.Sleep(1);
            }
            catch (ThreadInterruptedException)
            {
                // This is the latched interrupt being consumed, which is the point of the wait.
            }

            return true;
        }
    }
}

// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

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
        /// How long a parked thread waits for an operation before retiring itself, in milliseconds.
        /// </summary>
        /// <remarks>
        /// A drain is the primary way threads are released, but it only runs when a testing engine
        /// finishes, so without an expiry a pool that is never drained again retains its threads for the
        /// lifetime of the process. Mutable so that tests can shorten it; a thread that is already parked
        /// completes its current wait at the previous value.
        /// </remarks>
        internal static int IdleTimeoutMs = 30000;

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
        /// <remarks>
        /// Ownership rule for keeping this accurate: whoever takes a thread out of the idle state owns
        /// the matching decrement. That is <see cref="Rent"/> when its assignment wins, <see cref="Drain"/>
        /// when its retirement wins, and the thread itself when it expires. Every mutation is interlocked;
        /// <see cref="ParkLock"/> orders park against drain, it does not guard this counter.
        /// </remarks>
        private int IdleCount;

        /// <summary>
        /// Orders parking a thread against draining the pool, so that a thread cannot be added to
        /// <see cref="IdleThreads"/> in between a drain retiring the threads it found and that drain
        /// completing.
        /// </summary>
        private readonly object ParkLock;

        /// <summary>
        /// Incremented by every drain. A thread rented before a drain carries an older value and is
        /// refused when it tries to park, which is what makes <see cref="Drain"/> a barrier rather than
        /// a sweep of whatever happened to be parked at that instant.
        /// </summary>
        private long DrainEpoch;

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
            this.ParkLock = new object();
        }

        /// <summary>
        /// The total number of threads this pool has created. Used to verify that reuse is happening.
        /// </summary>
        internal static long ThreadsCreated => Interlocked.Read(ref CreatedCount);

        /// <summary>
        /// Invoked with a freshly reserved thread before the runtime hands it an operation. Null unless a
        /// test installs it to fail in that window on purpose.
        /// </summary>
        internal static Action<PooledThread> ReservationFaultInjector;

        /// <summary>
        /// Accounts for a thread that retired itself because it waited longer than the idle timeout.
        /// </summary>
        internal void OnThreadExpired() => Interlocked.Decrement(ref this.IdleCount);

        /// <summary>
        /// The number of threads currently available for reuse.
        /// </summary>
        internal int IdleThreadCount => Volatile.Read(ref this.IdleCount);

        /// <summary>
        /// Returns a thread that is reserved for the caller, creating one if none is available.
        /// </summary>
        internal PooledThread Rent()
        {
            // Read the epoch before looking at any thread. Reading it later would allow this rental to
            // reserve a thread from before a drain and then stamp it with the epoch that same drain
            // installed, which would let that thread park again after the drain completed. Reading it
            // first can only stamp a thread as older than it really is, which merely retires a thread
            // that could have been kept.
            long epoch = Interlocked.Read(ref this.DrainEpoch);

            while (this.IdleThreads.TryTake(out PooledThread worker))
            {
                if (worker.TryAssign(epoch))
                {
                    Interlocked.Decrement(ref this.IdleCount);
                    return worker;
                }

                // The thread retired itself before it could be assigned, so it is a tombstone that is
                // only reachable from this bag. Dropping it here is what removes it, and it already
                // accounted for itself when it retired.
            }

            Interlocked.Increment(ref CreatedCount);
            return new PooledThread(this, epoch);
        }

        /// <summary>
        /// Parks the specified thread for reuse and returns true, or returns false if this pool is full
        /// or the thread was rented before the most recent drain, in which case the caller must retire it.
        /// </summary>
        /// <remarks>
        /// It is assumed that the caller is the thread being parked, and that it has already reset its
        /// signal and marked itself idle, so that an operation assigned to it between now and it waiting
        /// is not missed.
        /// </remarks>
        internal bool TryPark(PooledThread worker)
        {
            lock (this.ParkLock)
            {
                if (worker.Epoch != Interlocked.Read(ref this.DrainEpoch))
                {
                    return false;
                }

                if (Interlocked.Increment(ref this.IdleCount) > MaxIdleThreads)
                {
                    Interlocked.Decrement(ref this.IdleCount);
                    return false;
                }

                this.IdleThreads.Add(worker);
                return true;
            }
        }

        /// <summary>
        /// Retires every thread that this pool holds, and guarantees that no thread rented before this
        /// call can park afterwards.
        /// </summary>
        /// <remarks>
        /// Threads that are executing an operation are not waited for, because a controlled thread can be
        /// blocked indefinitely inside an uncontrolled call, so no join would be both correct and bounded.
        /// They are instead refused when they try to park and retire themselves. The guarantee is scoped
        /// to a single testing engine: another engine renting concurrently can legitimately repopulate
        /// the pool, and its in-flight threads are retired by this drain and replaced on demand.
        /// </remarks>
        internal void Drain()
        {
            lock (this.ParkLock)
            {
                Interlocked.Increment(ref this.DrainEpoch);
                while (this.IdleThreads.TryTake(out PooledThread worker))
                {
                    if (worker.Retire())
                    {
                        Interlocked.Decrement(ref this.IdleCount);
                    }

                    // Otherwise the thread had already retired itself and accounted for itself.
                }
            }
        }
    }

    /// <summary>
    /// What a pooled thread must do once the operation assigned to it returns.
    /// </summary>
    /// <remarks>
    /// Named rather than boolean because "this thread cannot simply be reused" covers two cases that
    /// must be handled differently: an operation cut short by its iteration tearing down leaves a
    /// thread that is reusable once its latched interrupt is drained, whereas a thread that finished
    /// in an unsafe state must never run another operation.
    /// </remarks>
    internal enum WorkerDisposition
    {
        /// <summary>
        /// The operation completed normally, so this thread is clean and can be reused as is.
        /// </summary>
        Reuse,

        /// <summary>
        /// The operation was cut short by its iteration tearing down, so this thread may still carry
        /// a latched interrupt, and is reusable once that interrupt has been drained.
        /// </summary>
        Drain,

        /// <summary>
        /// This thread is in an unknown or unsafe state and must terminate.
        /// </summary>
        Retire
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
        /// The operation to execute, which returns what this thread must do once it returns.
        /// </summary>
        private Func<WorkerDisposition> WorkItem;

        /// <summary>
        /// The current state of this thread.
        /// </summary>
        private int State;

        /// <summary>
        /// The drain epoch this thread was most recently rented in. It may only park while this still
        /// matches the pool's epoch. Written by the renter before the thread can observe it, and read by
        /// the thread itself only while parking.
        /// </summary>
        internal long Epoch;

        /// <summary>
        /// Initializes a new instance of the <see cref="PooledThread"/> class and starts it.
        /// </summary>
        /// <remarks>
        /// The new thread is created in the assigned state, because the caller is renting it.
        /// </remarks>
        internal PooledThread(ControlledThreadPool pool, long epoch)
        {
            this.Pool = pool;
            this.SignalEvent = new ManualResetEventSlim(false, 0);
            this.State = AssignedState;
            this.Epoch = epoch;

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
        /// Tries to reserve this thread for the specified drain epoch, and returns false if it has
        /// retired itself.
        /// </summary>
        internal bool TryAssign(long epoch)
        {
            if (Interlocked.CompareExchange(ref this.State, AssignedState, IdleState) is IdleState)
            {
                // Safe to write plainly: this thread is now reserved, and it is parked waiting for the
                // signal that the caller sends after this returns, which publishes the write to it.
                this.Epoch = epoch;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Assigns the specified operation to this thread and signals it to execute it.
        /// </summary>
        /// <remarks>
        /// It is assumed that the caller has already reserved this thread through <see cref="TryAssign"/>,
        /// so no other thread can be assigning to it concurrently.
        /// </remarks>
        internal void Dispatch(Func<WorkerDisposition> workItem)
        {
            this.WorkItem = workItem;
            this.SignalEvent.Set();
        }

        /// <summary>
        /// Retires this thread if it is still parked, waking it so that it can terminate, and returns
        /// true if this call is the one that retired it.
        /// </summary>
        /// <remarks>
        /// Returns false if this thread has already been reserved or has already expired, because the
        /// party that took it out of the idle state owns it, including accounting for it.
        /// </remarks>
        internal bool Retire()
        {
            if (Interlocked.CompareExchange(ref this.State, RetiredState, IdleState) is IdleState)
            {
                this.SignalEvent.Set();
                return true;
            }

            return false;
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
        /// Retires this thread because it waited for an operation for longer than the idle timeout, and
        /// returns true if it may now terminate.
        /// </summary>
        /// <remarks>
        /// Returns false when a caller reserved this thread while it was timing out. That caller has
        /// already sent, or is about to send, a signal carrying an operation, and dropping it would leave
        /// an operation that never starts, which the runtime reports as a hang rather than as a bug in
        /// this pool. The reservation is therefore binding, and this thread must run that operation.
        /// </remarks>
        private bool TryExpire()
        {
            if (Interlocked.CompareExchange(ref this.State, RetiredState, IdleState) is IdleState)
            {
                // This thread stays in the pool's bag as a tombstone that Rent discards, so account for
                // it here: the party that takes a thread out of the idle state owns its accounting.
                this.Pool.OnThreadExpired();
                return true;
            }

            return false;
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
                    if (!this.SignalEvent.Wait(ControlledThreadPool.IdleTimeoutMs))
                    {
                        if (this.TryExpire())
                        {
                            return;
                        }

                        // This thread waited long enough to expire but lost the race to a caller that was
                        // assigning to it. That caller has sent, or is about to send, the signal, so wait
                        // for it without a timeout and without resetting: resetting here would discard a
                        // signal that has already been sent.
                        this.SignalEvent.Wait();
                    }

                    Func<WorkerDisposition> workItem = this.WorkItem;
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
        ///
        /// That is why the work item reports a <see cref="WorkerDisposition"/> rather than a boolean:
        /// a torn-down operation and an operation that left this thread unsafe both mean "not directly
        /// reusable", but only the former may park again once its interrupt is drained. Collapsing them
        /// would make one of the two unreachable.
        /// </remarks>
        private static bool TryExecuteAssignedWork(Func<WorkerDisposition> workItem)
        {
            try
            {
                WorkerDisposition disposition = workItem();
                if (disposition is WorkerDisposition.Reuse)
                {
                    return true;
                }

                if (disposition is WorkerDisposition.Retire)
                {
                    return false;
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

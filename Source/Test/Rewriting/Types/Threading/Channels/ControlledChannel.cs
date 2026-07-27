// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#if NET
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Runtime.CompilerServices;
using CoyoteTasks = Microsoft.Coyote.Rewriting.Types.Threading.Tasks;
using SystemCancellationToken = System.Threading.CancellationToken;
using SystemCancellationTokenRegistration = System.Threading.CancellationTokenRegistration;
using SystemCancellationTokenSource = System.Threading.CancellationTokenSource;
using SystemChannels = System.Threading.Channels;
using SystemMonitor = System.Threading.Monitor;
using SystemTaskCreationOptions = System.Threading.Tasks.TaskCreationOptions;
using SystemTasks = System.Threading.Tasks;

namespace Microsoft.Coyote.Rewriting.Types.Threading.Channels
{
    /// <summary>
    /// A <see cref="SystemChannels.Channel{T}"/> whose reads, writes and completion are controlled by the
    /// <see cref="CoyoteRuntime"/> during systematic testing, so that a wait on an empty/full channel becomes
    /// a scheduling decision instead of an invisible wake queued inside non-rewritten framework internals
    /// (which the scheduler cannot observe, producing spurious deadlock reports).
    /// </summary>
    /// <remarks>
    /// All mutable state is guarded by the runtime's synchronized section (the same discipline the
    /// <c>SemaphoreSlim</c> mock uses) and, inside it, by <see cref="SyncRoot"/>. The second lock is
    /// what keeps a channel that outlives its iteration consistent: the synchronized section is
    /// entered on whichever runtime is current, and each runtime has a lock of its own, so it alone
    /// does not serialize two threads running under two runtimes. It fixes the state; it does not
    /// make the two iterations visible to each other, so a waiter parked when its creating iteration
    /// ended still never resumes and those interleavings are still unexplored — which is why
    /// <see cref="GetRuntime"/> reports the first operation to arrive from a later iteration, and why
    /// a legitimate process-lifetime singleton now shows up in the uncontrolled invocation count.
    /// A blocked async reader/writer parks on a plain
    /// <see cref="SystemTasks.TaskCompletionSource{TResult}"/> that a controlled operation completes inside
    /// that section; the returned task is registered as controlled and surfaced through
    /// <see cref="AsyncTaskAwaiterStateMachine{TResult}"/> so the runtime pauses the awaiting operation
    /// deterministically. Arbitrary user callbacks (<c>itemDropped</c>) and cancellation-registration
    /// disposal run OUTSIDE the section.
    /// </remarks>
    internal sealed class ControlledChannel<T> : SystemChannels.Channel<T>
    {
        /// <summary>
        /// Buffered items awaiting a reader. A linked list so either end can be dropped in O(1).
        /// </summary>
        private readonly LinkedList<T> Items;

        /// <summary>
        /// The maximum number of buffered items (<see cref="int.MaxValue"/> when unbounded).
        /// </summary>
        private readonly int Capacity;

        /// <summary>
        /// Behavior when a bounded channel's buffer is full.
        /// </summary>
        private readonly SystemChannels.BoundedChannelFullMode FullMode;

        /// <summary>
        /// Optional callback invoked (outside the synchronized section) for each dropped item.
        /// </summary>
        private readonly Action<T> ItemDropped;

        /// <summary>
        /// Readers parked in <c>ReadAsync</c> or the <c>ReadAllAsync</c> enumerator; each consumes one item.
        /// </summary>
        private readonly Queue<IItemWaiter> BlockedReaders;

        /// <summary>
        /// Readers parked in <c>WaitToReadAsync</c>; each is merely signaled that data may be available.
        /// </summary>
        private readonly Queue<SystemTasks.TaskCompletionSource<bool>> WaitingReaders;

        /// <summary>
        /// Writers parked in <c>WriteAsync</c> on a full <c>Wait</c>-mode channel; each carries its item.
        /// </summary>
        private readonly Queue<PendingWrite> PendingWrites;

        /// <summary>
        /// Writers parked in <c>WaitToWriteAsync</c> on a full <c>Wait</c>-mode channel.
        /// </summary>
        private readonly Queue<SystemTasks.TaskCompletionSource<bool>> WaitingWriters;

        /// <summary>
        /// Backs <c>Reader.Completion</c>; resolved once the channel is completed and drained.
        /// </summary>
        private readonly SystemTasks.TaskCompletionSource<bool> CompletionSource;

        /// <summary>
        /// Whether <c>Complete</c>/<c>TryComplete</c> has been called.
        /// </summary>
        private bool IsCompleted;

        /// <summary>
        /// The error the channel was completed with, if any.
        /// </summary>
        private Exception CompletionError;

        /// <summary>
        /// Guards this channel's own state, independently of any runtime.
        /// </summary>
        /// <remarks>
        /// The runtime's synchronized section is what makes an operation on this channel atomic with
        /// respect to the SCHEDULER, and it is entered on <see cref="CoyoteRuntime.Current"/> — which
        /// for a channel that outlives its iteration is not always the same runtime, and each runtime
        /// has a lock of its own. Two threads running under two runtimes would therefore mutate the
        /// buffer and the waiter queues below under different locks. This one is theirs, so that the
        /// state stays consistent whichever runtime is current.
        /// <para>
        /// A plain monitor rather than another <c>SynchronizedSection</c>: that type tracks whether the
        /// thread is inside a section with a THREAD-STATIC flag, so entering a second one while already
        /// inside the runtime's is a no-op and would take no lock at all.
        /// </para>
        /// </remarks>
        private readonly object SyncRoot;

        /// <summary>
        /// The runtime this channel was created under, used only to notice that a later operation is
        /// running under a different one.
        /// </summary>
        private readonly Guid CreatingRuntimeId;

        /// <summary>
        /// Initializes a new instance of the <see cref="ControlledChannel{T}"/> class.
        /// </summary>
        internal ControlledChannel(CoyoteRuntime runtime, int capacity,
            SystemChannels.BoundedChannelFullMode fullMode, Action<T> itemDropped)
        {
            this.SyncRoot = new object();
            this.CreatingRuntimeId = runtime.Id;
            this.Items = new LinkedList<T>();
            this.Capacity = capacity;
            this.FullMode = fullMode;
            this.ItemDropped = itemDropped;
            this.BlockedReaders = new Queue<IItemWaiter>();
            this.WaitingReaders = new Queue<SystemTasks.TaskCompletionSource<bool>>();
            this.PendingWrites = new Queue<PendingWrite>();
            this.WaitingWriters = new Queue<SystemTasks.TaskCompletionSource<bool>>();
            this.CompletionSource = CreateSource<bool>();
            runtime.RegisterKnownControlledTask(this.CompletionSource.Task);

            this.Reader = new ControlledReader(this);
            this.Writer = new ControlledWriter(this);
        }

        /// <summary>
        /// Returns the runtime that should control the CURRENT operation on this channel, reporting the
        /// first operation that arrives from a later test iteration.
        /// </summary>
        /// <remarks>
        /// Deliberately resolves <see cref="CoyoteRuntime.Current"/> per operation rather than failing the
        /// test outright when the channel is used by a runtime other than the one that created it (the
        /// guard the SemaphoreSlim mock uses). A channel is frequently a PROCESS-LIFETIME singleton — e.g.
        /// a logging service's buffer created lazily during one test iteration and written to across many
        /// later iterations — which is legitimate and behaves correctly on the real BCL channel; a
        /// per-operation Current lets each op run under its own iteration's runtime, and
        /// <see cref="SyncRoot"/> keeps its state consistent while they do.
        /// <para>
        /// What that costs is still worth reporting. The scheduler of the current iteration cannot see
        /// the operations of an earlier one, so a waiter parked when its creating iteration ended simply
        /// never resumes, and the interleavings between the two iterations are not explored. The report
        /// says so rather than leaving a run that looks fully explored.
        /// </para>
        /// </remarks>
        private CoyoteRuntime GetRuntime()
        {
            CoyoteRuntime runtime = CoyoteRuntime.Current;
            if (runtime.Id != this.CreatingRuntimeId)
            {
                runtime.NotifyUncontrolledPrimitive("A channel created in a previous test iteration");
            }

            return runtime;
        }

        /// <summary>
        /// Whether the buffer has room for another item (always true when unbounded, and never true
        /// for a rendezvous channel, which has no buffer at all).
        /// </summary>
        private bool HasSpace => this.Capacity is int.MaxValue || this.Items.Count < this.Capacity;

        /// <summary>
        /// Whether this is the zero capacity channel that .NET 10 added, where an item is handed from
        /// a writer to a reader directly because there is nowhere to put it in between.
        /// </summary>
        /// <remarks>
        /// The write side needs no special handling: <see cref="TryWriteLocked"/> already offers the
        /// item to a parked reader before it tries to buffer, and <see cref="HasSpace"/> is false here,
        /// so a write with no reader waiting parks exactly as it should. It is the read side that has
        /// to know about this, because a reader arriving first would otherwise park without ever
        /// looking at the writers already parked, and the two would wait for each other forever.
        /// </remarks>
        private bool IsRendezvous => this.Capacity is 0;

        /// <summary>
        /// Enters this channel's own critical section, which is always entered from inside the runtime's
        /// synchronized section and never the other way around.
        /// </summary>
        /// <remarks>
        /// That ordering is what makes a second lock safe here. Nothing acquires <see cref="SyncRoot"/>
        /// and then waits on a runtime lock, so there is no cycle to deadlock on, and two threads under
        /// the SAME runtime are already serialized by its section and never contend for this one. The
        /// only contention is between runtimes, which is exactly the case this exists for.
        /// <para>
        /// The section covers the state changes ONLY. <see cref="Park{TResult}"/> is deliberately left
        /// outside it, because parking pauses the calling operation: the runtime releases its own lock
        /// while an operation is paused, but it knows nothing about this one, so parking inside this
        /// section would hold it until the operation resumes — and the operation can only resume once
        /// another one reaches the channel, which it cannot. That is a deadlock, not a slow path.
        /// Releasing first is safe: the waiter is already queued, so a racing completion simply
        /// finishes the task before the awaiter state machine looks at it, which it handles.
        /// </para>
        /// <para>
        /// The same reasoning applies to anything added later. Nothing that can pause an operation may
        /// run inside this section.
        /// </para>
        /// </remarks>
        private ChannelSection EnterChannelSection() => new ChannelSection(this.SyncRoot);

        /// <summary>
        /// Creates a completion source whose continuations run asynchronously, as every parked waiter requires.
        /// </summary>
        private static SystemTasks.TaskCompletionSource<TResult> CreateSource<TResult>() =>
            new SystemTasks.TaskCompletionSource<TResult>(SystemTaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Registers <paramref name="tcs"/> as a controlled task, wires cancellation, and surfaces it through
        /// the awaiter state machine so the runtime pauses the awaiting operation deterministically.
        /// </summary>
        private static SystemTasks.Task<TResult> Park<TResult>(CoyoteRuntime runtime,
            SystemTasks.TaskCompletionSource<TResult> tcs, SystemCancellationToken cancellationToken)
        {
            runtime.RegisterKnownControlledTask(tcs.Task);
            Register(tcs, cancellationToken);
            return AsyncTaskAwaiterStateMachine<TResult>.RunAsync(runtime, tcs.Task, true);
        }

        /// <summary>
        /// Removes and returns the buffered head item, promoting parked writers and resolving completion.
        /// </summary>
        private T DequeueItem()
        {
            T item = this.Items.First.Value;
            this.Items.RemoveFirst();

            // A promoted write may buffer a new item and wake waiting readers (handled inside OnSlotFreed).
            this.OnSlotFreed();
            this.ResolveCompletionIfDrained();
            return item;
        }

        /// <summary>
        /// The <c>ValueTask&lt;bool&gt;</c> a read/wait returns once the channel is completed with no data:
        /// <c>false</c> for a clean completion, otherwise the completion error.
        /// </summary>
        private SystemTasks.ValueTask<bool> CompletedReadResult() =>
            this.CompletionError is null
                ? new SystemTasks.ValueTask<bool>(false)
                : new SystemTasks.ValueTask<bool>(CoyoteTasks.Task.FromException<bool>(this.CompletionError));

        // ─── Read side ───────────────────────────────────────────────────────────────────────────

        private bool CoreTryRead(out T item)
        {
            CoyoteRuntime runtime = this.GetRuntime();
            using (runtime.EnterSynchronizedSection())
            {
                using (this.EnterChannelSection())
                {
                    if (this.Items.Count > 0)
                    {
                        item = this.DequeueItem();
                        return true;
                    }

                    return this.TryTakeFromPendingWrite(out item);
                }
            }
        }

        private SystemTasks.ValueTask<bool> CoreWaitToReadAsync(SystemCancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new SystemTasks.ValueTask<bool>(CoyoteTasks.Task.FromCanceled<bool>(cancellationToken));
            }

            CoyoteRuntime runtime = this.GetRuntime();
            using (runtime.EnterSynchronizedSection())
            {
                SystemTasks.TaskCompletionSource<bool> tcs;
                using (this.EnterChannelSection())
                {
                    if (this.Items.Count > 0 || this.HasPendingWrite)
                    {
                        return new SystemTasks.ValueTask<bool>(true);
                    }

                    if (this.IsCompleted)
                    {
                        return this.CompletedReadResult();
                    }

                    tcs = CreateSource<bool>();
                    this.WaitingReaders.Enqueue(tcs);
                }

                return new SystemTasks.ValueTask<bool>(Park(runtime, tcs, cancellationToken));
            }
        }

        private SystemTasks.ValueTask<T> CoreReadAsync(SystemCancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new SystemTasks.ValueTask<T>(CoyoteTasks.Task.FromCanceled<T>(cancellationToken));
            }

            CoyoteRuntime runtime = this.GetRuntime();
            using (runtime.EnterSynchronizedSection())
            {
                SystemTasks.TaskCompletionSource<T> tcs;
                using (this.EnterChannelSection())
                {
                    if (this.Items.Count > 0)
                    {
                        return new SystemTasks.ValueTask<T>(this.DequeueItem());
                    }

                    if (this.TryTakeFromPendingWrite(out T handedOver))
                    {
                        return new SystemTasks.ValueTask<T>(handedOver);
                    }

                    if (this.IsCompleted)
                    {
                        return new SystemTasks.ValueTask<T>(
                            CoyoteTasks.Task.FromException<T>(CreateClosedException(this.CompletionError)));
                    }

                    tcs = CreateSource<T>();
                    this.BlockedReaders.Enqueue(new ReadItemWaiter(tcs));
                    this.OnReaderParked();
                }

                return new SystemTasks.ValueTask<T>(Park(runtime, tcs, cancellationToken));
            }
        }

        private SystemTasks.ValueTask<bool> CoreMoveNextAsync(Enumerator enumerator, SystemCancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new SystemTasks.ValueTask<bool>(CoyoteTasks.Task.FromCanceled<bool>(cancellationToken));
            }

            CoyoteRuntime runtime = this.GetRuntime();
            using (runtime.EnterSynchronizedSection())
            {
                SystemTasks.TaskCompletionSource<bool> tcs;
                using (this.EnterChannelSection())
                {
                    if (this.Items.Count > 0)
                    {
                        enumerator.CurrentItem = this.DequeueItem();
                        return new SystemTasks.ValueTask<bool>(true);
                    }

                    if (this.TryTakeFromPendingWrite(out T handedOver))
                    {
                        enumerator.CurrentItem = handedOver;
                        return new SystemTasks.ValueTask<bool>(true);
                    }

                    if (this.IsCompleted)
                    {
                        return this.CompletedReadResult();
                    }

                    tcs = CreateSource<bool>();
                    this.BlockedReaders.Enqueue(new MoveNextWaiter(tcs, enumerator));
                    this.OnReaderParked();
                }

                return new SystemTasks.ValueTask<bool>(Park(runtime, tcs, cancellationToken));
            }
        }

        private bool CoreTryPeek(out T item)
        {
            CoyoteRuntime runtime = this.GetRuntime();
            using (runtime.EnterSynchronizedSection())
            {
                using (this.EnterChannelSection())
                {
                    if (this.Items.Count > 0)
                    {
                        item = this.Items.First.Value;
                        return true;
                    }

                    // The item a writer is parked with is peekable even though it is not buffered, and
                    // peeking must not consume it, so the writer is left parked.
                    return this.TryPeekPendingWrite(out item);
                }
            }
        }

        private int CoreCount
        {
            get
            {
                CoyoteRuntime runtime = this.GetRuntime();
                using (runtime.EnterSynchronizedSection())
                {
                    using (this.EnterChannelSection())
                    {
                        return this.Items.Count;
                    }
                }
            }
        }

        // ─── Write side ──────────────────────────────────────────────────────────────────────────

        private bool CoreTryWrite(T item)
        {
            CoyoteRuntime runtime = this.GetRuntime();
            bool hasDropped;
            T dropped;
            bool accepted;
            using (runtime.EnterSynchronizedSection())
            {
                using (this.EnterChannelSection())
                {
                    accepted = this.TryWriteLocked(item, out dropped, out hasDropped, out _);
                }
            }

            if (hasDropped)
            {
                this.ItemDropped?.Invoke(dropped);
            }

            return accepted;
        }

        private SystemTasks.ValueTask CoreWriteAsync(T item, SystemCancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new SystemTasks.ValueTask(CoyoteTasks.Task.FromCanceled(cancellationToken));
            }

            CoyoteRuntime runtime = this.GetRuntime();
            bool hasDropped;
            T dropped;
            SystemTasks.ValueTask result;
            using (runtime.EnterSynchronizedSection())
            {
                SystemTasks.TaskCompletionSource<bool> tcs = null;
                using (this.EnterChannelSection())
                {
                    if (this.IsCompleted)
                    {
                        return new SystemTasks.ValueTask(
                            CoyoteTasks.Task.FromException(CreateClosedException(this.CompletionError)));
                    }

                    bool accepted = this.TryWriteLocked(item, out dropped, out hasDropped, out bool full);
                    if (!accepted)
                    {
                        // Bounded, Wait-mode, full: park until a read frees a slot — or, on a rendezvous
                        // channel, until a reader arrives to take the item straight out of this write.
                        Debug.Assert(full, "WriteAsync only parks when the bounded channel is full.");
                        tcs = CreateSource<bool>();
                        this.PendingWrites.Enqueue(new PendingWrite(tcs, item));
                        this.OnWriterParked();
                    }
                }

                result = tcs is null ? default :
                    new SystemTasks.ValueTask(Park(runtime, tcs, cancellationToken));
            }

            if (hasDropped)
            {
                this.ItemDropped?.Invoke(dropped);
            }

            return result;
        }

        private SystemTasks.ValueTask<bool> CoreWaitToWriteAsync(SystemCancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new SystemTasks.ValueTask<bool>(CoyoteTasks.Task.FromCanceled<bool>(cancellationToken));
            }

            CoyoteRuntime runtime = this.GetRuntime();
            using (runtime.EnterSynchronizedSection())
            {
                SystemTasks.TaskCompletionSource<bool> tcs;
                using (this.EnterChannelSection())
                {
                    if (this.IsCompleted)
                    {
                        return this.CompletedReadResult();
                    }

                    if (this.HasSpace || this.FullMode != SystemChannels.BoundedChannelFullMode.Wait)
                    {
                        // Space is available, or a drop mode means a write will always be accepted.
                        return new SystemTasks.ValueTask<bool>(true);
                    }

                    if (this.HasBlockedReader)
                    {
                        // No space, but a reader is parked, so a rendezvous write would be taken at once.
                        return new SystemTasks.ValueTask<bool>(true);
                    }

                    tcs = CreateSource<bool>();
                    this.WaitingWriters.Enqueue(tcs);
                }

                return new SystemTasks.ValueTask<bool>(Park(runtime, tcs, cancellationToken));
            }
        }

        private bool CoreTryComplete(Exception error)
        {
            CoyoteRuntime runtime = this.GetRuntime();
            using (runtime.EnterSynchronizedSection())
            {
                using (this.EnterChannelSection())
                {
                    if (this.IsCompleted)
                    {
                        return false;
                    }

                    this.IsCompleted = true;
                    this.CompletionError = error;

                    // Readers waiting on data: if the buffer holds items they can still be read, otherwise the
                    // channel is finished for them.
                    if (this.Items.Count is 0)
                    {
                        while (this.WaitingReaders.Count > 0)
                        {
                            CompleteWait(this.WaitingReaders.Dequeue(), error);
                        }

                        while (this.BlockedReaders.Count > 0)
                        {
                            this.BlockedReaders.Dequeue().FailNoData(error);
                        }

                        this.ResolveCompletion();
                    }
                    else
                    {
                        // Data remains: let waiters drain it; completion resolves when the last item is read.
                        this.WakeWaitingReaders();
                    }

                    // No further writes can succeed. The closed exception does not depend on the waiter,
                    // so build it once and share it, as the BCL does.
                    if (this.PendingWrites.Count > 0)
                    {
                        Exception closedError = CreateClosedException(error);
                        while (this.PendingWrites.Count > 0)
                        {
                            this.PendingWrites.Dequeue().Fail(closedError);
                        }
                    }

                    while (this.WaitingWriters.Count > 0)
                    {
                        CompleteWait(this.WaitingWriters.Dequeue(), error);
                    }

                    return true;
                }
            }
        }

        private SystemTasks.Task Completion
        {
            get
            {
                // Re-register under the CURRENT runtime so an await from a later test iteration (for a
                // singleton channel) still sees a controlled task. Registration is idempotent per runtime.
                this.GetRuntime().RegisterKnownControlledTask(this.CompletionSource.Task);
                return this.CompletionSource.Task;
            }
        }

        // ─── Locked helpers (caller holds the synchronized section) ────────────────────────────────

        /// <summary>
        /// Attempts to write <paramref name="item"/>: hand it to a blocked reader, buffer it, or apply the
        /// bounded full policy. Returns whether the write was accepted; <paramref name="full"/> is set when it
        /// was rejected solely because a bounded <c>Wait</c>-mode buffer is full (the caller may then park).
        /// </summary>
        private bool TryWriteLocked(T item, out T dropped, out bool hasDropped, out bool full)
        {
            dropped = default;
            hasDropped = false;
            full = false;

            if (this.IsCompleted)
            {
                return false;
            }

            if (this.TryDeliverToBlockedReader(item))
            {
                return true;
            }

            if (this.HasSpace)
            {
                this.Items.AddLast(item);
                this.WakeWaitingReaders();
                return true;
            }

            // Bounded and full.
            switch (this.FullMode)
            {
                case SystemChannels.BoundedChannelFullMode.Wait:
                    full = true;
                    return false;

                case SystemChannels.BoundedChannelFullMode.DropWrite:
                    dropped = item;
                    hasDropped = true;
                    return true;

                case SystemChannels.BoundedChannelFullMode.DropOldest:
                case SystemChannels.BoundedChannelFullMode.DropNewest:
                    if (this.Items.Count is 0)
                    {
                        // A rendezvous channel buffers nothing, so there is no older or newer item to
                        // evict and the incoming one is what gets dropped, exactly as the real channel
                        // does. Reached only here, since a bounded buffer is never both full and empty.
                        dropped = item;
                        hasDropped = true;
                        return true;
                    }

                    // Evict a buffered item to make room for the new one.
                    LinkedListNode<T> evicted = this.FullMode is SystemChannels.BoundedChannelFullMode.DropOldest ?
                        this.Items.First : this.Items.Last;
                    dropped = evicted.Value;
                    this.Items.Remove(evicted);
                    hasDropped = true;
                    this.Items.AddLast(item);
                    this.WakeWaitingReaders();
                    return true;

                default:
                    full = true;
                    return false;
            }
        }

        /// <summary>
        /// Takes the item of the first live parked writer, completing that write. Returns whether one
        /// was taken.
        /// </summary>
        /// <remarks>
        /// This is how a rendezvous channel hands an item over: with no buffer to pass through, the
        /// reader takes it out of the writer directly. Only reachable on such a channel — a bounded one
        /// parks a writer only when its buffer is full, so an empty buffer means nothing is parked, and
        /// the callers all check the buffer first.
        /// </remarks>
        private bool TryTakeFromPendingWrite(out T item)
        {
            while (this.PendingWrites.Count > 0)
            {
                PendingWrite pending = this.PendingWrites.Dequeue();
                if (pending.Promote())
                {
                    item = pending.Item;
                    return true;
                }

                // Otherwise the writer was canceled: skip it and try the next.
            }

            item = default;
            return false;
        }

        /// <summary>
        /// Returns the item of the first live parked writer without taking it, leaving that writer parked.
        /// </summary>
        private bool TryPeekPendingWrite(out T item)
        {
            foreach (PendingWrite pending in this.PendingWrites)
            {
                if (pending.IsPending)
                {
                    item = pending.Item;
                    return true;
                }
            }

            item = default;
            return false;
        }

        /// <summary>
        /// Whether a writer is parked holding an item that a read could take right now.
        /// </summary>
        private bool HasPendingWrite => this.TryPeekPendingWrite(out _);

        /// <summary>
        /// Whether a reader is parked waiting for an item that a write could hand over right now.
        /// </summary>
        private bool HasBlockedReader
        {
            get
            {
                foreach (IItemWaiter waiter in this.BlockedReaders)
                {
                    if (waiter.IsPending)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// A reader parked with nothing to take. On a rendezvous channel that is precisely what makes a
        /// write able to succeed, so everyone waiting to write is told to retry.
        /// </summary>
        private void OnReaderParked()
        {
            if (this.IsRendezvous)
            {
                while (this.WaitingWriters.Count > 0)
                {
                    this.WaitingWriters.Dequeue().TrySetResult(true);
                }
            }
        }

        /// <summary>
        /// A writer parked still holding its item. On a rendezvous channel that is precisely what makes
        /// a read able to succeed, so everyone waiting to read is told to retry.
        /// </summary>
        private void OnWriterParked()
        {
            if (this.IsRendezvous)
            {
                this.WakeWaitingReaders();
            }
        }

        /// <summary>
        /// Hands <paramref name="item"/> to the first live blocked reader, if any. Returns whether it was consumed.
        /// </summary>
        private bool TryDeliverToBlockedReader(T item)
        {
            while (this.BlockedReaders.Count > 0)
            {
                IItemWaiter waiter = this.BlockedReaders.Dequeue();
                if (waiter.TryDeliver(item))
                {
                    return true;
                }

                // Otherwise the reader was canceled: skip it and try the next.
            }

            return false;
        }

        /// <summary>
        /// Signals every parked <c>WaitToReadAsync</c> waiter that data may be available; each will retry.
        /// </summary>
        private void WakeWaitingReaders()
        {
            while (this.WaitingReaders.Count > 0)
            {
                this.WaitingReaders.Dequeue().TrySetResult(true);
            }
        }

        /// <summary>
        /// A read freed a buffer slot: promote a parked writer (transferring its item) and wake any
        /// <c>WaitToWriteAsync</c> waiters.
        /// </summary>
        private void OnSlotFreed()
        {
            bool buffered = false;
            while (this.HasSpace && this.PendingWrites.Count > 0)
            {
                PendingWrite pending = this.PendingWrites.Dequeue();
                if (pending.Promote())
                {
                    this.Items.AddLast(pending.Item);
                    buffered = true;
                }

                // Otherwise the writer was canceled: skip it, the slot stays free for the next.
            }

            if (buffered)
            {
                this.WakeWaitingReaders();
            }

            while (this.HasSpace && this.WaitingWriters.Count > 0)
            {
                this.WaitingWriters.Dequeue().TrySetResult(true);
            }
        }

        private void ResolveCompletionIfDrained()
        {
            if (this.IsCompleted && this.Items.Count is 0)
            {
                this.ResolveCompletion();
            }
        }

        private void ResolveCompletion()
        {
            if (this.CompletionError is null)
            {
                this.CompletionSource.TrySetResult(true);
            }
            else if (this.CompletionError is OperationCanceledException oce)
            {
                this.CompletionSource.TrySetCanceled(oce.CancellationToken);
            }
            else
            {
                this.CompletionSource.TrySetException(this.CompletionError);
            }
        }

        /// <summary>
        /// Completes a parked <c>WaitToReadAsync</c> or <c>WaitToWriteAsync</c> waiter because the channel
        /// completed: a clean completion reports that nothing more will arrive, an error faults the wait.
        /// </summary>
        private static void CompleteWait(SystemTasks.TaskCompletionSource<bool> tcs, Exception error)
        {
            if (error is null)
            {
                tcs.TrySetResult(false);
            }
            else
            {
                tcs.TrySetException(error);
            }
        }

        private static Exception CreateClosedException(Exception error)
        {
            if (error is null)
            {
                return new SystemChannels.ChannelClosedException();
            }

            if (error is OperationCanceledException)
            {
                return error;
            }

            return new SystemChannels.ChannelClosedException(error);
        }

        /// <summary>
        /// Registers a cancellation callback that completes <paramref name="tcs"/> as canceled. The parked
        /// item/wait is skipped by the dequeue loops once its task is completed, so a canceled waiter never
        /// blocks FIFO progress. No-op for a token that can never be canceled.
        /// </summary>
        private static void Register<TResult>(SystemTasks.TaskCompletionSource<TResult> tcs,
            SystemCancellationToken cancellationToken)
        {
            if (cancellationToken.CanBeCanceled)
            {
                SystemCancellationTokenRegistration registration =
                    cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

                // Release the registration once the wait is over. The token routinely outlives the wait (a
                // consumer loop awaiting on a long-lived token is the common shape), and without this the
                // source accumulates one live callback per parked reader/writer, each retaining its
                // completion source and task, until the token itself is collected.
                //
                // The disposal is queued to the default scheduler rather than run inline: the completion
                // that triggers it usually happens inside the runtime's synchronized section, and disposing
                // there would block that section on a cancellation callback running on another thread.
                tcs.Task.ContinueWith(
                    static (_, state) => ((SystemCancellationTokenRegistration)state).Dispose(),
                    registration,
                    SystemCancellationToken.None,
                    SystemTasks.TaskContinuationOptions.DenyChildAttach,
                    SystemTasks.TaskScheduler.Default);
            }
        }

        // ─── Waiter abstractions ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Holds this channel's critical section until disposed. See <see cref="EnterChannelSection"/>
        /// for why it is a plain monitor rather than a <c>SynchronizedSection</c>.
        /// </summary>
        private readonly struct ChannelSection : IDisposable
        {
            private readonly object SyncRoot;

            internal ChannelSection(object syncRoot)
            {
                this.SyncRoot = syncRoot;
                SystemMonitor.Enter(syncRoot);
            }

            public void Dispose() => SystemMonitor.Exit(this.SyncRoot);
        }

        private interface IItemWaiter
        {
            /// <summary>Whether this waiter is still waiting, rather than already canceled.</summary>
            bool IsPending { get; }

            /// <summary>Hands an item to the waiter; returns whether it accepted (false if already canceled).</summary>
            bool TryDeliver(T item);

            /// <summary>
            /// Fails the waiter because the channel completed with no data available. The channel
            /// completion error is passed as-is (null if it completed cleanly), as each waiter reports
            /// a clean completion differently.
            /// </summary>
            void FailNoData(Exception error);
        }

        private sealed class ReadItemWaiter : IItemWaiter
        {
            private readonly SystemTasks.TaskCompletionSource<T> Tcs;

            internal ReadItemWaiter(SystemTasks.TaskCompletionSource<T> tcs) => this.Tcs = tcs;

            public bool IsPending => !this.Tcs.Task.IsCompleted;

            public bool TryDeliver(T item) => this.Tcs.TrySetResult(item);

            // There is no item to return, so even a clean completion faults the read.
            public void FailNoData(Exception error) => this.Tcs.TrySetException(CreateClosedException(error));
        }

        private sealed class MoveNextWaiter : IItemWaiter
        {
            private readonly SystemTasks.TaskCompletionSource<bool> Tcs;
            private readonly Enumerator Enumerator;

            internal MoveNextWaiter(SystemTasks.TaskCompletionSource<bool> tcs, Enumerator enumerator)
            {
                this.Tcs = tcs;
                this.Enumerator = enumerator;
            }

            public bool IsPending => !this.Tcs.Task.IsCompleted;

            /// <remarks>
            /// The item is published BEFORE the waiter is completed, not after. A reader resumes once ITS
            /// runtime observes the task completed, and that is not always the runtime completing it: a write
            /// arriving from a thread the reader's runtime does not control holds a different runtime's
            /// section, and <see cref="Enumerator.Current"/> is a plain field read that takes neither section,
            /// so nothing would order it against a store made after the completion. Publishing first needs no
            /// such ordering. The reverse is not a race: the waiter is parked, so its reader is not reading
            /// Current, and if the waiter turns out to be canceled the item is left behind in an enumerator
            /// whose enumeration has already ended.
            /// </remarks>
            public bool TryDeliver(T item)
            {
                this.Enumerator.CurrentItem = item;
                return this.Tcs.TrySetResult(true);
            }

            public void FailNoData(Exception error)
            {
                // A clean completion ends enumeration (MoveNextAsync returns false); an error faults it.
                if (error is null)
                {
                    this.Tcs.TrySetResult(false);
                }
                else
                {
                    this.Tcs.TrySetException(CreateClosedException(error));
                }
            }
        }

        private sealed class PendingWrite
        {
            private readonly SystemTasks.TaskCompletionSource<bool> Tcs;

            internal PendingWrite(SystemTasks.TaskCompletionSource<bool> tcs, T item)
            {
                this.Tcs = tcs;
                this.Item = item;
            }

            internal T Item { get; }

            /// <summary>Whether this write is still parked, rather than already canceled.</summary>
            internal bool IsPending => !this.Tcs.Task.IsCompleted;

            /// <summary>Completes the parked write successfully; returns whether it was accepted (not canceled).</summary>
            internal bool Promote() => this.Tcs.TrySetResult(true);

            internal void Fail(Exception error) => this.Tcs.TrySetException(error);
        }

        // ─── Reader / writer facades (virtual dispatch targets) ────────────────────────────────────

        private sealed class ControlledReader : SystemChannels.ChannelReader<T>
        {
            private readonly ControlledChannel<T> Channel;

            internal ControlledReader(ControlledChannel<T> channel) => this.Channel = channel;

            public override SystemTasks.Task Completion => this.Channel.Completion;

            public override bool CanCount => true;

            public override bool CanPeek => true;

            public override int Count => this.Channel.CoreCount;

            public override bool TryRead(out T item) => this.Channel.CoreTryRead(out item);

            public override bool TryPeek(out T item) => this.Channel.CoreTryPeek(out item);

            public override SystemTasks.ValueTask<bool> WaitToReadAsync(SystemCancellationToken cancellationToken = default) =>
                this.Channel.CoreWaitToReadAsync(cancellationToken);

            public override SystemTasks.ValueTask<T> ReadAsync(SystemCancellationToken cancellationToken = default) =>
                this.Channel.CoreReadAsync(cancellationToken);

            public override IAsyncEnumerable<T> ReadAllAsync(SystemCancellationToken cancellationToken = default) =>
                new AsyncEnumerable(this.Channel, cancellationToken);
        }

        private sealed class ControlledWriter : SystemChannels.ChannelWriter<T>
        {
            private readonly ControlledChannel<T> Channel;

            internal ControlledWriter(ControlledChannel<T> channel) => this.Channel = channel;

            public override bool TryWrite(T item) => this.Channel.CoreTryWrite(item);

            public override SystemTasks.ValueTask<bool> WaitToWriteAsync(SystemCancellationToken cancellationToken = default) =>
                this.Channel.CoreWaitToWriteAsync(cancellationToken);

            public override SystemTasks.ValueTask WriteAsync(T item, SystemCancellationToken cancellationToken = default) =>
                this.Channel.CoreWriteAsync(item, cancellationToken);

            public override bool TryComplete(Exception error = null) => this.Channel.CoreTryComplete(error);
        }

        // ─── ReadAllAsync enumeration (hand-rolled; no compiler async iterator) ────────────────────

        private sealed class AsyncEnumerable : IAsyncEnumerable<T>
        {
            private readonly ControlledChannel<T> Channel;
            private readonly SystemCancellationToken CancellationToken;

            internal AsyncEnumerable(ControlledChannel<T> channel, SystemCancellationToken cancellationToken)
            {
                this.Channel = channel;
                this.CancellationToken = cancellationToken;
            }

            /// <remarks>
            /// Both tokens must be able to stop the enumeration: 'ReadAllAsync(ct1).WithCancellation(ct2)'
            /// supplies one at construction and one here, and honouring only one leaves the other unable to
            /// cancel a parked move-next. They are linked only when both can actually be cancelled, so the
            /// common case of at most one real token allocates nothing.
            /// </remarks>
            public IAsyncEnumerator<T> GetAsyncEnumerator(SystemCancellationToken cancellationToken = default)
            {
                if (!this.CancellationToken.CanBeCanceled)
                {
                    return new Enumerator(this.Channel, null, cancellationToken);
                }

                if (!cancellationToken.CanBeCanceled || this.CancellationToken.Equals(cancellationToken))
                {
                    return new Enumerator(this.Channel, null, this.CancellationToken);
                }

                var linkedSource = SystemCancellationTokenSource.CreateLinkedTokenSource(
                    this.CancellationToken, cancellationToken);
                return new Enumerator(this.Channel, linkedSource, linkedSource.Token);
            }
        }

        private sealed class Enumerator : IAsyncEnumerator<T>
        {
            private readonly ControlledChannel<T> Channel;
            private readonly SystemCancellationToken CancellationToken;

            /// <summary>
            /// The source linking the two tokens this enumerator honours, or null if there was
            /// at most one token to honour and nothing had to be linked.
            /// </summary>
            private readonly SystemCancellationTokenSource LinkedSource;

            internal Enumerator(ControlledChannel<T> channel, SystemCancellationTokenSource linkedSource,
                SystemCancellationToken cancellationToken)
            {
                this.Channel = channel;
                this.LinkedSource = linkedSource;
                this.CancellationToken = cancellationToken;
            }

            /// <summary>Set by the channel when it delivers an item to this enumerator's parked move-next.</summary>
            internal T CurrentItem { get; set; }

            public T Current => this.CurrentItem;

            public SystemTasks.ValueTask<bool> MoveNextAsync() =>
                this.Channel.CoreMoveNextAsync(this, this.CancellationToken);

            public SystemTasks.ValueTask DisposeAsync()
            {
                this.LinkedSource?.Dispose();
                return default;
            }
        }
    }
}
#endif

// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#if NET
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Runtime.CompilerServices;
using SystemCancellationToken = System.Threading.CancellationToken;
using SystemChannels = System.Threading.Channels;
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
    /// <c>SemaphoreSlim</c> mock uses). A blocked async reader/writer parks on a plain
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
        /// Initializes a new instance of the <see cref="ControlledChannel{T}"/> class.
        /// </summary>
        internal ControlledChannel(CoyoteRuntime runtime, int capacity,
            SystemChannels.BoundedChannelFullMode fullMode, Action<T> itemDropped)
        {
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
        /// Returns the runtime that should control the CURRENT operation on this channel.
        /// </summary>
        /// <remarks>
        /// Deliberately resolves <see cref="CoyoteRuntime.Current"/> per operation rather than asserting the
        /// channel is used by the runtime that created it (the guard the SemaphoreSlim mock uses). A channel
        /// is frequently a PROCESS-LIFETIME singleton — e.g. a logging service's buffer created lazily during
        /// one test iteration and written to across many later iterations — which is legitimate and behaves
        /// correctly on the real BCL channel; a per-operation Current lets each op run under its own
        /// iteration's runtime. Its buffered items/waiters are plain data, so cross-iteration writes are safe;
        /// a waiter parked when its creating iteration ends simply never resumes (no one is awaiting it).
        /// </remarks>
        private static CoyoteRuntime GetRuntime() => CoyoteRuntime.Current;

        /// <summary>
        /// Whether the buffer has room for another item (always true when unbounded).
        /// </summary>
        private bool HasSpace => this.Capacity is int.MaxValue || this.Items.Count < this.Capacity;

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
                : new SystemTasks.ValueTask<bool>(SystemTasks.Task.FromException<bool>(this.CompletionError));

        // ─── Read side ───────────────────────────────────────────────────────────────────────────

        private bool CoreTryRead(out T item)
        {
            CoyoteRuntime runtime = GetRuntime();
            using (runtime.EnterSynchronizedSection())
            {
                if (this.Items.Count is 0)
                {
                    item = default;
                    return false;
                }

                item = this.DequeueItem();
                return true;
            }
        }

        private SystemTasks.ValueTask<bool> CoreWaitToReadAsync(SystemCancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new SystemTasks.ValueTask<bool>(SystemTasks.Task.FromCanceled<bool>(cancellationToken));
            }

            CoyoteRuntime runtime = GetRuntime();
            using (runtime.EnterSynchronizedSection())
            {
                if (this.Items.Count > 0)
                {
                    return new SystemTasks.ValueTask<bool>(true);
                }

                if (this.IsCompleted)
                {
                    return this.CompletedReadResult();
                }

                var tcs = CreateSource<bool>();
                this.WaitingReaders.Enqueue(tcs);
                return new SystemTasks.ValueTask<bool>(Park(runtime, tcs, cancellationToken));
            }
        }

        private SystemTasks.ValueTask<T> CoreReadAsync(SystemCancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new SystemTasks.ValueTask<T>(SystemTasks.Task.FromCanceled<T>(cancellationToken));
            }

            CoyoteRuntime runtime = GetRuntime();
            using (runtime.EnterSynchronizedSection())
            {
                if (this.Items.Count > 0)
                {
                    return new SystemTasks.ValueTask<T>(this.DequeueItem());
                }

                if (this.IsCompleted)
                {
                    return new SystemTasks.ValueTask<T>(
                        SystemTasks.Task.FromException<T>(CreateClosedException(this.CompletionError)));
                }

                var tcs = CreateSource<T>();
                this.BlockedReaders.Enqueue(new ReadItemWaiter(tcs));
                return new SystemTasks.ValueTask<T>(Park(runtime, tcs, cancellationToken));
            }
        }

        private SystemTasks.ValueTask<bool> CoreMoveNextAsync(Enumerator enumerator, SystemCancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new SystemTasks.ValueTask<bool>(SystemTasks.Task.FromCanceled<bool>(cancellationToken));
            }

            CoyoteRuntime runtime = GetRuntime();
            using (runtime.EnterSynchronizedSection())
            {
                if (this.Items.Count > 0)
                {
                    enumerator.CurrentItem = this.DequeueItem();
                    return new SystemTasks.ValueTask<bool>(true);
                }

                if (this.IsCompleted)
                {
                    return this.CompletedReadResult();
                }

                var tcs = CreateSource<bool>();
                this.BlockedReaders.Enqueue(new MoveNextWaiter(tcs, enumerator));
                return new SystemTasks.ValueTask<bool>(Park(runtime, tcs, cancellationToken));
            }
        }

        private bool CoreTryPeek(out T item)
        {
            CoyoteRuntime runtime = GetRuntime();
            using (runtime.EnterSynchronizedSection())
            {
                if (this.Items.Count > 0)
                {
                    item = this.Items.First.Value;
                    return true;
                }

                item = default;
                return false;
            }
        }

        private int CoreCount
        {
            get
            {
                CoyoteRuntime runtime = GetRuntime();
                using (runtime.EnterSynchronizedSection())
                {
                    return this.Items.Count;
                }
            }
        }

        // ─── Write side ──────────────────────────────────────────────────────────────────────────

        private bool CoreTryWrite(T item)
        {
            CoyoteRuntime runtime = GetRuntime();
            bool hasDropped;
            T dropped;
            bool accepted;
            using (runtime.EnterSynchronizedSection())
            {
                accepted = this.TryWriteLocked(item, out dropped, out hasDropped, out _);
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
                return new SystemTasks.ValueTask(SystemTasks.Task.FromCanceled(cancellationToken));
            }

            CoyoteRuntime runtime = GetRuntime();
            bool hasDropped;
            T dropped;
            SystemTasks.ValueTask result;
            using (runtime.EnterSynchronizedSection())
            {
                if (this.IsCompleted)
                {
                    return new SystemTasks.ValueTask(
                        SystemTasks.Task.FromException(CreateClosedException(this.CompletionError)));
                }

                bool accepted = this.TryWriteLocked(item, out dropped, out hasDropped, out bool full);
                if (accepted)
                {
                    result = default;
                }
                else
                {
                    // Bounded, Wait-mode, full: park until a read frees a slot.
                    Debug.Assert(full, "WriteAsync only parks when the bounded channel is full.");
                    var tcs = CreateSource<bool>();
                    this.PendingWrites.Enqueue(new PendingWrite(tcs, item));
                    result = new SystemTasks.ValueTask(Park(runtime, tcs, cancellationToken));
                }
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
                return new SystemTasks.ValueTask<bool>(SystemTasks.Task.FromCanceled<bool>(cancellationToken));
            }

            CoyoteRuntime runtime = GetRuntime();
            using (runtime.EnterSynchronizedSection())
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

                var tcs = CreateSource<bool>();
                this.WaitingWriters.Enqueue(tcs);
                return new SystemTasks.ValueTask<bool>(Park(runtime, tcs, cancellationToken));
            }
        }

        private bool CoreTryComplete(Exception error)
        {
            CoyoteRuntime runtime = GetRuntime();
            using (runtime.EnterSynchronizedSection())
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

        private SystemTasks.Task Completion
        {
            get
            {
                // Re-register under the CURRENT runtime so an await from a later test iteration (for a
                // singleton channel) still sees a controlled task. Registration is idempotent per runtime.
                GetRuntime().RegisterKnownControlledTask(this.CompletionSource.Task);
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
                cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            }
        }

        // ─── Waiter abstractions ───────────────────────────────────────────────────────────────────

        private interface IItemWaiter
        {
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

            public bool TryDeliver(T item)
            {
                if (this.Tcs.TrySetResult(true))
                {
                    // Safe to publish after completing: the paused reader cannot observe Current until it is
                    // rescheduled, which happens only after this synchronized section is released.
                    this.Enumerator.CurrentItem = item;
                    return true;
                }

                return false;
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

            public IAsyncEnumerator<T> GetAsyncEnumerator(SystemCancellationToken cancellationToken = default) =>
                new Enumerator(this.Channel, this.CancellationToken.CanBeCanceled ? this.CancellationToken : cancellationToken);
        }

        private sealed class Enumerator : IAsyncEnumerator<T>
        {
            private readonly ControlledChannel<T> Channel;
            private readonly SystemCancellationToken CancellationToken;

            internal Enumerator(ControlledChannel<T> channel, SystemCancellationToken cancellationToken)
            {
                this.Channel = channel;
                this.CancellationToken = cancellationToken;
            }

            /// <summary>Set by the channel when it delivers an item to this enumerator's parked move-next.</summary>
            internal T CurrentItem { get; set; }

            public T Current => this.CurrentItem;

            public SystemTasks.ValueTask<bool> MoveNextAsync() =>
                this.Channel.CoreMoveNextAsync(this, this.CancellationToken);

            public SystemTasks.ValueTask DisposeAsync() => default;
        }
    }
}
#endif

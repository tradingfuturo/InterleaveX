// Copyright (c) TradingFuturo, LLC (https://pipflow.com).
// Licensed under the GNU General Public License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Coyote.Runtime;
using SystemCancellationToken = System.Threading.CancellationToken;
using SystemCancellationTokenRegistration = System.Threading.CancellationTokenRegistration;
using SystemConcurrent = System.Collections.Concurrent;
using SystemTimeout = System.Threading.Timeout;

namespace Microsoft.Coyote.Rewriting.Types.Collections.Concurrent
{
#pragma warning disable CA1000 // Do not declare static members on generic types
    /// <summary>
    /// Provides methods for controlling a <see cref="SystemConcurrent.BlockingCollection{T}"/> during testing.
    /// </summary>
    /// <remarks>
    /// <para>This type is intended for compiler use rather than use directly in code.</para>
    /// <para><b>Why this exists.</b> <see cref="SystemConcurrent.BlockingCollection{T}"/> lives in the
    /// concurrent-collections namespace but is a SYNCHRONIZATION primitive: its adds and takes park the
    /// calling thread inside the BCL. An unmodelled park is invisible to the scheduler — the operation
    /// still looks <see cref="OperationStatus.Enabled"/> while its thread has vanished into native code, so
    /// no scheduling step ever happens and the runtime's periodic hang monitor reports
    /// "Potential deadlock or hang detected" on every schedule. Modelling it turns each park into a
    /// controlled pause, which both lets the scheduler explore the interleavings and keeps the step count
    /// advancing.</para>
    /// <para><b>Shape.</b> The type is concrete and none of its blocking members is virtual, so behaviour
    /// cannot be overridden by a subclass. Instead every call site is rewritten to one of the static
    /// interceptors below, which dispatch on <see cref="Wrapper"/>. This mirrors
    /// <c>Types.Threading.SemaphoreSlim</c>.</para>
    /// <para><b>Storage.</b> <see cref="Wrapper"/> derives from the real type and uses ONLY its
    /// non-blocking operations for storage, layering controlled waiting on top. Bounded-capacity
    /// accounting, ordering, completion timing and exception behaviour therefore remain the BCL's own and
    /// cannot drift. This is safe because the interleaving scheduler runs one controlled operation at a
    /// time, so the base's internal locks are never contended and its zero-timeout paths never park.</para>
    /// </remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class BlockingCollection<T>
    {
        /// <summary>
        /// The greatest number of collections the take-any/add-any operations accept. The BCL limit is one
        /// lower on an STA thread, because it reserves a wait handle for the message pump.
        /// </summary>
        private const int MaxAnyCollectionsMta = 63;
        private const int MaxAnyCollectionsSta = 62;

        /// <summary>
        /// Initializes a new instance of the <see cref="SystemConcurrent.BlockingCollection{T}"/> class.
        /// </summary>
        /// <returns>The new instance.</returns>
        public static SystemConcurrent.BlockingCollection<T> Create() =>
            Create(new SystemConcurrent.ConcurrentQueue<T>(), -1);

        /// <summary>
        /// Initializes a new instance of the <see cref="SystemConcurrent.BlockingCollection{T}"/> class
        /// with the specified upper bound.
        /// </summary>
        /// <param name="boundedCapacity">The bounded size of the collection.</param>
        /// <returns>The new instance.</returns>
        public static SystemConcurrent.BlockingCollection<T> Create(int boundedCapacity) =>
            Create(new SystemConcurrent.ConcurrentQueue<T>(), boundedCapacity);

        /// <summary>
        /// Initializes a new instance of the <see cref="SystemConcurrent.BlockingCollection{T}"/> class
        /// over the specified backing collection.
        /// </summary>
        /// <param name="collection">The backing collection.</param>
        /// <returns>The new instance.</returns>
        public static SystemConcurrent.BlockingCollection<T> Create(
            SystemConcurrent.IProducerConsumerCollection<T> collection) => Create(collection, -1);

        /// <summary>
        /// Initializes a new instance of the <see cref="SystemConcurrent.BlockingCollection{T}"/> class
        /// over the specified backing collection and with the specified upper bound.
        /// </summary>
        /// <param name="collection">The backing collection.</param>
        /// <param name="boundedCapacity">The bounded size of the collection.</param>
        /// <returns>The new instance.</returns>
        public static SystemConcurrent.BlockingCollection<T> Create(
            SystemConcurrent.IProducerConsumerCollection<T> collection, int boundedCapacity)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving)
            {
                // The same choice the uncontrolled path below makes, and for the same reason: the base
                // type reaches its unbounded configuration only through the capacity-less constructor.
                return boundedCapacity < 0
                    ? new Wrapper(runtime, collection)
                    : new Wrapper(runtime, collection, boundedCapacity);
            }

            return boundedCapacity < 0
                ? new SystemConcurrent.BlockingCollection<T>(collection)
                : new SystemConcurrent.BlockingCollection<T>(collection, boundedCapacity);
        }

#pragma warning disable CA1707 // Identifiers should not contain underscores
#pragma warning disable SA1300 // Element should begin with upper-case letter
#pragma warning disable IDE1006 // Naming Styles

        /// <summary>Gets the bounded capacity of the collection.</summary>
        /// <param name="instance">The collection.</param><returns>The bounded capacity.</returns>
        public static int get_BoundedCapacity(SystemConcurrent.BlockingCollection<T> instance)
        {
            CheckOwnership(instance);
            ExploreInterleaving();
            return instance.BoundedCapacity;
        }

        /// <summary>Gets the number of items in the collection.</summary>
        /// <param name="instance">The collection.</param><returns>The item count.</returns>
        public static int get_Count(SystemConcurrent.BlockingCollection<T> instance)
        {
            CheckOwnership(instance);
            ExploreInterleaving();
            return instance.Count;
        }

        /// <summary>Gets a value indicating whether adding has been marked complete.</summary>
        /// <param name="instance">The collection.</param><returns>Whether adding is complete.</returns>
        public static bool get_IsAddingCompleted(SystemConcurrent.BlockingCollection<T> instance)
        {
            CheckOwnership(instance);
            ExploreInterleaving();
            return instance.IsAddingCompleted;
        }

        /// <summary>Gets a value indicating whether the collection is complete and empty.</summary>
        /// <param name="instance">The collection.</param><returns>Whether the collection is complete.</returns>
        public static bool get_IsCompleted(SystemConcurrent.BlockingCollection<T> instance)
        {
            CheckOwnership(instance);
            ExploreInterleaving();
            return instance.IsCompleted;
        }

#pragma warning restore IDE1006 // Naming Styles
#pragma warning restore SA1300 // Element should begin with upper-case letter
#pragma warning restore CA1707 // Identifiers should not contain underscores

        /// <summary>Adds an item, blocking until there is capacity.</summary>
        /// <param name="instance">The collection.</param><param name="item">The item to add.</param>
        public static void Add(SystemConcurrent.BlockingCollection<T> instance, T item) =>
            Add(instance, item, SystemCancellationToken.None);

        /// <summary>Adds an item, blocking until there is capacity.</summary>
        /// <param name="instance">The collection.</param><param name="item">The item to add.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public static void Add(SystemConcurrent.BlockingCollection<T> instance, T item,
            SystemCancellationToken cancellationToken)
        {
            if (instance is Wrapper wrapper)
            {
                // Add is TryAdd with an infinite timeout, except that a false result is impossible: it
                // either succeeds or throws (completed / cancelled / disposed).
                wrapper.TryAddItem(item, SystemTimeout.Infinite, cancellationToken);
                return;
            }

            instance.Add(item, cancellationToken);
        }

        /// <summary>Attempts to add an item without blocking.</summary>
        /// <param name="instance">The collection.</param><param name="item">The item to add.</param>
        /// <returns>Whether the item was added.</returns>
        public static bool TryAdd(SystemConcurrent.BlockingCollection<T> instance, T item) =>
            TryAdd(instance, item, 0, SystemCancellationToken.None);

        /// <summary>Attempts to add an item within the specified timeout.</summary>
        /// <param name="instance">The collection.</param><param name="item">The item to add.</param>
        /// <param name="timeout">The timeout.</param><returns>Whether the item was added.</returns>
        public static bool TryAdd(SystemConcurrent.BlockingCollection<T> instance, T item, TimeSpan timeout) =>
            TryAdd(instance, item, ToMilliseconds(timeout), SystemCancellationToken.None);

        /// <summary>Attempts to add an item within the specified timeout.</summary>
        /// <param name="instance">The collection.</param><param name="item">The item to add.</param>
        /// <param name="millisecondsTimeout">The timeout in milliseconds.</param>
        /// <returns>Whether the item was added.</returns>
        public static bool TryAdd(SystemConcurrent.BlockingCollection<T> instance, T item, int millisecondsTimeout) =>
            TryAdd(instance, item, millisecondsTimeout, SystemCancellationToken.None);

        /// <summary>Attempts to add an item within the specified timeout.</summary>
        /// <param name="instance">The collection.</param><param name="item">The item to add.</param>
        /// <param name="millisecondsTimeout">The timeout in milliseconds.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Whether the item was added.</returns>
        public static bool TryAdd(SystemConcurrent.BlockingCollection<T> instance, T item,
            int millisecondsTimeout, SystemCancellationToken cancellationToken)
        {
            if (instance is Wrapper wrapper)
            {
                return wrapper.TryAddItem(item, millisecondsTimeout, cancellationToken);
            }

            return instance.TryAdd(item, millisecondsTimeout, cancellationToken);
        }

        /// <summary>Removes an item, blocking until one is available.</summary>
        /// <param name="instance">The collection.</param><returns>The removed item.</returns>
        public static T Take(SystemConcurrent.BlockingCollection<T> instance) =>
            Take(instance, SystemCancellationToken.None);

        /// <summary>Removes an item, blocking until one is available.</summary>
        /// <param name="instance">The collection.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The removed item.</returns>
        public static T Take(SystemConcurrent.BlockingCollection<T> instance,
            SystemCancellationToken cancellationToken)
        {
            if (instance is Wrapper wrapper)
            {
                // Take is TryTake with an infinite timeout, except that a false result means the collection
                // completed while empty, which the real type reports by throwing.
                if (!wrapper.TryTakeItem(out T item, SystemTimeout.Infinite, cancellationToken))
                {
                    throw new InvalidOperationException(
                        "The collection argument is empty and has been marked as complete with regards to additions.");
                }

                return item;
            }

            return instance.Take(cancellationToken);
        }

        /// <summary>Attempts to remove an item without blocking.</summary>
        /// <param name="instance">The collection.</param><param name="item">The removed item.</param>
        /// <returns>Whether an item was removed.</returns>
        public static bool TryTake(SystemConcurrent.BlockingCollection<T> instance, out T item) =>
            TryTake(instance, out item, 0, SystemCancellationToken.None);

        /// <summary>Attempts to remove an item within the specified timeout.</summary>
        /// <param name="instance">The collection.</param><param name="item">The removed item.</param>
        /// <param name="timeout">The timeout.</param><returns>Whether an item was removed.</returns>
        public static bool TryTake(SystemConcurrent.BlockingCollection<T> instance, out T item, TimeSpan timeout) =>
            TryTake(instance, out item, ToMilliseconds(timeout), SystemCancellationToken.None);

        /// <summary>Attempts to remove an item within the specified timeout.</summary>
        /// <param name="instance">The collection.</param><param name="item">The removed item.</param>
        /// <param name="millisecondsTimeout">The timeout in milliseconds.</param>
        /// <returns>Whether an item was removed.</returns>
        public static bool TryTake(SystemConcurrent.BlockingCollection<T> instance, out T item,
            int millisecondsTimeout) => TryTake(instance, out item, millisecondsTimeout, SystemCancellationToken.None);

        /// <summary>Attempts to remove an item within the specified timeout.</summary>
        /// <param name="instance">The collection.</param><param name="item">The removed item.</param>
        /// <param name="millisecondsTimeout">The timeout in milliseconds.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Whether an item was removed.</returns>
        public static bool TryTake(SystemConcurrent.BlockingCollection<T> instance, out T item,
            int millisecondsTimeout, SystemCancellationToken cancellationToken)
        {
            if (instance is Wrapper wrapper)
            {
                return wrapper.TryTakeItem(out item, millisecondsTimeout, cancellationToken);
            }

            return instance.TryTake(out item, millisecondsTimeout, cancellationToken);
        }

        /// <summary>Marks the collection as not accepting any more additions.</summary>
        /// <param name="instance">The collection.</param>
        public static void CompleteAdding(SystemConcurrent.BlockingCollection<T> instance)
        {
            if (instance is Wrapper wrapper)
            {
                wrapper.CompleteAddingControlled();
                return;
            }

            instance.CompleteAdding();
        }

        /// <summary>Copies the items to an array.</summary>
        /// <param name="instance">The collection.</param><param name="array">The destination array.</param>
        /// <param name="index">The destination index.</param>
        public static void CopyTo(SystemConcurrent.BlockingCollection<T> instance, T[] array, int index)
        {
            CheckOwnership(instance);
            ExploreInterleaving();
            instance.CopyTo(array, index);
        }

        /// <summary>Copies the items to a new array.</summary>
        /// <param name="instance">The collection.</param><returns>The new array.</returns>
        public static T[] ToArray(SystemConcurrent.BlockingCollection<T> instance)
        {
            CheckOwnership(instance);
            ExploreInterleaving();
            return instance.ToArray();
        }

        /// <summary>Returns a consuming enumerable over the collection.</summary>
        /// <param name="instance">The collection.</param><returns>The consuming enumerable.</returns>
        public static IEnumerable<T> GetConsumingEnumerable(SystemConcurrent.BlockingCollection<T> instance) =>
            GetConsumingEnumerable(instance, SystemCancellationToken.None);

        /// <summary>Returns a consuming enumerable over the collection.</summary>
        /// <param name="instance">The collection.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The consuming enumerable.</returns>
        public static IEnumerable<T> GetConsumingEnumerable(SystemConcurrent.BlockingCollection<T> instance,
            SystemCancellationToken cancellationToken)
        {
            if (instance is Wrapper wrapper)
            {
                return ConsumeControlled(wrapper, cancellationToken);
            }

            return instance.GetConsumingEnumerable(cancellationToken);
        }

        /// <summary>Releases the resources used by the collection.</summary>
        /// <param name="instance">The collection.</param>
        public static void Dispose(SystemConcurrent.BlockingCollection<T> instance)
        {
            CheckOwnership(instance);
            ExploreInterleaving();
            instance.Dispose();
        }

        // The consuming enumerable, as an iterator over the CONTROLLED take. Each MoveNext is a blocking
        // take with an infinite timeout, so a consumer parked between items is a controlled pause rather
        // than an invisible native wait — which is the whole point of this type being modelled. Ends when
        // the collection is completed and empty, exactly as the real enumerable does.
        private static IEnumerable<T> ConsumeControlled(Wrapper wrapper, SystemCancellationToken cancellationToken)
        {
            while (wrapper.TryTakeItem(out T item, SystemTimeout.Infinite, cancellationToken))
            {
                yield return item;
            }
        }

        /// <summary>Takes an item from any of the collections, blocking until one is available.</summary>
        /// <param name="collections">The collections.</param><param name="item">The removed item.</param>
        /// <returns>The index of the collection the item came from.</returns>
        public static int TakeFromAny(SystemConcurrent.BlockingCollection<T>[] collections, out T item) =>
            TakeFromAny(collections, out item, SystemCancellationToken.None);

        /// <summary>Takes an item from any of the collections, blocking until one is available.</summary>
        /// <param name="collections">The collections.</param><param name="item">The removed item.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The index of the collection the item came from.</returns>
        public static int TakeFromAny(SystemConcurrent.BlockingCollection<T>[] collections, out T item,
            SystemCancellationToken cancellationToken)
        {
            if (!TryGetControlledSet(collections, out Wrapper[] wrappers, out CoyoteRuntime runtime))
            {
                return SystemConcurrent.BlockingCollection<T>.TakeFromAny(collections, out item, cancellationToken);
            }

            int index = TakeFromAnyControlled(runtime, wrappers, out item, SystemTimeout.Infinite, cancellationToken);
            if (index < 0)
            {
                throw new ArgumentException(
                    "All collections are marked as complete with regards to additions.", nameof(collections));
            }

            return index;
        }

        /// <summary>Attempts to take an item from any of the collections without blocking.</summary>
        /// <param name="collections">The collections.</param><param name="item">The removed item.</param>
        /// <returns>The index of the collection, or -1.</returns>
        public static int TryTakeFromAny(SystemConcurrent.BlockingCollection<T>[] collections, out T item) =>
            TryTakeFromAny(collections, out item, 0, SystemCancellationToken.None);

        /// <summary>Attempts to take an item from any of the collections within the timeout.</summary>
        /// <param name="collections">The collections.</param><param name="item">The removed item.</param>
        /// <param name="timeout">The timeout.</param><returns>The index of the collection, or -1.</returns>
        public static int TryTakeFromAny(SystemConcurrent.BlockingCollection<T>[] collections, out T item,
            TimeSpan timeout) => TryTakeFromAny(collections, out item, ToMilliseconds(timeout), SystemCancellationToken.None);

        /// <summary>Attempts to take an item from any of the collections within the timeout.</summary>
        /// <param name="collections">The collections.</param><param name="item">The removed item.</param>
        /// <param name="millisecondsTimeout">The timeout in milliseconds.</param>
        /// <returns>The index of the collection, or -1.</returns>
        public static int TryTakeFromAny(SystemConcurrent.BlockingCollection<T>[] collections, out T item,
            int millisecondsTimeout) => TryTakeFromAny(collections, out item, millisecondsTimeout, SystemCancellationToken.None);

        /// <summary>Attempts to take an item from any of the collections within the timeout.</summary>
        /// <param name="collections">The collections.</param><param name="item">The removed item.</param>
        /// <param name="millisecondsTimeout">The timeout in milliseconds.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The index of the collection, or -1.</returns>
        public static int TryTakeFromAny(SystemConcurrent.BlockingCollection<T>[] collections, out T item,
            int millisecondsTimeout, SystemCancellationToken cancellationToken)
        {
            if (!TryGetControlledSet(collections, out Wrapper[] wrappers, out CoyoteRuntime runtime))
            {
                return SystemConcurrent.BlockingCollection<T>.TryTakeFromAny(
                    collections, out item, millisecondsTimeout, cancellationToken);
            }

            return TakeFromAnyControlled(runtime, wrappers, out item, millisecondsTimeout, cancellationToken);
        }

        private static int TakeFromAnyControlled(CoyoteRuntime runtime, Wrapper[] wrappers, out T item,
            int millisecondsTimeout, SystemCancellationToken cancellationToken)
        {
            bool isUncontrolled = false;
            var wait = new Wait(runtime, millisecondsTimeout, cancellationToken);
            try
            {
                // One section for the whole cycle; see TryAddItem.
                using (runtime.EnterSynchronizedSection())
                {
                    while (true)
                    {
                        wait.ThrowIfCancellationRequested();

                        // Fast scan in INDEX ORDER. The first collection that yields an item wins, which is
                        // the priority contract callers rely on (the PipFlow bridge counts the returned
                        // index against its control-burst quota). A completed-and-empty collection is
                        // simply skipped: unlike the add side, a dead input does not poison the whole call.
                        bool anyOpen = false;
                        for (int idx = 0; idx < wrappers.Length; ++idx)
                        {
                            Wrapper wrapper = wrappers[idx];
                            if (wrapper.TryTakeImmediate(out item))
                            {
                                return idx;
                            }

                            anyOpen |= !wrapper.IsCompletedControlled;
                        }

                        if (!anyOpen)
                        {
                            // Every collection is completed and drained, so no further item can ever arrive.
                            item = default;
                            return -1;
                        }

                        // Nothing available. A zero timeout stops here — which is also the semantics of the
                        // parameterless overload, and the reason a poll loop over it is a busy spin rather
                        // than a wait.
                        if (!wait.CanPause)
                        {
                            item = default;
                            return -1;
                        }

                        // Register against EVERY open collection at once and pause on all of them. On
                        // waking, the loop re-scans from index 0 rather than consuming from whichever
                        // collection signalled: the item may already be gone, and index priority has to be
                        // re-applied.
                        if (!wait.PauseOn(wrappers, WaiterKind.Taker))
                        {
                            isUncontrolled = wait.IsUncontrolled;
                            break;
                        }
                    }
                }
            }
            finally
            {
                wait.Dispose();
            }

            if (!isUncontrolled)
            {
                item = default;
                return -1; // the timeout elapsed
            }

            // See Wrapper.TryAddItem. Array covariance lets the wrappers stand in for the real type, whose
            // static take-any applies the same index-order contract over the same base storage.
            return SystemConcurrent.BlockingCollection<T>.TryTakeFromAny(
                wrappers, out item, millisecondsTimeout, cancellationToken);
        }

        /// <summary>Adds an item to any of the collections, blocking until one has capacity.</summary>
        /// <param name="collections">The collections.</param><param name="item">The item to add.</param>
        /// <returns>The index of the collection the item was added to.</returns>
        public static int AddToAny(SystemConcurrent.BlockingCollection<T>[] collections, T item) =>
            AddToAny(collections, item, SystemCancellationToken.None);

        /// <summary>Adds an item to any of the collections, blocking until one has capacity.</summary>
        /// <param name="collections">The collections.</param><param name="item">The item to add.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The index of the collection the item was added to.</returns>
        public static int AddToAny(SystemConcurrent.BlockingCollection<T>[] collections, T item,
            SystemCancellationToken cancellationToken)
        {
            if (!TryGetControlledSet(collections, out Wrapper[] wrappers, out CoyoteRuntime runtime))
            {
                return SystemConcurrent.BlockingCollection<T>.AddToAny(collections, item, cancellationToken);
            }

            return AddToAnyControlled(runtime, wrappers, item, SystemTimeout.Infinite, cancellationToken);
        }

        /// <summary>Attempts to add an item to any of the collections without blocking.</summary>
        /// <param name="collections">The collections.</param><param name="item">The item to add.</param>
        /// <returns>The index of the collection, or -1.</returns>
        public static int TryAddToAny(SystemConcurrent.BlockingCollection<T>[] collections, T item) =>
            TryAddToAny(collections, item, 0, SystemCancellationToken.None);

        /// <summary>Attempts to add an item to any of the collections within the timeout.</summary>
        /// <param name="collections">The collections.</param><param name="item">The item to add.</param>
        /// <param name="timeout">The timeout.</param><returns>The index of the collection, or -1.</returns>
        public static int TryAddToAny(SystemConcurrent.BlockingCollection<T>[] collections, T item, TimeSpan timeout) =>
            TryAddToAny(collections, item, ToMilliseconds(timeout), SystemCancellationToken.None);

        /// <summary>Attempts to add an item to any of the collections within the timeout.</summary>
        /// <param name="collections">The collections.</param><param name="item">The item to add.</param>
        /// <param name="millisecondsTimeout">The timeout in milliseconds.</param>
        /// <returns>The index of the collection, or -1.</returns>
        public static int TryAddToAny(SystemConcurrent.BlockingCollection<T>[] collections, T item,
            int millisecondsTimeout) => TryAddToAny(collections, item, millisecondsTimeout, SystemCancellationToken.None);

        /// <summary>Attempts to add an item to any of the collections within the timeout.</summary>
        /// <param name="collections">The collections.</param><param name="item">The item to add.</param>
        /// <param name="millisecondsTimeout">The timeout in milliseconds.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The index of the collection, or -1.</returns>
        public static int TryAddToAny(SystemConcurrent.BlockingCollection<T>[] collections, T item,
            int millisecondsTimeout, SystemCancellationToken cancellationToken)
        {
            if (!TryGetControlledSet(collections, out Wrapper[] wrappers, out CoyoteRuntime runtime))
            {
                return SystemConcurrent.BlockingCollection<T>.TryAddToAny(
                    collections, item, millisecondsTimeout, cancellationToken);
            }

            return AddToAnyControlled(runtime, wrappers, item, millisecondsTimeout, cancellationToken);
        }

        // NOT a mirror of the take side, and deliberately so — the BCL's add-any differs on two points that
        // a symmetric implementation would silently get wrong:
        //
        //   * an UNBOUNDED collection wins through the fast path even when an earlier bounded collection
        //     currently has space, so index order is not the whole story; and
        //   * ANY input marked complete for adding poisons the whole call with an ArgumentException, where
        //     the take side merely skips such a collection. That validation also happens before the
        //     cancellation check.
        private static int AddToAnyControlled(CoyoteRuntime runtime, Wrapper[] wrappers, T item,
            int millisecondsTimeout, SystemCancellationToken cancellationToken)
        {
            // Completion validation first, and over the WHOLE array, before anything else observes the
            // token. A single completed input invalidates the call however healthy its siblings are.
            for (int idx = 0; idx < wrappers.Length; ++idx)
            {
                if (wrappers[idx].IsAddingCompletedControlled)
                {
                    throw new ArgumentException(
                        "At least one of the specified collections has been marked as complete with regards to additions.",
                        nameof(wrappers));
                }
            }

            bool isUncontrolled = false;
            var wait = new Wait(runtime, millisecondsTimeout, cancellationToken);
            try
            {
                // One section for the whole cycle; see TryAddItem.
                using (runtime.EnterSynchronizedSection())
                {
                    while (true)
                    {
                        wait.ThrowIfCancellationRequested();

                        // The unbounded fast path, ahead of index order.
                        for (int idx = 0; idx < wrappers.Length; ++idx)
                        {
                            if (wrappers[idx].IsUnbounded && wrappers[idx].TryAddImmediate(item))
                            {
                                return idx;
                            }
                        }

                        for (int idx = 0; idx < wrappers.Length; ++idx)
                        {
                            if (!wrappers[idx].IsUnbounded && wrappers[idx].TryAddImmediate(item))
                            {
                                return idx;
                            }
                        }

                        if (!wait.CanPause)
                        {
                            return -1;
                        }

                        if (!wait.PauseOn(wrappers, WaiterKind.Adder))
                        {
                            isUncontrolled = wait.IsUncontrolled;
                            break;
                        }
                    }
                }
            }
            finally
            {
                wait.Dispose();
            }

            if (!isUncontrolled)
            {
                return -1; // the timeout elapsed
            }

            // See Wrapper.TryAddItem. The completion validation above has already run, so this is only
            // ever reached for an array the real add-any would also accept.
            return SystemConcurrent.BlockingCollection<T>.TryAddToAny(
                wrappers, item, millisecondsTimeout, cancellationToken);
        }

        // Holds a collection against the runtime that is current, for the members that have no other reason
        // to resolve one.
        //
        // Applied to the plain reads as well as the mutations, which is a deliberate departure from
        // SemaphoreSlim — its CurrentCount and AvailableWaitHandle skip the equivalent check. The reasoning
        // there does not carry over: this type hands out items rather than a count, so a read from a
        // collection belonging to a finished iteration is a program that is quietly sharing state across
        // iterations, and saying so at the read is far more use than leaving the first symptom to be a hang
        // somewhere else. It costs nothing when the collection is the current runtime's, and when it is not
        // the runtime resolved is a detached one whose reporting is already a no-op.
        private static void CheckOwnership(SystemConcurrent.BlockingCollection<T> instance)
        {
            if (instance is Wrapper wrapper)
            {
                wrapper.GetRuntime();
            }
        }

        // Adds a scheduling point around an operation that does not block, so the scheduler can interleave
        // around reads of shared collection state.
        private static void ExploreInterleaving()
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                runtime.TryGetExecutingOperation(out ControlledOperation current))
            {
                runtime.ScheduleNextOperation(current, SchedulingPointType.Default);
            }
        }

        private static int ToMilliseconds(TimeSpan timeout)
        {
            long total = (long)timeout.TotalMilliseconds;
            if (total < -1 || total > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            return (int)total;
        }

        // Validates the array against the BCL's own rules and decides whether this call can be controlled.
        // A MIXED array — some wrappers, some real collections, or wrappers from a previous test iteration
        // — is an assertion failure rather than a silent half-controlled wait, because half of such a wait
        // would be invisible to the scheduler and would reintroduce exactly the hang this type prevents.
        private static bool TryGetControlledSet(SystemConcurrent.BlockingCollection<T>[] collections,
            out Wrapper[] wrappers, out CoyoteRuntime runtime)
        {
            wrappers = null;
            runtime = null;
            if (collections is null)
            {
                throw new ArgumentNullException(nameof(collections));
            }

            if (collections.Length < 1)
            {
                throw new ArgumentException("The collections argument is empty.", nameof(collections));
            }

            int maxCollections = System.Threading.Thread.CurrentThread.GetApartmentState() == System.Threading.ApartmentState.STA
                ? MaxAnyCollectionsSta
                : MaxAnyCollectionsMta;
            if (collections.Length > maxCollections)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(collections),
                    $"The number of collections must not exceed {maxCollections} on this thread.");
            }

            runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy != SchedulingPolicy.Interleaving)
            {
                return false;
            }

            var controlled = new Wrapper[collections.Length];
            int wrapperCount = 0;
            for (int idx = 0; idx < collections.Length; ++idx)
            {
                if (collections[idx] is null)
                {
                    throw new ArgumentException("The collections argument contains a null element.", nameof(collections));
                }

                if (collections[idx] is Wrapper wrapper)
                {
                    // EVERY element is held against the runtime, not just the first. Checking only
                    // 'wrappers[0]' left a collection from a finished iteration invisible for as long as
                    // something else occupied index 0 — and it is the waiter sets and resource ids of that
                    // dead runtime which the wait then registers against. This loop already walks the whole
                    // array and already reports the mixed-array case, so it is the one place that can make
                    // the omission structurally impossible rather than merely fixed.
                    wrapper.CheckOwnedBy(runtime);
                    controlled[idx] = wrapper;
                    wrapperCount++;
                }
            }

            if (wrapperCount is 0)
            {
                return false;
            }

            if (wrapperCount != collections.Length)
            {
                runtime.NotifyAssertionFailure(
                    "Invoking a 'BlockingCollection' take-any or add-any operation over a mix of controlled and " +
                    "uncontrolled collections is not supported in systematic testing: the uncontrolled half of " +
                    "the wait would be invisible to the scheduler.");

                // NotifyAssertionFailure detaches the execution, but it can also simply return, so do not
                // depend on it unwinding. Falling back to the uncontrolled path keeps the failure the
                // assertion above rather than a NullReferenceException on the half-filled array.
                return false;
            }

            wrappers = controlled;
            return true;
        }

        /// <summary>Identifies which of a collection's two waiter sets an operation belongs to.</summary>
        private enum WaiterKind
        {
            /// <summary>Waiting for an item to become available.</summary>
            Taker,

            /// <summary>Waiting for capacity to become available.</summary>
            Adder,
        }

        /// <summary>
        /// One in-flight controlled wait. Owns the timeout budget, the cancellation registration and the
        /// waiter-set memberships, and guarantees they are all released on every exit path — a stale entry
        /// left in a collection's waiter set would misdirect a later signal to an operation that is no
        /// longer waiting for it.
        /// </summary>
        private sealed class Wait : IDisposable
        {
            private readonly CoyoteRuntime Runtime;
            private readonly SystemCancellationToken CancellationToken;
            private readonly bool IsInfinite;
            private readonly List<Wrapper> Registered = new List<Wrapper>();
            private SystemCancellationTokenRegistration Registration;
            private bool IsCancellationRegistered;
            private WaiterKind RegisteredKind;
            private ControlledOperation Operation;
            private uint RemainingBudget;
            private bool IsExpired;

            internal Wait(CoyoteRuntime runtime, int millisecondsTimeout, SystemCancellationToken cancellationToken)
            {
                if (millisecondsTimeout < -1)
                {
                    throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));
                }

                this.Runtime = runtime;
                this.CancellationToken = cancellationToken;
                this.IsInfinite = millisecondsTimeout is SystemTimeout.Infinite;

                if (millisecondsTimeout is 0)
                {
                    this.IsExpired = true;
                }
                else if (!this.IsInfinite)
                {
                    // The requested duration only distinguishes zero from non-zero; the actual wait is an
                    // ABSTRACT step budget drawn from Configuration.TimeoutDelay, exactly as Thread.Sleep
                    // and Task.Delay do under this runtime. There is no clock to honour milliseconds
                    // against, so a proportional mapping would be false precision. A budget of zero means
                    // the scheduler chose "this timeout has already elapsed".
                    this.RemainingBudget = (uint)runtime.GetNextNondeterministicIntegerChoice(
                        (int)runtime.Configuration.TimeoutDelay, null, null);
                    this.IsExpired = this.RemainingBudget is 0;
                }
            }

            /// <summary>Gets a value indicating whether this wait may still pause.</summary>
            internal bool CanPause => !this.IsExpired;

            /// <summary>
            /// Gets a value indicating whether this wait gave up because the calling thread has no
            /// controlled operation to park.
            /// </summary>
            /// <remarks>
            /// <see cref="PauseOn"/> returns false for two unrelated reasons — the timeout elapsed, or
            /// there is no operation to pause — and a caller that cannot tell them apart has to guess.
            /// Every guess made here was wrong: an add reported success having dropped the item, a take
            /// reported that the collection had been marked complete when it had not, and a consuming
            /// enumerable simply ended. Which of the two happened is knowable, so it is recorded rather
            /// than inferred.
            /// </remarks>
            internal bool IsUncontrolled { get; private set; }

            internal void ThrowIfCancellationRequested() => this.CancellationToken.ThrowIfCancellationRequested();

            /// <summary>
            /// Registers against every eligible collection and pauses until one of them signals, the token
            /// is cancelled, or the timeout budget runs out. Returns false only when the timeout elapsed.
            /// </summary>
            /// <param name="wrappers">The collections to await.</param>
            /// <param name="kind">Which waiter set to join.</param>
            /// <returns><see langword="true"/> when the wait should re-scan; <see langword="false"/> on timeout.</returns>
            internal bool PauseOn(Wrapper[] wrappers, WaiterKind kind)
            {
                using (this.Runtime.EnterSynchronizedSection())
                {
                    if (!this.Runtime.TryGetExecutingOperation(out ControlledOperation current))
                    {
                        this.Runtime.NotifyUncontrolledSynchronizationInvocation("BlockingCollection wait");
                        this.IsUncontrolled = true;
                        return false;
                    }

                    this.Operation = current;
                    this.RegisteredKind = kind;

                    var resources = new List<Guid>(wrappers.Length);
                    for (int idx = 0; idx < wrappers.Length; ++idx)
                    {
                        Wrapper wrapper = wrappers[idx];
                        wrapper.AddWaiter(current, kind);
                        this.Registered.Add(wrapper);
                        resources.Add(wrapper.ResourceIdFor(kind));
                    }

                    // Registered ONCE for the whole wait, not once per pause: this method is called again for
                    // every re-scan, and re-registering would overwrite (and leak) the previous callback.
                    //
                    // The token may fire between the caller's check and this registration, so re-check
                    // after registering; otherwise a cancellation that lands in that window would leave
                    // this operation parked with nothing left to wake it.
                    if (this.CancellationToken.CanBeCanceled && !this.IsCancellationRegistered)
                    {
                        this.IsCancellationRegistered = true;
                        this.Registration = this.CancellationToken.Register(this.OnCancelled);
                        if (this.CancellationToken.IsCancellationRequested)
                        {
                            // Release only the WAITER SETS here. Disposing the cancellation registration
                            // must not happen under the runtime lock: Dispose waits for an in-flight
                            // callback, and OnCancelled takes that same lock, so doing it here would
                            // deadlock the test host. The caller's finally disposes it outside this section.
                            this.ReleaseWaiterSets();
                            this.CancellationToken.ThrowIfCancellationRequested();
                        }
                    }

                    if (this.IsInfinite)
                    {
                        current.PauseWithResources(resources, waitForAll: false);
                    }
                    else
                    {
                        current.PauseWithResourcesOrDelay(resources, this.RemainingBudget);
                    }

                    this.Runtime.ScheduleNextOperation(current, SchedulingPointType.Pause);

                    // Awake again. A zero remaining budget means the delay is what enabled us, so the
                    // timeout fired; anything else is a resource (or cancellation) wake and the remainder
                    // carries over. Carrying it is what stops a waiter that is repeatedly woken and then
                    // beaten to the item from restarting its timeout each time and waiting forever.
                    this.ReleaseWaiterSets();
                    if (!this.IsInfinite)
                    {
                        this.RemainingBudget = (uint)Math.Max(0, current.DelayedStepsCount);
                        if (this.RemainingBudget is 0)
                        {
                            this.IsExpired = true;
                            return false;
                        }
                    }

                    return true;
                }
            }

            public void Dispose()
            {
                // Every caller reaches here from a finally that sits OUTSIDE its synchronized section, and
                // it has to stay that way: the registration disposal below waits for an in-flight
                // OnCancelled, which takes the same lock. A nested enter is a no-op that does not release
                // on dispose, so a caller that wrapped its finally would still be holding the lock here and
                // would deadlock the test host — with the periodic monitor blocked on that same lock and
                // therefore unable to report it. This asserts the contract rather than trusting it.
                Debug.Assert(!CoyoteRuntime.IsExecutionSynchronized,
                    "Disposing a 'BlockingCollection' wait while holding the runtime lock would deadlock the test host.");

                // The waiter sets themselves are runtime state and are released under the lock; a stale
                // entry left behind would misdirect a later signal to an operation that is no longer
                // waiting for it.
                using (this.Runtime.EnterSynchronizedSection())
                {
                    this.ReleaseWaiterSets();
                }

                SystemCancellationTokenRegistration registration = this.Registration;
                this.Registration = default;
                registration.Dispose();
            }

            private void OnCancelled()
            {
                using (this.Runtime.EnterSynchronizedSection())
                {
                    ControlledOperation operation = this.Operation;
                    if (operation is null)
                    {
                        return;
                    }

                    // Enable through every resource this operation is parked on; whichever call wins is the
                    // single wake, and the rest are no-ops because the status is no longer paused.
                    for (int idx = 0; idx < this.Registered.Count; ++idx)
                    {
                        operation.TryEnable(this.Registered[idx].ResourceIdFor(this.RegisteredKind));
                    }
                }
            }

            private void ReleaseWaiterSets()
            {
                ControlledOperation operation = this.Operation;
                if (operation is null)
                {
                    return;
                }

                for (int idx = 0; idx < this.Registered.Count; ++idx)
                {
                    this.Registered[idx].RemoveWaiter(operation, this.RegisteredKind);
                }

                this.Registered.Clear();
            }
        }

        /// <summary>
        /// A <see cref="SystemConcurrent.BlockingCollection{T}"/> whose blocking is controlled by the
        /// runtime. Storage is delegated to the base type through its non-blocking operations only.
        /// </summary>
        private sealed class Wrapper : SystemConcurrent.BlockingCollection<T>
        {
            // The creating runtime, held directly rather than resolved through CoyoteRuntime.Current on
            // demand. Signalling has to enter that runtime's synchronized section, and the two paths that
            // signal outside a controlled operation — completion and disposal — are exactly the ones most
            // likely to run once the iteration has moved on, where Current resolves to the default runtime
            // instead. Entering the creating runtime's lock is always safe; enabling operations belonging
            // to a runtime that has detached is a no-op rather than a corruption.
            private readonly CoyoteRuntime Runtime;
            private readonly Guid RuntimeId;
            private readonly string DebugName;

            // Two resource ids, not one: takers wake on an add and adders wake on a take, so a single id
            // would wake the wrong half of the waiters on every signal.
            internal readonly Guid ItemsAvailableResourceId;
            internal readonly Guid SpaceAvailableResourceId;

            private readonly HashSet<ControlledOperation> Takers = new HashSet<ControlledOperation>();
            private readonly HashSet<ControlledOperation> Adders = new HashSet<ControlledOperation>();

            /// <summary>
            /// Initializes a new instance of the <see cref="Wrapper"/> class that is UNBOUNDED, through
            /// the base type's own capacity-less constructor.
            /// </summary>
            /// <remarks>
            /// Two constructors rather than one taking a sentinel, because the base type does not treat
            /// "no bound" and "a very large bound" as the same thing. Its capacity-less constructor maps
            /// to an internal NON_BOUNDED mode that reports <c>BoundedCapacity</c> as -1 — the documented
            /// contract — and skips building the free-slot semaphore entirely. Passing
            /// <see cref="int.MaxValue"/> instead reported a bound that no caller asked for, and left a
            /// live semaphore for every add and take to acquire and release for no purpose. Since this
            /// type's whole premise is that bounded-capacity accounting stays the BCL's own, it has to be
            /// constructed the way the caller asked for.
            /// </remarks>
            internal Wrapper(CoyoteRuntime runtime, SystemConcurrent.IProducerConsumerCollection<T> collection)
                : base(collection)
            {
                this.Runtime = runtime;
                this.RuntimeId = runtime.Id;
                this.ItemsAvailableResourceId = Guid.NewGuid();
                this.SpaceAvailableResourceId = Guid.NewGuid();
                this.IsUnbounded = true;
                this.DebugName = $"BlockingCollection({this.ItemsAvailableResourceId})";
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="Wrapper"/> class that is BOUNDED. See the
            /// unbounded overload for why the two are separate.
            /// </summary>
            internal Wrapper(CoyoteRuntime runtime, SystemConcurrent.IProducerConsumerCollection<T> collection,
                int boundedCapacity)
                : base(collection, boundedCapacity)
            {
                this.Runtime = runtime;
                this.RuntimeId = runtime.Id;
                this.ItemsAvailableResourceId = Guid.NewGuid();
                this.SpaceAvailableResourceId = Guid.NewGuid();
                this.IsUnbounded = false;
                this.DebugName = $"BlockingCollection({this.ItemsAvailableResourceId})";
            }

            /// <summary>Gets a value indicating whether this collection was created without a bound.</summary>
            internal bool IsUnbounded { get; }

            /// <summary>Gets a value indicating whether adding has been marked complete.</summary>
            internal bool IsAddingCompletedControlled => this.IsAddingCompleted;

            /// <summary>Gets a value indicating whether the collection is complete and drained.</summary>
            internal bool IsCompletedControlled => this.IsCompleted;

            internal Guid ResourceIdFor(WaiterKind kind) =>
                kind is WaiterKind.Taker ? this.ItemsAvailableResourceId : this.SpaceAvailableResourceId;

            internal void AddWaiter(ControlledOperation operation, WaiterKind kind)
            {
                if (kind is WaiterKind.Taker)
                {
                    this.Takers.Add(operation);
                }
                else
                {
                    this.Adders.Add(operation);
                }
            }

            internal void RemoveWaiter(ControlledOperation operation, WaiterKind kind)
            {
                if (kind is WaiterKind.Taker)
                {
                    this.Takers.Remove(operation);
                }
                else
                {
                    this.Adders.Remove(operation);
                }
            }

            /// <summary>Adds without blocking, using the base type's own storage and bounds.</summary>
            internal bool TryAddImmediate(T item)
            {
                // The inherited non-virtual TryAdd, which is the base type's own storage and bound check.
                // It never parks, so it is safe to call from inside a controlled operation.
                if (!this.TryAdd(item))
                {
                    return false;
                }

                this.Signal(WaiterKind.Taker);
                return true;
            }

            /// <summary>Takes without blocking, using the base type's own storage.</summary>
            internal bool TryTakeImmediate(out T item)
            {
                // The inherited non-virtual TryTake; likewise non-blocking.
                if (!this.TryTake(out item))
                {
                    return false;
                }

                this.Signal(WaiterKind.Adder);
                return true;
            }

            internal bool TryAddItem(T item, int millisecondsTimeout, SystemCancellationToken cancellationToken)
            {
                var wrappers = new[] { this };
                CoyoteRuntime runtime = this.GetRuntime();
                bool isUncontrolled = false;
                var wait = new Wait(runtime, millisecondsTimeout, cancellationToken);
                try
                {
                    // ONE section for the whole attempt-register-park cycle, which is the shape
                    // SemaphoreSlim.Enter uses. Two things depend on it. The signal that follows a
                    // successful add mutates waiter sets and operation state that only this lock orders
                    // against the scheduler. And a FAILED attempt must reach its registration without
                    // anything slipping in between: a completion or a competing take landing in that gap
                    // would signal a waiter set this operation has not joined yet, and it would then park
                    // with nothing left to wake it.
                    using (runtime.EnterSynchronizedSection())
                    {
                        while (true)
                        {
                            wait.ThrowIfCancellationRequested();

                            // The base throws InvalidOperationException when adding is complete, which is
                            // the real type's contract and is deliberately not intercepted.
                            if (this.TryAddImmediate(item))
                            {
                                return true;
                            }

                            if (!wait.CanPause || !wait.PauseOn(wrappers, WaiterKind.Adder))
                            {
                                isUncontrolled = wait.IsUncontrolled;
                                break;
                            }
                        }
                    }
                }
                finally
                {
                    // OUTSIDE the section above, and it has to stay that way: disposing the wait disposes
                    // a cancellation registration, which waits for an in-flight callback that takes this
                    // same lock.
                    wait.Dispose();
                }

                if (!isUncontrolled)
                {
                    return false; // the timeout elapsed
                }

                // The caller has no controlled operation, so there is no park for the scheduler to model
                // and nothing this type can usefully do with the wait. What it must not do is any of the
                // things it used to: the item is not dropped and success is not reported for an add that
                // never happened. Deferring to the real type completes the operation for real, honouring
                // the original timeout, and it is safe precisely because storage was delegated to the base
                // all along — its own semaphores have been maintained by every controlled add and take, so
                // a wait parked here is released by them.
                //
                // Note this runs OUTSIDE the section: a genuine block while holding the runtime lock would
                // stall every controlled thread and the deadlock monitor with them.
                // The inherited BLOCKING overload, unlike the zero-timeout one TryAddImmediate uses.
                return this.TryAdd(item, millisecondsTimeout, cancellationToken);
            }

            internal bool TryTakeItem(out T item, int millisecondsTimeout, SystemCancellationToken cancellationToken)
            {
                var wrappers = new[] { this };
                CoyoteRuntime runtime = this.GetRuntime();
                bool isUncontrolled = false;
                var wait = new Wait(runtime, millisecondsTimeout, cancellationToken);
                try
                {
                    // See TryAddItem for why this is one section. The completion check below makes the
                    // point sharper on this side: read outside the section that registers the waiter, a
                    // completion arriving between the two leaves this operation parked on a collection
                    // that is already drained and closed.
                    using (runtime.EnterSynchronizedSection())
                    {
                        while (true)
                        {
                            wait.ThrowIfCancellationRequested();

                            if (this.TryTakeImmediate(out item))
                            {
                                return true;
                            }

                            if (this.IsCompleted)
                            {
                                // Completed AND drained: no further item can arrive, so waiting would be a
                                // deadlock rather than a timeout.
                                item = default;
                                return false;
                            }

                            if (!wait.CanPause || !wait.PauseOn(wrappers, WaiterKind.Taker))
                            {
                                isUncontrolled = wait.IsUncontrolled;
                                break;
                            }
                        }
                    }
                }
                finally
                {
                    wait.Dispose();
                }

                if (!isUncontrolled)
                {
                    item = default;
                    return false; // the timeout elapsed
                }

                // See TryAddItem. A false from here now means what the caller reads it to mean — the
                // collection completed while empty — rather than doubling as "this thread could not be
                // parked", which is what made Take report a completion that had never happened.
                // The inherited BLOCKING overload, unlike the zero-timeout one TryTakeImmediate uses.
                return this.TryTake(out item, millisecondsTimeout, cancellationToken);
            }

            internal void CompleteAddingControlled()
            {
                // Held against the current runtime before anything is mutated, as the blocking members are:
                // completion wakes both waiter sets, and doing that to the waiters of a runtime that has
                // already finished is exactly what this reports.
                this.GetRuntime();

                // Deliberately OUTSIDE the section below. The base type spin-waits for in-flight adders to
                // drain, and spinning while holding the runtime lock would stall every controlled thread
                // and the periodic deadlock monitor along with them — converting a reportable hang into a
                // silent one. Completion is monotonic, so sequencing it before the signal is all the
                // ordering this needs.
                this.CompleteAdding();

                using (this.Runtime.EnterSynchronizedSection())
                {
                    // Completion wakes BOTH halves: takers so they can observe the drained-and-complete
                    // state and stop waiting, adders so their pending add fails fast instead of waiting for
                    // capacity that would never be usable.
                    this.Signal(WaiterKind.Taker);
                    this.Signal(WaiterKind.Adder);
                }
            }

            /// <summary>Returns the runtime, asserting it is the one that created this collection.</summary>
            internal CoyoteRuntime GetRuntime()
            {
                var runtime = CoyoteRuntime.Current;
                this.CheckOwnedBy(runtime);
                return runtime;
            }

            /// <summary>Asserts that the specified runtime is the one that created this collection.</summary>
            /// <remarks>
            /// Split out from <see cref="GetRuntime"/> for the take-any and add-any paths, which resolve
            /// the runtime once and then have to hold every collection in the array against it rather than
            /// just whichever one happened to be first.
            /// </remarks>
            internal void CheckOwnedBy(CoyoteRuntime runtime)
            {
                if (runtime.Id != this.RuntimeId)
                {
                    var trace = new StackTrace();
                    runtime.NotifyAssertionFailure($"Accessing '{this.DebugName}' that was created in a " +
                        $"previous test iteration with runtime id '{this.RuntimeId}':\n{trace}");
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    // A disposed collection can never signal again, so release every waiter first. The
                    // creating runtime's lock is used rather than the current one: disposal is the member
                    // most likely to run once the iteration that built this collection has finished.
                    using (this.Runtime.EnterSynchronizedSection())
                    {
                        this.Signal(WaiterKind.Taker);
                        this.Signal(WaiterKind.Adder);
                    }
                }

                base.Dispose(disposing);
            }

            // Enables every operation waiting on this half, and prunes the ones that are no longer paused.
            // The first TryEnable to match is the single winner; later calls see a non-paused status and do
            // nothing, so two concurrent signals cannot both claim the same waiter.
            //
            // It is assumed that this method runs in the scope of the runtime SynchronizedSection, as the
            // equivalent methods on WaitHandle, SemaphoreSlim and ReaderWriterLockSlim all are. Neither the
            // waiter sets below nor the operation state that TryEnable writes is volatile or interlocked,
            // and the periodic deadlock monitor reads that state from a thread of its own, so the lock is
            // the only thing ordering any of it.
            private void Signal(WaiterKind kind)
            {
                Debug.Assert(CoyoteRuntime.IsExecutionSynchronized,
                    "Signalling a 'BlockingCollection' waiter set outside the runtime's synchronized section.");

                HashSet<ControlledOperation> waiters = kind is WaiterKind.Taker ? this.Takers : this.Adders;
                if (waiters.Count is 0)
                {
                    return;
                }

                Guid resourceId = this.ResourceIdFor(kind);
                var enabled = new List<ControlledOperation>(waiters.Count);
                foreach (ControlledOperation operation in waiters)
                {
                    if (operation.TryEnable(resourceId) || !operation.IsPaused)
                    {
                        enabled.Add(operation);
                    }
                }

                for (int idx = 0; idx < enabled.Count; ++idx)
                {
                    waiters.Remove(enabled[idx]);
                }
            }
        }
    }
#pragma warning restore CA1000 // Do not declare static members on generic types
}

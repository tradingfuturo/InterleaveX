// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

using System;
using Microsoft.Coyote.Runtime;
using SystemGenerics = System.Collections.Generic;
using SystemThread = System.Threading.Thread;

namespace Microsoft.Coyote.Rewriting.Types.Collections
{
    /// <summary>
    /// Detects unsynchronized concurrent access to one modelled collection instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The guarded window spans the REAL collection operation, not merely the entry to it. Each shim
    /// opens a <see cref="Scope"/>, delegates to the underlying collection, and disposes the scope
    /// afterwards, so two operations that are simultaneously inside the collection are detected
    /// directly. An earlier design incremented a counter, took one scheduling point and decremented
    /// again BEFORE delegating: it could only observe a race when the other thread happened to reach
    /// its own check during that single scheduling point, and was blind to the far more common case
    /// where both threads were preempted inside the operations themselves (the preemption typically
    /// comes from the key's own <see cref="object.GetHashCode"/>, which runs inside the operation).
    /// </para>
    /// <para>
    /// Reader/reader overlap is legal and is not reported; a writer excludes everything, a reader
    /// excludes writers only.
    /// </para>
    /// <para>
    /// Because the window is open across the delegation, user code invoked BY the collection —
    /// <see cref="object.GetHashCode"/>, <see cref="object.Equals(object)"/>, an
    /// <see cref="SystemGenerics.IEqualityComparer{T}"/>, or a comparison/action delegate passed to
    /// <c>Sort</c>/<c>ForEach</c>/<c>FindAll</c> — runs inside it, and such a callback is free to touch
    /// the same collection again. That nesting is re-entry by one logical operation, never concurrency,
    /// so a frame whose owner already holds one is EXEMPT: it asserts nothing, counts nothing and takes
    /// no scheduling point, and only the outermost frame releases. Without the exemption every
    /// reentrant comparer would report a race against itself.
    /// </para>
    /// <para>
    /// Known and accepted gap: an exempt nested WRITE inside a READ frame leaves only the read visible
    /// for its duration, so under <see cref="SchedulingPolicy.Fuzzing"/> a genuinely parallel reader
    /// overlapping that instant is not reported. Reaching it requires a comparer that mutates the very
    /// collection it is comparing for, which is already undefined behaviour.
    /// </para>
    /// </remarks>
    internal sealed class DataRaceChecker
    {
        /// <summary>
        /// Guards every field below. A real monitor: this assembly is never rewritten, and under
        /// <see cref="SchedulingPolicy.Fuzzing"/> the threads contending it are genuinely parallel.
        /// Held only for bookkeeping, never across the collection operation or the scheduling point,
        /// and nothing that holds the runtime lock can reach a collection shim, so it cannot invert.
        /// </summary>
        private readonly object SyncObject = new object();

        /// <summary>
        /// The modelled collection type, used to name it in an assertion failure.
        /// </summary>
        private readonly Type CollectionType;

        /// <summary>
        /// Re-entrancy depth per frame owner, where an owner is the executing
        /// <see cref="ControlledOperation"/> or, on an uncontrolled thread, the thread itself.
        /// </summary>
        private readonly SystemGenerics.Dictionary<object, int> ActiveFrames =
            new SystemGenerics.Dictionary<object, int>();

        /// <summary>
        /// Count of frames currently reading the collection.
        /// </summary>
        private int ReaderCount;

        /// <summary>
        /// Count of frames currently writing the collection.
        /// </summary>
        private int WriterCount;

        /// <summary>
        /// Generation of the runtime that owns the current counts.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A collection cached in a static field outlives the iteration that created it, and an
        /// iteration that detaches part way through an operation abandons its frame without disposing
        /// the scope. Stamping the owning runtime lets the next iteration discard both rather than
        /// inherit a permanently non-zero count. Mirrors how a synchronized block heals itself.
        /// </para>
        /// <para>
        /// Stamped with the generation rather than the identifier, because taking the state over must
        /// be something only a YOUNGER runtime can do. A thread left behind by an iteration that has
        /// already ended still resolves its own, dead runtime, so under an identifier it is
        /// indistinguishable from a new iteration arriving: it would clear the frames the live
        /// iteration is holding, and the live frames would then release counts they no longer own,
        /// leaving real races unreported for as long as it kept running.
        /// </para>
        /// </remarks>
        private long Generation;

        /// <summary>
        /// Initializes a new instance of the <see cref="DataRaceChecker"/> class.
        /// </summary>
        /// <param name="collectionType">The modelled collection type, named in assertion failures.</param>
        internal DataRaceChecker(Type collectionType)
        {
            this.CollectionType = collectionType;
        }

        /// <summary>
        /// Declares that the calling frame is about to access the collection, and returns the scope
        /// that must be disposed once the access completes.
        /// </summary>
        /// <param name="isWriteAccess">True if the access can modify the collection.</param>
        /// <returns>The scope guarding this access.</returns>
        internal Scope Enter(bool isWriteAccess)
        {
            CoyoteRuntime runtime = CoyoteRuntime.Current;
            return this.Enter(runtime, runtime.Generation, IsAccountable(runtime), isWriteAccess);
        }

        /// <summary>
        /// Declares that the calling frame is about to access the collection on behalf of the runtime
        /// of the specified generation, and returns the scope that must be disposed once the access
        /// completes.
        /// </summary>
        /// <remarks>
        /// The identity and the standing of the calling runtime are passed in rather than read here,
        /// so that what this method does with a caller that has no business touching the state can be
        /// stated directly, instead of only through a race that has to be provoked.
        /// </remarks>
        /// <param name="runtime">The runtime the calling frame belongs to.</param>
        /// <param name="generation">The generation of that runtime.</param>
        /// <param name="isAccountable">True if that runtime may own frames here.</param>
        /// <param name="isWriteAccess">True if the access can modify the collection.</param>
        /// <returns>The scope guarding this access.</returns>
        internal Scope Enter(CoyoteRuntime runtime, long generation, bool isAccountable, bool isWriteAccess)
        {
            if (!isAccountable)
            {
                return default;
            }

            // The unsynchronized accessor deliberately: this is only an identity lookup, and unlike
            // TryGetExecutingOperation it performs no uncontrolled-thread notification. The notifying
            // accessor is still used below, where that side effect is part of the existing behaviour.
            object owner = (object)CoyoteRuntime.GetExecutingOperationUnsynchronized() ??
                SystemThread.CurrentThread;
            bool isExempt;

            lock (this.SyncObject)
            {
                if (generation < this.Generation)
                {
                    // Left over from an iteration that a younger one has already taken the state over
                    // from. It owns nothing here and must disturb nothing: no frame, no count, and no
                    // scheduling point, which is also why it takes the no-op scope rather than one that
                    // would release a frame it never took.
                    return default;
                }

                if (generation > this.Generation)
                {
                    this.ActiveFrames.Clear();
                    this.ReaderCount = 0;
                    this.WriterCount = 0;
                    this.Generation = generation;
                }

                if (this.ActiveFrames.TryGetValue(owner, out int depth))
                {
                    // Re-entry by a frame that is already inside this collection; see the remarks.
                    this.ActiveFrames[owner] = depth + 1;
                    isExempt = true;
                }
                else
                {
                    isExempt = false;

                    // Asserted BEFORE the counts move, so a failure reports the state that produced it.
                    if (isWriteAccess)
                    {
                        runtime.Assert(this.WriterCount is 0,
                            $"Found write/write data race on '{this.CollectionType}'.");
                        runtime.Assert(this.ReaderCount is 0,
                            $"Found read/write data race on '{this.CollectionType}'.");
                        this.WriterCount++;
                    }
                    else
                    {
                        runtime.Assert(this.WriterCount is 0,
                            $"Found read/write data race on '{this.CollectionType}'.");
                        this.ReaderCount++;
                    }

                    this.ActiveFrames.Add(owner, 1);
                }
            }

            var scope = new Scope(this, owner, generation, isWriteAccess, isExempt);
            if (!isExempt)
            {
                try
                {
                    ExploreInterleaving(runtime);
                }
                catch
                {
                    // Scheduling can tear the execution down and interrupt this thread. The scope has
                    // not been handed to the caller, so nothing else will ever dispose it — release the
                    // frame here or the count leaks for the rest of the iteration.
                    scope.Dispose();
                    throw;
                }
            }

            return scope;
        }

        /// <summary>
        /// Releases a frame previously taken by <see cref="Enter(bool)"/>.
        /// </summary>
        /// <param name="owner">The frame owner.</param>
        /// <param name="generation">The generation that owned the counts when the frame was taken.</param>
        /// <param name="isWriteAccess">True if the frame was a write access.</param>
        /// <param name="isExempt">True if the frame was re-entrant and took no count.</param>
        private void Exit(object owner, long generation, bool isWriteAccess, bool isExempt)
        {
            lock (this.SyncObject)
            {
                if (this.Generation != generation)
                {
                    // A younger iteration already took the counts over; this frame belongs to an older
                    // one. Releasing here would decrement a count that now belongs to somebody else and
                    // hide the very race this type exists to report.
                    return;
                }

                if (this.ActiveFrames.TryGetValue(owner, out int depth))
                {
                    if (depth > 1)
                    {
                        this.ActiveFrames[owner] = depth - 1;
                    }
                    else
                    {
                        this.ActiveFrames.Remove(owner);
                    }
                }

                if (!isExempt)
                {
                    if (isWriteAccess)
                    {
                        if (this.WriterCount > 0)
                        {
                            this.WriterCount--;
                        }
                    }
                    else if (this.ReaderCount > 0)
                    {
                        this.ReaderCount--;
                    }
                }
            }
        }

        /// <summary>
        /// Checks whether frames belonging to the specified runtime can be accounted for at all.
        /// </summary>
        /// <remarks>
        /// Two callers cannot be: one whose iteration has already been torn down, and one that reached
        /// a modelled collection with no controlled execution behind it, which resolves the process-wide
        /// default runtime. The default runtime is the reason liveness alone is not enough to ask —
        /// it is created once, stays <see cref="ExecutionStatus.Running"/> forever, and is what any
        /// genuinely uncontrolled thread reports, so every such thread would otherwise look like the
        /// arrival of a new iteration.
        /// </remarks>
        /// <param name="runtime">The runtime the calling frame belongs to.</param>
        /// <returns>True if that runtime may own frames here.</returns>
        private static bool IsAccountable(CoyoteRuntime runtime) =>
            !runtime.HasExecutionEnded && runtime.SchedulingPolicy != SchedulingPolicy.None;

        /// <summary>
        /// Gives the scheduler the opportunity to switch to another operation.
        /// </summary>
        /// <param name="runtime">The current runtime.</param>
        private static void ExploreInterleaving(CoyoteRuntime runtime)
        {
            if (runtime.SchedulingPolicy != SchedulingPolicy.None &&
                runtime.TryGetExecutingOperation(out ControlledOperation current))
            {
                if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving)
                {
                    runtime.ScheduleNextOperation(current, SchedulingPointType.Default);
                }
                else if (runtime.SchedulingPolicy is SchedulingPolicy.Fuzzing)
                {
                    runtime.DelayOperation(current);
                }
            }
        }

        /// <summary>
        /// Guards one collection access for as long as it is in flight.
        /// </summary>
        internal readonly struct Scope : IDisposable
        {
            /// <summary>
            /// The checker that owns this frame, or null when the accessed collection is not modelled.
            /// </summary>
            private readonly DataRaceChecker Checker;

            /// <summary>
            /// The frame owner.
            /// </summary>
            private readonly object Owner;

            /// <summary>
            /// The generation that owned the counts when this frame was taken.
            /// </summary>
            private readonly long Generation;

            /// <summary>
            /// True if this frame is a write access.
            /// </summary>
            private readonly bool IsWriteAccess;

            /// <summary>
            /// True if this frame is re-entrant and took no count.
            /// </summary>
            private readonly bool IsExempt;

            /// <summary>
            /// Initializes a new instance of the <see cref="Scope"/> struct.
            /// </summary>
            internal Scope(DataRaceChecker checker, object owner, long generation, bool isWriteAccess,
                bool isExempt)
            {
                this.Checker = checker;
                this.Owner = owner;
                this.Generation = generation;
                this.IsWriteAccess = isWriteAccess;
                this.IsExempt = isExempt;
            }

            /// <summary>
            /// Releases the frame. Never asserts, never schedules and never throws: it runs while an
            /// exception may already be unwinding the shim, and must not replace it.
            /// </summary>
            public void Dispose()
            {
                if (this.Checker is null)
                {
                    return;
                }

                try
                {
                    this.Checker.Exit(this.Owner, this.Generation, this.IsWriteAccess, this.IsExempt);
                }
                catch (Exception)
                {
                    // Teardown interrupts threads, including one waiting on the checker's lock.
                }
            }
        }
    }
}

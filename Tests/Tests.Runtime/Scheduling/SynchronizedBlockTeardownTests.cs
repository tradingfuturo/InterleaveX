// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;
using CoyoteMonitor = Microsoft.Coyote.Rewriting.Types.Threading.Monitor;
using SynchronizedBlock = Microsoft.Coyote.Rewriting.Types.Threading.Monitor.SynchronizedBlock;
#if NET9_0_OR_GREATER
using CoyoteLock = Microsoft.Coyote.Rewriting.Types.Threading.Lock;
using SystemLock = System.Threading.Lock;
#endif

namespace Microsoft.Coyote.Runtime.Tests
{
    /// <summary>
    /// Tests how synchronized blocks behave around the end of a test iteration.
    /// </summary>
    /// <remarks>
    /// When an iteration ends, operations that were executing user code keep running until the interrupt
    /// that terminates them is delivered, while the testing engine concurrently clears the block cache so
    /// that the next iteration starts from an empty one. These tests plant the states that race produces
    /// directly, rather than trying to reproduce the timing, so they are deterministic.
    /// </remarks>
    public class SynchronizedBlockTeardownTests : BaseRuntimeTest
    {
        public SynchronizedBlockTeardownTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 30000)]
        public void TestAcquiringHealsBlockFromEndedIteration()
        {
            var syncObject = new object();
            CoyoteRuntime endedRuntime = this.CaptureEndedRuntime();
            PlantBlock(endedRuntime, syncObject);

            // The block planted above belongs to an iteration that has ended, so acquiring the lock must
            // discard it rather than report that this iteration is touching a previous iteration's state.
            this.RunSystematicTest(() =>
            {
                CoyoteMonitor.Enter(syncObject);
                CoyoteMonitor.Exit(syncObject);
            },
            this.GetConfiguration().WithTestingIterations(1));

            Assert.False(ReferenceEquals(FindPlantedBlock(syncObject), PlantedBlock),
                "The block from the ended iteration is still cached.");
        }

        [Fact(Timeout = 30000)]
        public void TestQueryingHealsBlockFromEndedIteration()
        {
            var syncObject = new object();
            CoyoteRuntime endedRuntime = this.CaptureEndedRuntime();
            PlantBlock(endedRuntime, syncObject);
            bool isLockReportedAsEntered = true;

            // Querying reaches the cache through a different path than acquiring, and must also discard
            // the previous iteration's block. Discarding it leaves nothing cached for this object, which
            // is the state of a lock this iteration does not hold, so the query reports that rather than
            // reporting that this iteration is touching a previous iteration's state.
            this.RunSystematicTest(() =>
            {
                isLockReportedAsEntered = CoyoteMonitor.IsEntered(syncObject);
            },
            this.GetConfiguration().WithTestingIterations(1));

            Assert.False(isLockReportedAsEntered,
                "Querying a lock reported the previous iteration's block for it as held.");
            Assert.Null(FindPlantedBlock(syncObject));
        }

        [Fact(Timeout = 30000)]
        public void TestQueryingOnEndedRuntimeNeverInspectsTheBlock()
        {
            var syncObject = new object();
            CoyoteRuntime endedRuntime = this.CaptureEndedRuntime();
            bool isLockReportedAsEntered = true;

            // Rejecting the block has to happen before it is inspected rather than after, and the answer
            // alone cannot tell the two apart: inspecting a block asks whoever is asking whether they own
            // it, and an ended runtime never owns anything, so an implementation that inspects first and
            // masks the result with the status afterwards answers false as well. What separates them is
            // that inspecting a block asks the runtime CURRENT ON THE THREAD, not the runtime the query is
            // being made on behalf of. Planting a block created by a runtime other than the current one
            // therefore turns any inspection into the cross-iteration assertion that this ordering exists
            // to keep out of an operation that outlived its iteration, which fails the test whatever the
            // query goes on to answer. The owner is set because a block without one answers without asking.
            this.RunSystematicTest(() =>
            {
                CoyoteRuntime runtime = CoyoteRuntime.Current;
                SynchronizedBlock stale = CreateBlock(endedRuntime, syncObject);
                SetOwner(stale, runtime.GetExecutingOperation());
                GetCache()[syncObject] = new Lazy<SynchronizedBlock>(() => stale);

                isLockReportedAsEntered = CoyoteMonitor.IsBlockEntered(endedRuntime, syncObject);
            },
            this.GetConfiguration().WithTestingIterations(1));

            Assert.False(isLockReportedAsEntered,
                "Querying a lock on behalf of an iteration that ended reported it as held.");
        }

        [Fact(Timeout = 30000)]
        public void TestQueryingChecksTeardownAfterLookingUp()
        {
            var syncObject = new object();
            CoyoteRuntime endedRuntime = this.CaptureEndedRuntime();
            bool isLookedUp = false;

            GetCache()[syncObject] = new Lazy<SynchronizedBlock>(() =>
            {
                isLookedUp = true;
                return CreateBlock(endedRuntime, syncObject);
            });

            // The status a runtime ends with is written before the cache it leaves behind is cleared, and
            // both are read through volatile reads, so a query that looks up first and checks the status
            // afterwards observes the ended status whenever its lookup could have seen what teardown did.
            // Checking first inverts that: the status can turn ended in the window between the check and
            // the lookup, and nothing downstream of the lookup is left to notice. The order is pinned here
            // by observing that the lookup runs at all for a runtime that has already ended, which it only
            // does if the check has not already returned.
            bool isLockReportedAsEntered = CoyoteMonitor.IsBlockEntered(endedRuntime, syncObject);

            Assert.False(isLockReportedAsEntered,
                "Querying a lock on behalf of an iteration that ended reported it as held.");
            Assert.True(isLookedUp,
                "Querying a lock checked for teardown before looking the block up, which leaves the window " +
                "between the check and the lookup unguarded.");
        }

#if NET9_0_OR_GREATER
#pragma warning disable CS9216 // Lock objects are intentionally passed as object to SynchronizedBlock
        [Fact(Timeout = 30000)]
        public void TestQueryingLockOnEndedRuntimeNeverInspectsTheBlock()
        {
            var lockObj = new SystemLock();
            CoyoteRuntime endedRuntime = this.CaptureEndedRuntime();
            bool isLockReportedAsHeld = true;

            // The same setup on the lock type that keys its blocks by a System.Threading.Lock, and for the
            // same reason: the block is created by a runtime other than the one current on this thread, so
            // inspecting it raises the cross-iteration assertion rather than quietly answering.
            this.RunSystematicTest(() =>
            {
                CoyoteRuntime runtime = CoyoteRuntime.Current;
                SynchronizedBlock stale = CreateBlock(endedRuntime, lockObj);
                SetOwner(stale, runtime.GetExecutingOperation());
                GetCache()[lockObj] = new Lazy<SynchronizedBlock>(() => stale);

                isLockReportedAsHeld = CoyoteLock.IsBlockHeldByCurrentThread(endedRuntime, lockObj);
            },
            this.GetConfiguration().WithTestingIterations(1));

            Assert.False(isLockReportedAsHeld,
                "Querying a lock on behalf of an iteration that ended reported it as held.");
        }

        [Fact(Timeout = 30000)]
        public void TestQueryingLockChecksTeardownAfterLookingUp()
        {
            var lockObj = new SystemLock();
            CoyoteRuntime endedRuntime = this.CaptureEndedRuntime();
            bool isLookedUp = false;

            GetCache()[lockObj] = new Lazy<SynchronizedBlock>(() =>
            {
                isLookedUp = true;
                return CreateBlock(endedRuntime, lockObj);
            });

            // Pins the order for the same reason as the object-based query: the lookup only runs if the
            // teardown check has not already returned, so a check made first is visible as a lookup that
            // never happened.
            bool isLockReportedAsHeld = CoyoteLock.IsBlockHeldByCurrentThread(endedRuntime, lockObj);

            Assert.False(isLockReportedAsHeld,
                "Querying a lock on behalf of an iteration that ended reported it as held.");
            Assert.True(isLookedUp,
                "Querying a lock checked for teardown before looking the block up, which leaves the window " +
                "between the check and the lookup unguarded.");
        }
#pragma warning restore CS9216 // Lock objects are intentionally passed as object to SynchronizedBlock
#endif

        [Fact(Timeout = 30000)]
        public void TestQueryingLooksAgainAfterDiscardingAnEndedIterationsBlock()
        {
            var syncObject = new object();
            CoyoteRuntime endedRuntime = this.CaptureEndedRuntime();
            bool isQueryRejected = false;
            bool isLockReportedAsEntered = false;
            bool isReplacementRetained = false;

            this.RunSystematicTest(() =>
            {
                CoyoteRuntime runtime = CoyoteRuntime.Current;
                SynchronizedBlock stale = CreateBlock(endedRuntime, syncObject);
                SynchronizedBlock replacement = CreateBlock(runtime, syncObject);
                SetOwner(replacement, runtime.GetExecutingOperation());

                // Stands in for this iteration installing its own entry for the object in the window
                // between the previous iteration's entry being read and being discarded. Forcing the
                // entry is the one point inside that window that a test can reach, and it is enough:
                // the discard is matched on the value read here, so it does nothing, and a lookup that
                // reports a miss on that basis is reporting a miss that is not there.
                GetCache()[syncObject] = new Lazy<SynchronizedBlock>(() =>
                {
                    GetCache()[syncObject] = new Lazy<SynchronizedBlock>(() => replacement);
                    return stale;
                });

                try
                {
                    isLockReportedAsEntered = CoyoteMonitor.IsEntered(syncObject);
                }
                catch (System.Threading.SynchronizationLockException)
                {
                    isQueryRejected = true;
                }

                // Observed here rather than after the test, because the engine clears the whole cache
                // between iterations, which would remove the entry either way.
                isReplacementRetained = ReferenceEquals(GetCacheEntry(syncObject)?.Value, replacement);
            },
            this.GetConfiguration().WithTestingIterations(1));

            Assert.False(isQueryRejected,
                "Querying a lock reported that it was never entered, even though this iteration's own " +
                "block for it was cached.");
            Assert.True(isLockReportedAsEntered,
                "Querying a lock did not find this iteration's block for it.");
            Assert.True(isReplacementRetained,
                "Querying a lock removed this iteration's cache entry for the object.");
        }

        [Fact(Timeout = 30000)]
        public void TestAcquiringOnEndedRuntimeLeavesNoBlockBehind()
        {
            var syncObject = new object();
            CoyoteRuntime endedRuntime = this.CaptureEndedRuntime();

            // An operation that is still unwinding after its iteration ended must not populate the cache
            // that the next iteration is about to use.
            SynchronizedBlock block = SynchronizedBlock.Lock(endedRuntime, syncObject);

            Assert.Null(block);
            Assert.Null(FindPlantedBlock(syncObject));
        }

        [Fact(Timeout = 30000)]
        public void TestReleasingDoesNotDisturbAnotherIterationsEntry()
        {
            var syncObject = new object();
            bool isReplacementCreated = false;
            bool isReplacementRetained = false;

            // Releasing has to run inside the iteration that holds the lock, because that is the only
            // context in which the lock is owned.
            this.RunSystematicTest(() =>
            {
                CoyoteMonitor.Enter(syncObject);
                SynchronizedBlock acquired = SynchronizedBlock.Find(syncObject);
                Specifications.Specification.Assert(acquired != null, "The lock was not tracked.");

                // Stand in for a later iteration having installed its own entry for this object before
                // this operation finished releasing it. The entry is left uncreated, so that creating it
                // can be detected.
                var replacement = new Lazy<SynchronizedBlock>(() =>
                {
                    isReplacementCreated = true;
                    return null;
                });

                GetCache()[syncObject] = replacement;
                acquired.Exit();

                // Observed here rather than after the test, because the engine clears the whole cache
                // between iterations, which would remove the entry either way.
                isReplacementRetained = ReferenceEquals(GetCacheEntry(syncObject), replacement);
            },
            this.GetConfiguration().WithTestingIterations(1));

            Assert.False(isReplacementCreated,
                "Releasing a lock created the block belonging to another iteration's cache entry.");
            Assert.True(isReplacementRetained,
                "Releasing a lock removed another iteration's cache entry for the same object.");
        }

        /// <summary>
        /// The block most recently planted by <see cref="PlantBlock"/>.
        /// </summary>
        private static SynchronizedBlock PlantedBlock;

        /// <summary>
        /// Runs a test and returns its runtime, which has detached and been deregistered by the time it
        /// is returned, so it is authentically what an operation left over from a finished iteration sees.
        /// </summary>
        private CoyoteRuntime CaptureEndedRuntime()
        {
            CoyoteRuntime captured = null;
            this.RunSystematicTest(() => captured = CoyoteRuntime.Current,
                this.GetConfiguration().WithTestingIterations(1));

            Assert.NotNull(captured);
            Assert.True(captured.HasExecutionEnded, "The captured runtime is still running.");
            return captured;
        }

        /// <summary>
        /// Caches a block owned by the specified runtime for the specified object, as an operation that
        /// outlived its iteration would.
        /// </summary>
        private static void PlantBlock(CoyoteRuntime runtime, object syncObject)
        {
            PlantedBlock = CreateBlock(runtime, syncObject);
            GetCache()[syncObject] = new Lazy<SynchronizedBlock>(() => PlantedBlock);
        }

        /// <summary>
        /// Creates a block owned by the specified runtime for the specified object, without caching it.
        /// </summary>
        private static SynchronizedBlock CreateBlock(CoyoteRuntime runtime, object syncObject)
        {
            var constructor = typeof(SynchronizedBlock).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(CoyoteRuntime), typeof(object) }, null);
            Assert.NotNull(constructor);
            return (SynchronizedBlock)constructor.Invoke(new object[] { runtime, syncObject });
        }

        /// <summary>
        /// Makes the specified operation the owner of the specified block, as entering it would.
        /// </summary>
        private static void SetOwner(SynchronizedBlock block, ControlledOperation owner)
        {
            var field = typeof(SynchronizedBlock).GetField("Owner",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field.SetValue(block, owner);
        }

        /// <summary>
        /// Returns the cached block for the specified object, or null if there is none.
        /// </summary>
        private static SynchronizedBlock FindPlantedBlock(object syncObject) =>
            GetCacheEntry(syncObject)?.Value;

        /// <summary>
        /// Returns the cache entry for the specified object, or null if there is none.
        /// </summary>
        private static Lazy<SynchronizedBlock> GetCacheEntry(object syncObject) =>
            GetCache().TryGetValue(syncObject, out Lazy<SynchronizedBlock> entry) ? entry : null;

        /// <summary>
        /// Returns the cache that associates objects with their synchronized blocks.
        /// </summary>
        private static ConcurrentDictionary<object, Lazy<SynchronizedBlock>> GetCache()
        {
            var field = typeof(SynchronizedBlock).GetField("Cache",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return (ConcurrentDictionary<object, Lazy<SynchronizedBlock>>)field.GetValue(null);
        }
    }
}

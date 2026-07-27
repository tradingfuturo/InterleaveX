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

            // Querying reaches the cache through a different path than acquiring, and must also discard
            // the previous iteration's block. Discarding it leaves nothing cached for this object, which
            // is the state of a lock that was never entered, so the query reports that rather than
            // reporting that this iteration is touching a previous iteration's state.
            this.RunSystematicTest(() =>
            {
                try
                {
                    CoyoteMonitor.IsEntered(syncObject);
                    Specifications.Specification.Assert(false,
                        "Querying a lock that was never entered in this iteration should not succeed.");
                }
                catch (System.Threading.SynchronizationLockException)
                {
                    // Expected: the planted block was discarded, so no lock is being tracked.
                }
            },
            this.GetConfiguration().WithTestingIterations(1));

            Assert.Null(FindPlantedBlock(syncObject));
        }

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

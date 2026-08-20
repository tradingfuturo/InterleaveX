// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Rewriting;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// Tests for the controlled <see cref="BlockingCollection{T}"/> mock. The test assembly is rewritten,
    /// so every construction and every blocking member below is redirected to the controlled
    /// implementation.
    /// <para>Without the mock these are not merely imprecise, they are unrunnable: a thread parked inside
    /// the real type has left Coyote's world entirely, so no scheduling step occurs and the runtime's
    /// PERIODIC HANG MONITOR reports "Potential deadlock or hang detected" on every schedule. That monitor
    /// inspects step counts rather than operation statuses, which is why modelling the park — rather than
    /// teaching the status-based detector a new state — is what actually fixes it.</para>
    /// </summary>
    public class BlockingCollectionTests : BaseBugFindingTest
    {
        public BlockingCollectionTests(ITestOutputHelper output)
            : base(output)
        {
        }

        // Everything else in this file would also pass against the REAL type on a lucky schedule, so pin
        // the redirection itself first: if the rewriter stops mapping the constructor, this is the test
        // that says so instead of the suite quietly going back to testing the BCL.
        [Fact(Timeout = 5000)]
        public void TestConstructorsAreRedirected()
        {
            this.Test(() =>
            {
                var collections = new object[]
                {
                    new BlockingCollection<int>(),
                    new BlockingCollection<int>(4),
                    new BlockingCollection<int>(new ConcurrentQueue<int>()),
                    new BlockingCollection<int>(new ConcurrentQueue<int>(), 4),
                };

                foreach (var collection in collections)
                {
                    string name = collection.GetType().FullName;
                    Specification.Assert(
                        name.Contains("Wrapper", StringComparison.Ordinal),
                        "BlockingCollection constructor was not redirected to the controlled mock: '{0}'.",
                        name);
                }
            });
        }

        // The BCL reports an unbounded collection as -1, NOT as a very large bound. The distinction is not
        // cosmetic: code that branches on BoundedCapacity to decide whether back-pressure exists reads
        // int.MaxValue as "bounded, enormous" and takes the wrong branch. The bounded case is asserted
        // alongside it so that a fix cannot satisfy this by reporting -1 for everything.
        [Fact(Timeout = 5000)]
        public void TestUnboundedCollectionReportsNoBound()
        {
            this.Test(() =>
            {
                var parameterless = new BlockingCollection<int>();
                var overBackingStore = new BlockingCollection<int>(new ConcurrentQueue<int>());
                var bounded = new BlockingCollection<int>(4);

                Specification.Assert(parameterless.BoundedCapacity is -1,
                    "An unbounded collection reported a bound of {0} instead of -1.", parameterless.BoundedCapacity);
                Specification.Assert(overBackingStore.BoundedCapacity is -1,
                    "An unbounded collection over a backing store reported a bound of {0} instead of -1.",
                    overBackingStore.BoundedCapacity);
                Specification.Assert(bounded.BoundedCapacity is 4,
                    "A bounded collection reported a bound of {0} instead of 4.", bounded.BoundedCapacity);
            });
        }

        [Fact(Timeout = 5000)]
        public void TestBoundedProducerConsumerCompletes()
        {
            this.Test(() =>
            {
                var collection = new BlockingCollection<int>(2);
                var consumed = new List<int>();

                Task producer = Task.Run(() =>
                {
                    for (int i = 0; i < 5; i++)
                    {
                        collection.Add(i);
                    }

                    collection.CompleteAdding();
                });

                Task consumer = Task.Run(() =>
                {
                    while (collection.TryTake(out int item, Timeout.Infinite))
                    {
                        consumed.Add(item);
                    }
                });

                Task.WaitAll(producer, consumer);

                Specification.Assert(consumed.Count is 5, "Consumed {0} items instead of 5.", consumed.Count);
            });
        }

        // THE COLD-WAIT CASE. A taker parks on an empty collection and only a later add can release it —
        // the interleaving that is invisible, and therefore a hang, without the mock.
        [Fact(Timeout = 5000)]
        public void TestBlockedTakeIsWokenByAdd()
        {
            this.Test(() =>
            {
                var collection = new BlockingCollection<int>(4);
                int taken = -1;

                Task consumer = Task.Run(() => taken = collection.Take());
                Task producer = Task.Run(() => collection.Add(42));

                Task.WaitAll(consumer, producer);

                Specification.Assert(taken is 42, "Took {0} instead of 42.", taken);
            });
        }

        // The other release path: no item ever arrives, and completion is what ends the wait.
        [Fact(Timeout = 5000)]
        public void TestBlockedTakeIsWokenByCompleteAdding()
        {
            this.Test(() =>
            {
                var collection = new BlockingCollection<int>(4);
                bool tookItem = true;

                Task consumer = Task.Run(() => tookItem = collection.TryTake(out int _, Timeout.Infinite));
                Task completer = Task.Run(() => collection.CompleteAdding());

                Task.WaitAll(consumer, completer);

                Specification.Assert(!tookItem, "A completed and empty collection yielded an item.");
            });
        }

        // The mirror on the add side: a full collection back-pressures the producer until a take frees a slot.
        [Fact(Timeout = 5000)]
        public void TestBlockedAddIsWokenByTake()
        {
            this.Test(() =>
            {
                var collection = new BlockingCollection<int>(1);
                collection.Add(1);

                bool added = false;
                Task producer = Task.Run(() =>
                {
                    collection.Add(2);
                    added = true;
                });

                Task consumer = Task.Run(() => collection.Take());

                Task.WaitAll(producer, consumer);

                Specification.Assert(added, "The blocked add was never released by the take.");
            });
        }

        // Completion must not discard what is already queued: the items drain first, and only then does the
        // collection report itself complete.
        [Fact(Timeout = 5000)]
        public void TestCompleteAddingDrainsQueuedItemsFirst()
        {
            this.Test(() =>
            {
                var collection = new BlockingCollection<int>(4);
                collection.Add(1);
                collection.Add(2);
                collection.CompleteAdding();

                int count = 0;
                while (collection.TryTake(out int _, Timeout.Infinite))
                {
                    count++;
                }

                Specification.Assert(count is 2, "Drained {0} items instead of 2.", count);
                Specification.Assert(collection.IsCompleted, "The drained collection did not report completion.");
            });
        }

        [Fact(Timeout = 5000)]
        public void TestZeroTimeoutNeverBlocks()
        {
            this.Test(() =>
            {
                var collection = new BlockingCollection<int>(1);

                Specification.Assert(!collection.TryTake(out int _, 0), "An empty collection yielded an item.");
                Specification.Assert(collection.TryAdd(1, 0), "A collection with space refused an item.");
                Specification.Assert(!collection.TryAdd(2, 0), "A full collection accepted an item.");
                Specification.Assert(collection.TryTake(out int taken, 0) && taken is 1, "The queued item was not returned.");
            });
        }

        // A finite timeout has no clock to honour, so it is modelled as an abstract budget that the
        // scheduler may exhaust. What matters is that it TERMINATES rather than waiting forever: nothing
        // will ever release this taker, so only the timeout can.
        [Fact(Timeout = 5000)]
        public void TestFiniteTimeoutExpiresWhenNothingCanRelease()
        {
            this.Test(() =>
            {
                var collection = new BlockingCollection<int>(4);

                bool took = collection.TryTake(out int _, 100);

                Specification.Assert(!took, "An empty collection yielded an item.");
            });
        }

        // Exact time competes with the producer. Across schedules, both the resource and the deadline must
        // be able to win; requiring only the resource outcome would suppress timeout-before-release.
        [Fact(Timeout = 5000)]
        public void TestFiniteTimeoutSucceedsWhenAnAddArrives()
        {
            bool observedResource = false;
            bool observedDeadline = false;
            this.Test(() =>
            {
                var collection = new BlockingCollection<int>(4);
                bool took = false;

                Task consumer = Task.Run(() => took = collection.TryTake(out int _, 1000));
                Task producer = Task.Run(() => collection.Add(1));

                Task.WaitAll(consumer, producer);
                observedResource |= took;
                observedDeadline |= !took;
            }, this.GetConfiguration().WithTestingIterations(200));

            Assert.True(observedResource, "No schedule let the add satisfy the finite-timeout take.");
            Assert.True(observedDeadline, "No schedule let the exact deadline beat the concurrent add.");
        }

        // WAKE-THEN-LOSE: a timed waiter repeatedly woken and then beaten to the item must still TERMINATE,
        // one way or the other, rather than looping forever.
        //
        // This is a liveness check on that path, NOT the witness for budget preservation — measured by
        // mutation, a budget redraw leaves this test passing (termination survives either policy) and
        // instead breaks TestFiniteTimeoutSucceedsWhenAnAddArrives. Kept because it exercises the
        // wake/re-scan/re-pause loop, which nothing else here drives repeatedly.
        [Fact(Timeout = 5000)]
        public void TestFiniteTimeoutIsNotRestartedByEachWake()
        {
            this.Test(() =>
            {
                var collection = new BlockingCollection<int>(4);

                // A greedy consumer races the timed one for every item the producer publishes.
                Task greedy = Task.Run(() =>
                {
                    for (int i = 0; i < 4; i++)
                    {
                        collection.TryTake(out int _, 0);
                    }
                });

                Task timed = Task.Run(() => collection.TryTake(out int _, 1000));

                Task producer = Task.Run(() =>
                {
                    for (int i = 0; i < 4; i++)
                    {
                        collection.Add(i);
                    }
                });

                // Reaching here at all is the assertion: the timed take terminated, one way or the other.
                Task.WaitAll(greedy, timed, producer);
            });
        }

        [Fact(Timeout = 5000)]
        public void TestInvalidTimeoutThrows()
        {
            this.Test(() =>
            {
                var collection = new BlockingCollection<int>(4);
                bool threw = false;
                try
                {
                    collection.TryTake(out int _, -2);
                }
                catch (ArgumentOutOfRangeException)
                {
                    threw = true;
                }

                Specification.Assert(threw, "A timeout below -1 did not throw.");
            });
        }

        [Fact(Timeout = 5000)]
        public void TestTakeFromAnyPrefersTheEarlierIndex()
        {
            this.Test(() =>
            {
                var first = new BlockingCollection<int>(4);
                var second = new BlockingCollection<int>(4);
                first.Add(10);
                second.Add(20);

                int index = BlockingCollection<int>.TryTakeFromAny(
                    new[] { first, second }, out int item, Timeout.Infinite);

                Specification.Assert(index is 0, "Took from index {0} instead of 0.", index);
                Specification.Assert(item is 10, "Took {0} instead of 10.", item);
            });
        }

        // The parameterless overload is a ZERO-TIMEOUT PROBE: it returns -1 whenever nothing is
        // immediately available, even though both collections are still open and could yet receive items.
        // Only the infinite overload treats -1 as "all completed and drained". Conflating the two turns a
        // poll loop into a busy spin.
        [Fact(Timeout = 5000)]
        public void TestParameterlessTakeFromAnyIsAZeroTimeoutProbe()
        {
            this.Test(() =>
            {
                var first = new BlockingCollection<int>(4);
                var second = new BlockingCollection<int>(4);

                int index = BlockingCollection<int>.TryTakeFromAny(new[] { first, second }, out int _);

                Specification.Assert(index is -1, "An empty open pair returned index {0} instead of -1.", index);
                Specification.Assert(!first.IsCompleted && !second.IsCompleted, "The collections should still be open.");
            });
        }

        [Fact(Timeout = 5000)]
        public void TestTakeFromAnyReturnsMinusOneWhenAllCompletedAndDrained()
        {
            this.Test(() =>
            {
                var first = new BlockingCollection<int>(4);
                var second = new BlockingCollection<int>(4);
                first.CompleteAdding();
                second.CompleteAdding();

                int index = BlockingCollection<int>.TryTakeFromAny(
                    new[] { first, second }, out int _, Timeout.Infinite);

                Specification.Assert(index is -1, "Returned index {0} instead of -1.", index);
            });
        }

        // A completed input does NOT poison a take-any call — unlike the add side. The open collection is
        // still waited on.
        [Fact(Timeout = 5000)]
        public void TestTakeFromAnyIgnoresACompletedCollection()
        {
            this.Test(() =>
            {
                var completed = new BlockingCollection<int>(4);
                var open = new BlockingCollection<int>(4);
                completed.CompleteAdding();

                int index = -2;
                Task consumer = Task.Run(() =>
                    index = BlockingCollection<int>.TryTakeFromAny(
                        new[] { completed, open }, out int _, Timeout.Infinite));
                Task producer = Task.Run(() => open.Add(7));

                Task.WaitAll(consumer, producer);

                Specification.Assert(index is 1, "Took from index {0} instead of 1.", index);
            });
        }

        // THE ADD-SIDE ASYMMETRY. An unbounded collection wins through the fast path even when an earlier
        // bounded collection has space, so add-any is not simply take-any with the verbs swapped.
        [Fact(Timeout = 5000)]
        public void TestAddToAnyPrefersAnUnboundedCollection()
        {
            this.Test(() =>
            {
                var boundedWithSpace = new BlockingCollection<int>(4);
                var unbounded = new BlockingCollection<int>();

                int index = BlockingCollection<int>.TryAddToAny(new[] { boundedWithSpace, unbounded }, 1);

                Specification.Assert(index is 1, "Added to index {0} instead of the unbounded index 1.", index);
            });
        }

        // ...and a single completed input invalidates the WHOLE add-any call, however healthy its siblings.
        [Fact(Timeout = 5000)]
        public void TestAddToAnyThrowsWhenAnyCollectionIsCompleted()
        {
            this.Test(() =>
            {
                var completed = new BlockingCollection<int>(4);
                var open = new BlockingCollection<int>(4);
                completed.CompleteAdding();

                bool threw = false;
                try
                {
                    BlockingCollection<int>.TryAddToAny(new[] { completed, open }, 1);
                }
                catch (ArgumentException)
                {
                    threw = true;
                }

                Specification.Assert(threw, "A completed collection did not invalidate the add-any call.");
            });
        }

        [Fact(Timeout = 5000)]
        public void TestBlockedTakeFromAnyIsWokenByAnAddToEither()
        {
            this.Test(() =>
            {
                var first = new BlockingCollection<int>(4);
                var second = new BlockingCollection<int>(4);

                int index = -2;
                Task consumer = Task.Run(() =>
                    index = BlockingCollection<int>.TryTakeFromAny(
                        new[] { first, second }, out int _, Timeout.Infinite));
                Task producer = Task.Run(() => second.Add(5));

                Task.WaitAll(consumer, producer);

                Specification.Assert(index is 1, "Took from index {0} instead of 1.", index);
            });
        }

        [Fact(Timeout = 5000)]
        public void TestCancellationWakesABlockedTake()
        {
            this.Test(() =>
            {
                var collection = new BlockingCollection<int>(4);
                using var source = new CancellationTokenSource();
                bool cancelled = false;

                Task consumer = Task.Run(() =>
                {
                    try
                    {
                        collection.Take(source.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                    }
                });

                Task canceller = Task.Run(() => source.Cancel());

                Task.WaitAll(consumer, canceller);

                Specification.Assert(cancelled, "The blocked take was not released by cancellation.");
            });
        }

        [Fact(Timeout = 5000)]
        public void TestCancellationWakesABlockedAdd()
        {
            this.Test(() =>
            {
                var collection = new BlockingCollection<int>(1);
                collection.Add(1);
                using var source = new CancellationTokenSource();
                bool cancelled = false;

                Task producer = Task.Run(() =>
                {
                    try
                    {
                        collection.Add(2, source.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                    }
                });

                Task canceller = Task.Run(() => source.Cancel());

                Task.WaitAll(producer, canceller);

                Specification.Assert(cancelled, "The blocked add was not released by cancellation.");
            });
        }

        // GetConsumingEnumerable is the only member that is more than a thin interceptor: its MoveNext is a
        // blocking take, so a consumer parked between items must be a controlled pause.
        [Fact(Timeout = 5000)]
        public void TestConsumingEnumerableTerminatesOnCompletion()
        {
            this.Test(() =>
            {
                var collection = new BlockingCollection<int>(4);
                var consumed = new List<int>();

                Task consumer = Task.Run(() =>
                {
                    foreach (int item in collection.GetConsumingEnumerable())
                    {
                        consumed.Add(item);
                    }
                });

                Task producer = Task.Run(() =>
                {
                    collection.Add(1);
                    collection.Add(2);
                    collection.CompleteAdding();
                });

                Task.WaitAll(consumer, producer);

                Specification.Assert(consumed.Count is 2, "Consumed {0} items instead of 2.", consumed.Count);
            });
        }

        [Fact(Timeout = 5000)]
        public void TestDisposeReleasesWaiters()
        {
            this.Test(() =>
            {
                var collection = new BlockingCollection<int>(4);
                collection.Add(1);
                collection.CompleteAdding();

                while (collection.TryTake(out int _, Timeout.Infinite))
                {
                }

                collection.Dispose();

                bool threw = false;
                try
                {
                    collection.TryAdd(1, 0);
                }
                catch (ObjectDisposedException)
                {
                    threw = true;
                }

                Specification.Assert(threw, "A disposed collection accepted an add.");
            });
        }

        [Fact(Timeout = 5000)]
        public void TestDisposeThroughTheInterfaceIsAlsoControlled()
        {
            this.Test(() =>
            {
                var collection = new BlockingCollection<int>(4);
                IDisposable disposable = collection;
                disposable.Dispose();

                bool threw = false;
                try
                {
                    collection.TryAdd(1, 0);
                }
                catch (ObjectDisposedException)
                {
                    threw = true;
                }

                Specification.Assert(threw, "A collection disposed through IDisposable accepted an add.");
            });
        }

        // The mock must not make every wait harmless. Two collections, two operations, each holding what
        // the other needs: this is a REAL deadlock and it must still be reported.
        [Fact(Timeout = 5000)]
        public void TestGenuineDeadlockIsStillReported()
        {
            this.TestWithError(() =>
            {
                var first = new BlockingCollection<int>(1);
                var second = new BlockingCollection<int>(1);

                Task a = Task.Run(() => first.Take());
                Task b = Task.Run(() => second.Take());

                Task.WaitAll(a, b);
            },
            errorChecker: (e) =>
            {
                // It must be the STATUS-BASED detector that fires, not the periodic hang monitor. Accepting
                // either would make this test pass against an unmodelled BlockingCollection too — the hang
                // report is exactly what the mock exists to eliminate, so treating it as success would
                // assert the opposite of the intent.
                Specification.Assert(
                    e.Contains("Deadlock detected", StringComparison.Ordinal),
                    "Expected the status-based deadlock report, got: {0}", e);
                Specification.Assert(
                    !e.Contains("periodic deadlock detection monitor", StringComparison.Ordinal),
                    "The periodic hang monitor fired, which means the wait was never modelled: {0}", e);
            },
            replay: true);
        }

        // THE LOCK DISCIPLINE. Every other modelled primitive mutates its waiter set and calls
        // ControlledOperation.TryEnable inside the runtime's synchronized section — WaitHandle.SignalNext
        // and SignalAll say so in as many words, and SemaphoreSlim.Exit holds the section across the whole
        // release. Neither the operation's Status nor its awaited-resource set is volatile or interlocked,
        // so the section is the only thing that orders them against the scheduler and against the periodic
        // deadlock monitor, which reads that state from its own thread.
        //
        // This asserts the invariant directly rather than trying to win the race that violating it opens,
        // which is what makes it deterministic. The backing store is the observation point because it is
        // the one piece of test code that runs INSIDE a controlled add or take, immediately before the
        // signal that follows it.
        //
        // It also stands in for a defect that has no reproducer of its own: the failed immediate attempt
        // and the waiter registration currently sit in different critical sections, so a completion or a
        // competing take that lands between them signals an empty waiter set and parks the operation
        // forever. That window contains no user-reachable code — no scheduling point, no callback, not
        // even a rewritten call — so only an uncontrolled thread can land in it, and a test that has to
        // race for it could not tell the fix from its absence. Holding one section across the whole
        // attempt-register-park cycle is what closes it, and this test is what pins that section down.
        [Fact(Timeout = 5000)]
        public void TestImmediateOperationsHoldTheRuntimeLock()
        {
            this.Test(() =>
            {
                var store = new ObservingStore();
                var collection = new BlockingCollection<int>(store, 4);

                collection.Add(1);
                collection.TryTake(out int _, 0);

                Specification.Assert(store.WasSynchronizedDuringAdd is true,
                    "The add mutated storage outside the runtime's synchronized section, so the signal that " +
                    "follows it is unsynchronized too.");
                Specification.Assert(store.WasSynchronizedDuringTake is true,
                    "The take mutated storage outside the runtime's synchronized section, so the signal that " +
                    "follows it is unsynchronized too.");
            });
        }

        // THE UNCONTROLLED BLOCKING CALLER, take side. A timer callback runs on a thread the scheduler does
        // not control, but with the execution context flowed — so the runtime it resolves IS this test's
        // runtime, the ownership check passes, and the wait reaches the point where it discovers it has no
        // operation to park. Partial control is allowed here, which is the configuration in which the
        // runtime explicitly says to "stay attached and let the caller finish the operation".
        //
        // The collection is open and empty, so the honest outcomes are "block until an item arrives" or
        // "report that this thread cannot be parked". Reporting that the collection has been marked
        // complete is neither: it is a false statement about program state, handed to the caller as the
        // BCL's own exception type.
        [Fact(Timeout = 10000)]
        public void TestUncontrolledTakeDoesNotFabricateCompletion()
        {
            this.Test(async () =>
            {
                var collection = new BlockingCollection<int>(4);
                var reached = new TaskCompletionSource<bool>();
                var finished = new TaskCompletionSource<bool>();
                Exception failure = null;
                int taken = -1;

                using var timer = new Timer(
                    _ =>
                    {
                        reached.TrySetResult(true);
                        try
                        {
                            taken = collection.Take();
                        }
                        catch (Exception ex)
                        {
                            failure = ex;
                        }

                        finished.TrySetResult(true);
                    }, null, 1, Timeout.Infinite);

                await reached.Task;
                collection.Add(7);
                await finished.Task;

                Specification.Assert(!collection.IsAddingCompleted,
                    "The collection was never marked complete, so nothing may claim that it was.");
                Specification.Assert(failure is null,
                    "An uncontrolled take on an open collection threw '{0}'.", failure?.Message);
                Specification.Assert(taken is 7, "The uncontrolled take returned {0} instead of 7.", taken);
            },
            configuration: this.GetConfiguration()
                .WithPartiallyControlledConcurrencyAllowed()
                .WithTestingIterations(10));
        }

        // The add side of the same defect, and the more damaging one: the take at least fails loudly, while
        // this one succeeds. Add is TryAdd with an infinite timeout and its result is discarded, on the
        // stated grounds that it "either succeeds or throws" — which stops being true the moment the wait
        // can fail without either. The item is then gone, and nothing anywhere says so.
        [Fact(Timeout = 10000)]
        public void TestUncontrolledAddDoesNotSilentlyDropAnItem()
        {
            this.Test(async () =>
            {
                var collection = new BlockingCollection<int>(1);
                collection.Add(1);

                var reached = new TaskCompletionSource<bool>();
                var finished = new TaskCompletionSource<bool>();

                using var timer = new Timer(
                    _ =>
                    {
                        reached.TrySetResult(true);
                        collection.Add(2);
                        finished.TrySetResult(true);
                    }, null, 1, Timeout.Infinite);

                // Freeing the slot is what lets a blocking add complete; the callback is already committed
                // to its add by then.
                await reached.Task;
                int first = collection.Take();
                await finished.Task;

                // A ZERO timeout, deliberately: the callback has already returned from its add, so a correct
                // implementation has the item in hand and an incorrect one never will. Waiting for it
                // instead would turn a dropped item into a hang report, which says far less than the
                // assertion below.
                bool tookSecond = collection.TryTake(out int second, 0);

                Specification.Assert(first is 1, "Took {0} instead of the queued 1.", first);
                Specification.Assert(tookSecond && second is 2,
                    "The item added from an uncontrolled thread was silently dropped.");
            },
            configuration: this.GetConfiguration()
                .WithPartiallyControlledConcurrencyAllowed()
                .WithTestingIterations(10));
        }

        /// <summary>
        /// A collection that outlives the iteration that created it, which is what a process-lifetime
        /// singleton amounts to.
        /// </summary>
        private static BlockingCollection<int> SharedCollection;

        // THE OWNERSHIP CHECK, and everything it currently walks past. A wrapper carries the id of the
        // runtime that built it, and touching it from a later iteration is reported — but only from the
        // handful of members that bothered to ask. Every member below reaches the same wrapper, holding
        // waiter sets and resource ids belonging to a runtime that no longer exists.
        //
        // The last case is the one that is not about a forgotten call at all: the take-any and add-any
        // paths do ask, but they ask 'wrappers[0]' and then use the answer for the whole array, so a stale
        // collection is invisible for as long as it is not the first element.
        [Theory(Timeout = 10000)]
        [InlineData("Count")]
        [InlineData("BoundedCapacity")]
        [InlineData("IsAddingCompleted")]
        [InlineData("IsCompleted")]
        [InlineData("CompleteAdding")]
        [InlineData("CopyTo")]
        [InlineData("ToArray")]
        [InlineData("Dispose")]
        [InlineData("TakeFromAnyAtIndexOne")]
        public void TestStaleCollectionIsDetected(string operation)
        {
            SharedCollection = null;
            try
            {
                this.TestWithError(() =>
                {
                    if (SharedCollection is null)
                    {
                        // The first iteration only creates it. Everything below runs against a wrapper
                        // built by a runtime that has since been torn down.
                        SharedCollection = new BlockingCollection<int>(4);
                        return;
                    }

                    BlockingCollection<int> stale = SharedCollection;
                    switch (operation)
                    {
                        case "Count":
                            _ = stale.Count;
                            break;
                        case "BoundedCapacity":
                            _ = stale.BoundedCapacity;
                            break;
                        case "IsAddingCompleted":
                            _ = stale.IsAddingCompleted;
                            break;
                        case "IsCompleted":
                            _ = stale.IsCompleted;
                            break;
                        case "CompleteAdding":
                            stale.CompleteAdding();
                            break;
                        case "CopyTo":
                            stale.CopyTo(new int[4], 0);
                            break;
                        case "ToArray":
                            _ = stale.ToArray();
                            break;
                        case "Dispose":
                            stale.Dispose();
                            break;
                        case "TakeFromAnyAtIndexOne":
                            var fresh = new BlockingCollection<int>(4);
                            BlockingCollection<int>.TryTakeFromAny(new[] { fresh, stale }, out int _, 0);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(operation));
                    }
                },
                configuration: this.GetConfiguration().WithTestingIterations(2),
                errorChecker: (e) =>
                {
                    Assert.Contains("was created in a previous test iteration", e, StringComparison.Ordinal);
                });
            }
            finally
            {
                SharedCollection = null;
            }
        }

        /// <summary>
        /// A backing store that records whether the runtime lock was held when the controlled collection
        /// reached it.
        /// </summary>
        /// <remarks>
        /// Not rewritten: this measures the runtime state of the operation that called it, and
        /// instrumenting it would add scheduling points and collection guards to the very window being
        /// measured. The base type consults a backing store only on the paths that actually move an item,
        /// so a recorded observation always corresponds to a real storage mutation.
        /// </remarks>
        [SkipRewriting("Observes runtime state from inside a controlled operation; rewriting it would perturb what it measures.")]
        private sealed class ObservingStore : IProducerConsumerCollection<int>
        {
            private readonly Queue<int> Items = new Queue<int>();

            internal bool? WasSynchronizedDuringAdd;
            internal bool? WasSynchronizedDuringTake;

            public int Count => this.Items.Count;

            public bool IsSynchronized => false;

            public object SyncRoot => this;

            public bool TryAdd(int item)
            {
                this.WasSynchronizedDuringAdd ??= CoyoteRuntime.IsExecutionSynchronized;
                this.Items.Enqueue(item);
                return true;
            }

            public bool TryTake(out int item)
            {
                this.WasSynchronizedDuringTake ??= CoyoteRuntime.IsExecutionSynchronized;
                if (this.Items.Count is 0)
                {
                    item = default;
                    return false;
                }

                item = this.Items.Dequeue();
                return true;
            }

            public void CopyTo(int[] array, int index) => this.Items.CopyTo(array, index);

            public void CopyTo(Array array, int index) => ((ICollection)this.Items).CopyTo(array, index);

            public int[] ToArray() => this.Items.ToArray();

            public IEnumerator<int> GetEnumerator() => this.Items.GetEnumerator();

            IEnumerator IEnumerable.GetEnumerator() => this.Items.GetEnumerator();
        }
    }
}

// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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

        // RESOURCE-WINS, and the witness for BUDGET PRESERVATION. A timed wait draws its abstract budget
        // once and carries the remainder across every wake; re-drawing it on each pause instead makes this
        // test fail, because a redraw can land on zero and expire a wait that a concurrent add was about to
        // satisfy. (Verified by mutation: replacing the carried remainder with a fresh draw fails exactly
        // this test.)
        [Fact(Timeout = 5000)]
        public void TestFiniteTimeoutSucceedsWhenAnAddArrives()
        {
            this.Test(() =>
            {
                var collection = new BlockingCollection<int>(4);
                bool took = false;

                Task consumer = Task.Run(() => took = collection.TryTake(out int _, 1000));
                Task producer = Task.Run(() => collection.Add(1));

                Task.WaitAll(consumer, producer);

                Specification.Assert(took, "A finite-timeout take was not satisfied by a concurrent add.");
            });
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
    }
}

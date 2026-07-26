// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#if NET
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Coyote.Specifications;
using Microsoft.Coyote.SystematicTesting;
using Xunit;
using Xunit.Abstractions;
using CoyoteChannels = Microsoft.Coyote.Rewriting.Types.Threading.Channels;

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// Tests for the controlled <see cref="Channel"/> mock. The test assembly is rewritten, so the
    /// <c>Channel.Create*</c> factory calls below are redirected to the controlled implementation and the
    /// reader/writer calls dispatch into it — a wait on an empty/full channel is a scheduling decision,
    /// not an invisible framework wake (which the scheduler cannot observe and would flag as a deadlock).
    /// </summary>
    public class ChannelTests : BaseBugFindingTest
    {
        public ChannelTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestChannelFactoriesAreRedirected()
        {
            this.Test(() =>
            {
                var channels = new object[]
                {
                    Channel.CreateUnbounded<int>(),
                    Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }),
                    Channel.CreateBounded<int>(4),
                    Channel.CreateBounded<int>(new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest }),
                    Channel.CreateBounded<int>(
                        new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropWrite }, _ => { }),
                };

                foreach (var channel in channels)
                {
                    Specification.Assert(channel is CoyoteChannels.ControlledChannel<int>,
                        "Channel factory was not redirected to the controlled mock: '{0}'.",
                        channel.GetType().FullName);
                }
            });
        }

        [Fact(Timeout = 5000)]
        public void TestColdWaitToReadIsWokenByWrite()
        {
            // The exact scenario that false-deadlocked before the mock: a reader parks on WaitToReadAsync of
            // an empty channel and is woken by a write from another operation. With the mock the wake is a
            // controlled scheduling decision, so no schedule reports a spurious deadlock.
            this.Test(async () =>
            {
                Channel<int> channel = Channel.CreateUnbounded<int>();

                Task reader = Task.Run(async () =>
                {
                    bool available = await channel.Reader.WaitToReadAsync();
                    Specification.Assert(available, "Reader was not signaled that data is available.");
                    Specification.Assert(channel.Reader.TryRead(out int value) && value is 42,
                        "Reader did not observe the written item.");
                });

                Task writer = Task.Run(() => channel.Writer.TryWrite(42));

                await Task.WhenAll(reader, writer);
            },
            this.GetConfiguration().WithTestingIterations(200));
        }

        [Fact(Timeout = 5000)]
        public void TestProducerConsumerInterleaving()
        {
            this.Test(async () =>
            {
                Channel<int> channel = Channel.CreateUnbounded<int>();
                const int count = 3;

                Task producer = Task.Run(() =>
                {
                    for (int i = 0; i < count; i++)
                    {
                        channel.Writer.TryWrite(i);
                    }

                    channel.Writer.TryComplete();
                });

                Task consumer = Task.Run(async () =>
                {
                    int sum = 0;
                    while (await channel.Reader.WaitToReadAsync())
                    {
                        while (channel.Reader.TryRead(out int value))
                        {
                            sum += value;
                        }
                    }

                    Specification.Assert(sum is 0 + 1 + 2, "Consumer summed {0} instead of 3.", sum);
                });

                await Task.WhenAll(producer, consumer);
            },
            this.GetConfiguration().WithTestingIterations(200));
        }

        [Fact(Timeout = 5000)]
        public void TestReadAsyncHandoffToCompetingReaders()
        {
            // Two ReadAsync waiters and two writes: each reader consumes exactly one distinct item.
            this.Test(async () =>
            {
                Channel<int> channel = Channel.CreateUnbounded<int>();

                Task<int> r1 = Task.Run(async () => await channel.Reader.ReadAsync());
                Task<int> r2 = Task.Run(async () => await channel.Reader.ReadAsync());

                Task writer = Task.Run(() =>
                {
                    channel.Writer.TryWrite(1);
                    channel.Writer.TryWrite(2);
                });

                await Task.WhenAll(r1, r2, writer);
                Specification.Assert(r1.Result + r2.Result is 3 && r1.Result != r2.Result,
                    "Readers did not each consume a distinct item ({0}, {1}).", r1.Result, r2.Result);
            },
            this.GetConfiguration().WithTestingIterations(300));
        }

        [Fact(Timeout = 5000)]
        public void TestBoundedWriteAsyncParksUntilRead()
        {
            // Capacity 1, Wait mode: the first item buffers; a second WriteAsync parks until a read frees the
            // slot, and the parked item is delivered in order.
            this.Test(async () =>
            {
                Channel<int> channel = Channel.CreateBounded<int>(
                    new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait });

                Specification.Assert(channel.Writer.TryWrite(1), "First write should buffer.");

                Task writer = Task.Run(async () => await channel.Writer.WriteAsync(2));

                Task reader = Task.Run(async () =>
                {
                    int first = await channel.Reader.ReadAsync();
                    Specification.Assert(first is 1, "Expected 1 first, got {0}.", first);
                    int second = await channel.Reader.ReadAsync();
                    Specification.Assert(second is 2, "Expected 2 second, got {0}.", second);
                });

                await Task.WhenAll(writer, reader);
            },
            this.GetConfiguration().WithTestingIterations(300));
        }

        [Fact(Timeout = 5000)]
        public void TestCompletionResolvesAfterDrain()
        {
            this.Test(async () =>
            {
                Channel<int> channel = Channel.CreateUnbounded<int>();
                channel.Writer.TryWrite(1);
                channel.Writer.TryWrite(2);
                bool completed = channel.Writer.TryComplete();
                Specification.Assert(completed, "TryComplete should succeed the first time.");
                Specification.Assert(!channel.Writer.TryComplete(), "TryComplete should fail the second time.");

                // Buffered data is still readable after completion.
                Specification.Assert(await channel.Reader.WaitToReadAsync(), "Data should remain readable.");
                Specification.Assert(channel.Reader.TryRead(out int a) && a is 1, "Expected 1.");
                Specification.Assert(await channel.Reader.WaitToReadAsync(), "Second item should remain readable.");
                Specification.Assert(channel.Reader.TryRead(out int b) && b is 2, "Expected 2.");

                // Drained + completed: no more data, and Completion resolves.
                Specification.Assert(!await channel.Reader.WaitToReadAsync(), "Channel should be drained.");
                await channel.Reader.Completion;
            });
        }

        [Fact(Timeout = 5000)]
        public void TestFaultedCompletion()
        {
            this.Test(async () =>
            {
                Channel<int> channel = Channel.CreateUnbounded<int>();
                var error = new InvalidOperationException("boom");
                channel.Writer.TryComplete(error);

                bool waitThrew = false;
                try
                {
                    await channel.Reader.WaitToReadAsync();
                }
                catch (InvalidOperationException e)
                {
                    waitThrew = e.Message == "boom";
                }

                Specification.Assert(waitThrew, "WaitToReadAsync should surface the completion error.");

                bool writeThrew = false;
                try
                {
                    await channel.Writer.WriteAsync(1);
                }
                catch (ChannelClosedException)
                {
                    writeThrew = true;
                }

                Specification.Assert(writeThrew, "WriteAsync on a completed channel should throw ChannelClosedException.");
                Specification.Assert(!channel.Writer.TryWrite(1), "TryWrite on a completed channel should fail.");
            });
        }

        [Fact(Timeout = 5000)]
        public void TestPreCanceledWaitToRead()
        {
            this.Test(async () =>
            {
                Channel<int> channel = Channel.CreateUnbounded<int>();
                using var cts = new CancellationTokenSource();
                cts.Cancel();

                bool canceled = false;
                try
                {
                    await channel.Reader.WaitToReadAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                }

                Specification.Assert(canceled, "A pre-canceled WaitToReadAsync should throw OperationCanceledException.");
            });
        }

        [Fact(Timeout = 5000)]
        public void TestCancelMidWaitDoesNotBlockLaterReader()
        {
            // A reader parks and is canceled; a subsequent write/read must still succeed — a canceled waiter,
            // left stale in the reader queue, must not block later FIFO progress. Draining the canceled reader
            // before the write keeps the invariant deterministic under every schedule.
            this.Test(async () =>
            {
                Channel<int> channel = Channel.CreateUnbounded<int>();
                using var cts = new CancellationTokenSource();

                Task canceledReader = Task.Run(async () =>
                {
                    bool canceled = false;
                    try
                    {
                        await channel.Reader.ReadAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        canceled = true;
                    }

                    Specification.Assert(canceled, "The parked reader should observe cancellation.");
                });

                cts.Cancel();
                await canceledReader;

                // The stale canceled waiter must be skipped: a fresh write reaches a fresh read.
                Specification.Assert(channel.Writer.TryWrite(7), "Write after cancellation should succeed.");
                int value = await channel.Reader.ReadAsync();
                Specification.Assert(value is 7, "The later read should observe the written item, got {0}.", value);
            },
            this.GetConfiguration().WithTestingIterations(300));
        }

        [Fact(Timeout = 5000)]
        public void TestDropOldestInvokesCallback()
        {
            this.Test(() =>
            {
                var dropped = new List<int>();
                Channel<int> channel = Channel.CreateBounded<int>(
                    new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropOldest }, dropped.Add);

                Specification.Assert(channel.Writer.TryWrite(1), "write 1");
                Specification.Assert(channel.Writer.TryWrite(2), "write 2");
                Specification.Assert(channel.Writer.TryWrite(3), "write 3 (drops oldest)");

                Specification.Assert(dropped.Count is 1 && dropped[0] is 1, "Oldest item (1) should have been dropped.");
                Specification.Assert(channel.Reader.TryRead(out int a) && a is 2, "Expected 2 after drop-oldest.");
                Specification.Assert(channel.Reader.TryRead(out int b) && b is 3, "Expected 3 after drop-oldest.");
            });
        }

        [Fact(Timeout = 5000)]
        public void TestDropWriteInvokesCallback()
        {
            this.Test(() =>
            {
                var dropped = new List<int>();
                Channel<int> channel = Channel.CreateBounded<int>(
                    new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite }, dropped.Add);

                Specification.Assert(channel.Writer.TryWrite(1), "write 1");
                Specification.Assert(channel.Writer.TryWrite(2), "write 2 (dropped, buffer keeps 1)");

                Specification.Assert(dropped.Count is 1 && dropped[0] is 2, "The new item (2) should have been dropped.");
                Specification.Assert(channel.Reader.TryRead(out int a) && a is 1, "Expected the buffered 1 to remain.");
            });
        }

        [Fact(Timeout = 5000)]
        public void TestDropNewestInvokesCallback()
        {
            this.Test(() =>
            {
                var dropped = new List<int>();
                Channel<int> channel = Channel.CreateBounded<int>(
                    new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropNewest }, dropped.Add);

                channel.Writer.TryWrite(1);
                channel.Writer.TryWrite(2);
                channel.Writer.TryWrite(3); // drops the newest buffered (2), then appends 3

                Specification.Assert(dropped.Count is 1 && dropped[0] is 2, "The newest buffered item (2) should drop.");
                Specification.Assert(channel.Reader.TryRead(out int a) && a is 1, "Expected 1.");
                Specification.Assert(channel.Reader.TryRead(out int b) && b is 3, "Expected 3.");
            });
        }

        [Fact(Timeout = 5000)]
        public void TestReadAllAsyncEnumeratesUntilCompletion()
        {
            this.Test(async () =>
            {
                Channel<int> channel = Channel.CreateUnbounded<int>();

                Task producer = Task.Run(() =>
                {
                    for (int i = 0; i < 3; i++)
                    {
                        channel.Writer.TryWrite(i);
                    }

                    channel.Writer.TryComplete();
                });

                Task consumer = Task.Run(async () =>
                {
                    int sum = 0;
                    await foreach (int value in channel.Reader.ReadAllAsync())
                    {
                        sum += value;
                    }

                    Specification.Assert(sum is 0 + 1 + 2, "ReadAllAsync summed {0} instead of 3.", sum);
                });

                await Task.WhenAll(producer, consumer);
            },
            this.GetConfiguration().WithTestingIterations(200));
        }

        [Fact(Timeout = 10000)]
        public void TestReadAllAsyncHonorsBothCancellationTokens()
        {
            // 'ReadAllAsync(ct1)' hands one token to the enumerable and 'GetAsyncEnumerator(ct2)'
            // (what 'await foreach ... .WithCancellation(ct2)' compiles to) hands it another. Both
            // must be able to stop a parked move-next; honoring only one leaves the enumeration
            // blocked forever on a channel that is never written to or completed.
            foreach (bool cancelEnumerator in new[] { false, true })
            {
                this.Test(async () =>
                {
                    Channel<int> channel = Channel.CreateUnbounded<int>();
                    using var enumerableCts = new CancellationTokenSource();
                    using var enumeratorCts = new CancellationTokenSource();

                    IAsyncEnumerator<int> enumerator = channel.Reader.ReadAllAsync(enumerableCts.Token)
                        .GetAsyncEnumerator(enumeratorCts.Token);

                    Task consumer = Task.Run(async () =>
                    {
                        bool canceled = false;
                        try
                        {
                            await enumerator.MoveNextAsync();
                        }
                        catch (OperationCanceledException)
                        {
                            canceled = true;
                        }

                        Specification.Assert(canceled, "The enumeration should observe cancellation.");
                    });

                    if (cancelEnumerator)
                    {
                        enumeratorCts.Cancel();
                    }
                    else
                    {
                        enumerableCts.Cancel();
                    }

                    await consumer;
                    await enumerator.DisposeAsync();
                },
                this.GetConfiguration().WithTestingIterations(200));
            }
        }

#if NET10_0_OR_GREATER
        /// <summary>
        /// A zero capacity channel buffers nothing: each item passes from a writer to a reader directly,
        /// and whichever side arrives first waits for the other. Guarded to .NET 10, which is where the
        /// shape was added; earlier frameworks reject a capacity of zero outright.
        /// </summary>
        /// <remarks>
        /// The expectations below were taken from the real <c>RendezvousChannel&lt;T&gt;</c> rather than
        /// derived, including the ones that look inconsistent: <c>Count</c> stays zero while a writer is
        /// parked, yet <c>TryPeek</c> returns that writer's item.
        /// </remarks>
        [Fact(Timeout = 10000)]
        public void TestRendezvousReaderArrivingFirstIsHandedTheItem()
        {
            // The case the capacity guard used to exist to avoid: a reader parks with nothing buffered
            // to take, and must still be woken by a writer that arrives afterwards. Reading the pending
            // write is the whole mechanism; without it both sides wait for each other forever and the
            // run reports a deadlock that the program under test never had.
            this.Test(async () =>
            {
                Channel<int> channel = Channel.CreateBounded<int>(0);
                Task<int> reader = Task.Run(async () => await channel.Reader.ReadAsync());
                Task writer = Task.Run(async () => await channel.Writer.WriteAsync(42));

                await Task.WhenAll(reader, writer);
                Specification.Assert(reader.Result is 42, "The reader should receive the written item.");
            },
            this.GetConfiguration().WithTestingIterations(200));
        }

        [Fact(Timeout = 10000)]
        public void TestRendezvousWriterWaitsForAReader()
        {
            // The mirror image, and the half that already worked: a write with no reader waiting parks
            // holding its item, and completes only once a read takes it.
            this.Test(async () =>
            {
                Channel<int> channel = Channel.CreateBounded<int>(0);
                Task writer = Task.Run(async () => await channel.Writer.WriteAsync(7));

                Specification.Assert(channel.Reader.Count is 0,
                    "A rendezvous channel buffers nothing, so its count stays zero.");

                int read = await channel.Reader.ReadAsync();
                await writer;
                Specification.Assert(read is 7, "The read should take the parked writer's item.");
            },
            this.GetConfiguration().WithTestingIterations(200));
        }

        [Fact(Timeout = 10000)]
        public void TestRendezvousHandsOverEveryItemInOrder()
        {
            // The shape a rendezvous channel is actually used in. Every item must arrive exactly once
            // and in order, under every interleaving the scheduler explores — which is the coverage the
            // capacity guard was giving up.
            this.Test(async () =>
            {
                const int Count = 5;
                Channel<int> channel = Channel.CreateBounded<int>(0);

                Task producer = Task.Run(async () =>
                {
                    for (int idx = 0; idx < Count; idx++)
                    {
                        await channel.Writer.WriteAsync(idx);
                    }

                    channel.Writer.Complete();
                });

                Task consumer = Task.Run(async () =>
                {
                    int expected = 0;
                    await foreach (int item in channel.Reader.ReadAllAsync())
                    {
                        Specification.Assert(item == expected, "Items should arrive in the order written.");
                        expected++;
                    }

                    Specification.Assert(expected == Count, "Every item should arrive exactly once.");
                });

                await Task.WhenAll(producer, consumer);
            },
            this.GetConfiguration().WithTestingIterations(200));
        }

        [Fact(Timeout = 10000)]
        public void TestRendezvousSynchronousAndWaitOperations()
        {
            // 'TryWrite' succeeds only against a reader that is already parked, 'TryRead' only against a
            // writer that is, and the two 'WaitTo' operations report exactly that. Each side must also
            // wake the other's waiter when it parks, or a 'WaitToWriteAsync'/'WaitToReadAsync' loop
            // never makes progress.
            this.Test(async () =>
            {
                Channel<int> idle = Channel.CreateBounded<int>(0);
                Specification.Assert(!idle.Writer.TryWrite(1), "A write with no reader waiting should not be taken.");
                Specification.Assert(!idle.Reader.TryRead(out _), "A read with no writer waiting should find nothing.");
                Specification.Assert(!idle.Reader.TryPeek(out _), "A peek with no writer waiting should find nothing.");

                Channel<int> parkedWriter = Channel.CreateBounded<int>(0);
                Task writer = Task.Run(async () => await parkedWriter.Writer.WriteAsync(3));
                Specification.Assert(await parkedWriter.Reader.WaitToReadAsync(),
                    "A parked writer should make a read possible.");
                Specification.Assert(parkedWriter.Reader.TryPeek(out int peeked) && peeked is 3,
                    "A peek should see the parked writer's item.");
                Specification.Assert(parkedWriter.Reader.TryPeek(out _),
                    "A peek should not consume the parked writer's item.");
                Specification.Assert(parkedWriter.Reader.TryRead(out int taken) && taken is 3,
                    "A read should take the parked writer's item.");
                await writer;

                Channel<int> parkedReader = Channel.CreateBounded<int>(0);
                Task<int> reader = Task.Run(async () => await parkedReader.Reader.ReadAsync());
                Specification.Assert(await parkedReader.Writer.WaitToWriteAsync(),
                    "A parked reader should make a write possible.");
                Specification.Assert(parkedReader.Writer.TryWrite(9), "A write should reach the parked reader.");
                Specification.Assert(await reader is 9, "The parked reader should receive the written item.");
            },
            this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 10000)]
        public void TestRendezvousDropModeDropsWritesWithNoReader()
        {
            // With no buffer there is no older or newer item to evict, so every drop mode drops the
            // incoming item instead — but only when nobody is waiting for it. A parked reader still
            // wins, in every mode. Both facts are the real channel's, not this mock's invention.
            this.Test(async () =>
            {
                foreach (BoundedChannelFullMode mode in new[]
                {
                    BoundedChannelFullMode.DropWrite,
                    BoundedChannelFullMode.DropOldest,
                    BoundedChannelFullMode.DropNewest
                })
                {
                    var dropped = new List<int>();
                    Channel<int> channel = Channel.CreateBounded<int>(
                        new BoundedChannelOptions(0) { FullMode = mode }, dropped.Add);

                    Specification.Assert(channel.Writer.TryWrite(1), "A drop mode always accepts the write.");
                    Specification.Assert(dropped.Count is 1 && dropped[0] is 1,
                        "With nobody waiting, the incoming item is the one dropped.");

                    await channel.Writer.WriteAsync(2);
                    Specification.Assert(dropped.Count is 2 && dropped[1] is 2,
                        "An asynchronous write completes by dropping rather than parking.");
                    Specification.Assert(await channel.Writer.WaitToWriteAsync(),
                        "A drop mode never has to wait to write.");
                    Specification.Assert(channel.Reader.Count is 0 && !channel.Reader.TryRead(out _),
                        "Nothing is left behind for a reader to find.");
                }

                // Whether a parked reader takes the item ahead of the drop policy is deliberately not
                // asserted here. Getting a reader parked first would need the scheduler to be pinned to
                // one interleaving, and any other ordering drops the item and hangs the reader. The
                // hand-off itself is covered by the Wait-mode tests above, which share the same code
                // path; what is new below the drop modes is only the no-reader case checked here.
            },
            this.GetConfiguration().WithTestingIterations(50));
        }

        [Fact(Timeout = 5000)]
        public void TestPrioritizedChannelIsReportedAsUncontrolled()
        {
            // A prioritized channel keeps the real implementation, because a priority queue decides the
            // order items come out in and the controlled channel is a FIFO. It is redirected all the
            // same, so that the coverage lost by not controlling it is reported instead of a green run
            // silently having explored fewer interleavings than it looks like.
            TestReport report = this.RunSystematicTest(() =>
            {
                Channel<int> plain = Channel.CreateUnboundedPrioritized<int>();
                Channel<int> configured = Channel.CreateUnboundedPrioritized<int>(
                    new UnboundedPrioritizedChannelOptions<int> { SingleReader = true });

                Specification.Assert(!(plain is CoyoteChannels.ControlledChannel<int>),
                    "A prioritized channel is not controlled.");
                Specification.Assert(!(configured is CoyoteChannels.ControlledChannel<int>),
                    "A prioritized channel from options is not controlled either.");
            },
            this.GetConfiguration().WithTestingIterations(1));

            Assert.Contains("A prioritized channel", report.UncontrolledInvocations);
        }

        [Fact(Timeout = 10000)]
        public void TestRendezvousCompletionFailsParkedWriters()
        {
            // Completing while a writer is parked has to fault that write rather than leave it parked
            // for a reader that can no longer come. The completion races the write deliberately rather
            // than waiting for it to park: no reader ever arrives, so the write must fault under every
            // ordering, and over these iterations the scheduler does explore the parked-first one.
            // ('WaitToWriteAsync' cannot serve as the barrier — with no reader it parks as well.)
            this.Test(async () =>
            {
                Channel<int> channel = Channel.CreateBounded<int>(0);
                Task writer = Task.Run(async () => await channel.Writer.WriteAsync(1));

                channel.Writer.Complete();

                bool faulted = false;
                try
                {
                    await writer;
                }
                catch (ChannelClosedException)
                {
                    faulted = true;
                }

                Specification.Assert(faulted, "A parked write should fault once the channel is completed.");
                Specification.Assert(!await channel.Reader.WaitToReadAsync(),
                    "A completed rendezvous channel has nothing more to read.");
                await channel.Reader.Completion;
            },
            this.GetConfiguration().WithTestingIterations(100));
        }
#endif

        [Fact(Timeout = 5000)]
        public void TestInvalidFactoryArgumentsThrow()
        {
            // The redirected factories must behave like the real ones for arguments the controlled
            // channel cannot represent, so that what the program under test really hit is what it
            // sees rather than a deadlock report from a channel no write could fit into.
            this.Test(() =>
            {
                bool threwOnNegativeCapacity = false;
                try
                {
                    Channel.CreateBounded<int>(-1);
                }
                catch (ArgumentOutOfRangeException)
                {
                    threwOnNegativeCapacity = true;
                }

                Specification.Assert(threwOnNegativeCapacity, "CreateBounded should reject a negative capacity.");

                // A zero capacity is the rendezvous channel on .NET 10 and an argument error on every
                // earlier framework. Which one it is decides what must happen, so the outcome is
                // asserted per framework rather than caught and ignored: swallowing the exception would
                // let the redirection quietly hand back a working channel where the real one throws.
                // Both the integer overload and the options overloads can ask for a zero, so all three
                // are covered.
#if NET10_0_OR_GREATER
                Channel<int> zeroCapacity = Channel.CreateBounded<int>(0);
                Specification.Assert(zeroCapacity is CoyoteChannels.ControlledChannel<int>,
                    "A zero capacity channel should be controlled.");

                Channel<int> fromOptions = Channel.CreateBounded<int>(new BoundedChannelOptions(0));
                Specification.Assert(fromOptions is CoyoteChannels.ControlledChannel<int>,
                    "A zero capacity channel from options should be controlled too.");

                Channel<int> withCallback = Channel.CreateBounded<int>(
                    new BoundedChannelOptions(0), _ => { });
                Specification.Assert(withCallback is CoyoteChannels.ControlledChannel<int>,
                    "A zero capacity channel from options with a drop callback should be controlled too.");
#else
                bool threwOnZeroCapacity = false;
                try
                {
                    Channel.CreateBounded<int>(0);
                }
                catch (ArgumentOutOfRangeException)
                {
                    threwOnZeroCapacity = true;
                }

                Specification.Assert(threwOnZeroCapacity,
                    "CreateBounded should reject a zero capacity on a framework without rendezvous channels.");

                bool threwOnZeroCapacityOptions = false;
                try
                {
                    Channel.CreateBounded<int>(new BoundedChannelOptions(0));
                }
                catch (ArgumentOutOfRangeException)
                {
                    threwOnZeroCapacityOptions = true;
                }

                Specification.Assert(threwOnZeroCapacityOptions,
                    "BoundedChannelOptions should reject a zero capacity on a framework without rendezvous channels.");
#endif

                bool threwOnNullOptions = false;
                try
                {
                    Channel.CreateUnbounded<int>(default(UnboundedChannelOptions));
                }
                catch (ArgumentNullException)
                {
                    threwOnNullOptions = true;
                }

                Specification.Assert(threwOnNullOptions, "CreateUnbounded should reject null options.");

                bool threwOnNullBoundedOptions = false;
                try
                {
                    Channel.CreateBounded<int>(default(BoundedChannelOptions));
                }
                catch (ArgumentNullException)
                {
                    threwOnNullBoundedOptions = true;
                }

                Specification.Assert(threwOnNullBoundedOptions, "CreateBounded should reject null options.");
            });
        }

        [Fact(Timeout = 5000)]
        public void TestGenuineDeadlockIsStillDetected()
        {
            // A read that can never complete (no writer, no completion) is a real deadlock — the mock must not
            // blind the detector.
            this.TestWithError(async () =>
            {
                Channel<int> channel = Channel.CreateUnbounded<int>();
                await channel.Reader.WaitToReadAsync();
            },
            errorChecker: (e) =>
            {
                Assert.StartsWith("Deadlock detected.", e);
            },
            replay: true);
        }
    }
}
#endif

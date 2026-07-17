// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#if NET
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

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
                    string name = channel.GetType().FullName;
                    Specification.Assert(name.Contains("Microsoft.Coyote.Rewriting.Types.Threading.Channels"),
                        "Channel factory was not redirected to the controlled mock: '{0}'.", name);
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

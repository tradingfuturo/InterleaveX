// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Threading;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    public class ThreadRunTests : BaseBugFindingTest
    {
        public ThreadRunTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestThreadStartAndJoin()
        {
            this.Test(() =>
            {
                bool isDone = false;
                Thread t = new Thread(() =>
                {
                    isDone = true;
                });

                t.Start();
                t.Join();

                Specification.Assert(isDone, "The expected condition was not satisfied.");
                Specification.Assert(t.ThreadState is ThreadState.Stopped, "State of thread '{0}' is {1} instead of Stopped.",
                    t.ManagedThreadId, t.ThreadState);
            },
            configuration: this.GetConfiguration().WithTestingIterations(10));
        }

        [Fact(Timeout = 5000)]
        public void TestParameterizedThreadStartAndJoin()
        {
            this.Test(() =>
            {
                bool isDone = false;
                Thread t = new Thread(input =>
                {
                    if ((int)input is 7)
                    {
                        isDone = true;
                    }
                });

                t.Start(7);
                t.Join();

                Specification.Assert(isDone, "The expected condition was not satisfied.");
                Specification.Assert(t.ThreadState is ThreadState.Stopped, "State of thread '{0}' is {1} instead of Stopped.",
                    t.ManagedThreadId, t.ThreadState);
            },
            configuration: this.GetConfiguration().WithTestingIterations(10));
        }

        [Fact(Timeout = 5000)]
        public void TestNamedAndRenamedThreadsStayControlled()
        {
            // The runtime once mapped a controlled thread to its operation by Thread.Name, so a thread named by the
            // code under test - at construction or from inside its own body - ran as an uncontrolled one and its
            // wait on a modelled event was reported as a potential deadlock.
            this.Test(() =>
            {
                using var ready = new AutoResetEvent(false);
                bool isDone = false;
                Thread named = new Thread(() =>
                {
                    Thread.CurrentThread.Name = "Renamed";
                    ready.WaitOne();
                    isDone = true;
                })
                {
                    Name = "Writer",
                    IsBackground = true,
                };

                named.Start();
                ready.Set();
                named.Join();

                Specification.Assert(isDone, "The named thread did not finish.");
            },
            configuration: this.GetConfiguration().WithTestingIterations(50)
                .WithPartiallyControlledConcurrencyAllowed(false)
                .WithPartiallyControlledDataNondeterminismAllowed(false)
                .WithSystematicFuzzingFallbackEnabled(false));
        }

        [Fact(Timeout = 5000)]
        public void TestThreadStartAndJoinStress()
        {
            this.Test(() =>
            {
                int counter = 0;
                int threadCount = 25;

                Thread[] threads = new Thread[threadCount];
                for (int i = 0; i < threads.Length; i++)
                {
                    threads[i] = new Thread(() =>
                    {
                        Interlocked.Increment(ref counter);
                    });
                }

                for (int i = 0; i < threads.Length; i++)
                {
                    threads[i].Start();
                }

                for (int i = 0; i < threads.Length; i++)
                {
                    threads[i].Join();
                }

                for (int i = 0; i < threads.Length; i++)
                {
                    ThreadState state = threads[i].ThreadState;
                    Specification.Assert(state is ThreadState.Stopped, "State of thread '{0}' is {1} instead of Stopped.",
                        threads[i].ManagedThreadId, state);
                }

                Specification.Assert(counter == threadCount, "Counter is {0} instead of {1}.", counter, threadCount);
            },
            configuration: this.GetConfiguration().WithTestingIterations(10));
        }
    }
}

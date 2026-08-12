// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

#if NET
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// Coverage for the controlled <see cref="PeriodicTimer"/>.
    /// <para>
    /// A <c>while (await timer.WaitForNextTickAsync(token))</c> loop is the standard shape for a background
    /// service. Unmodelled, its tick arrives from a real timer on a thread the runtime has no record of, so
    /// the loop is never interleaved with anything and a test over it explores no schedules while paying the
    /// full wall-clock cost of the cadence. Every test here declares a cadence far longer than its own
    /// timeout, so the suite cannot pass unless the tick really is a scheduling point.
    /// </para>
    /// </summary>
    public class PeriodicTimerTests : BaseBugFindingTest
    {
        public PeriodicTimerTests(ITestOutputHelper output)
            : base(output)
        {
        }

        /// <summary>A cadence no test would wait for, so a real tick cannot be mistaken for a modelled one.</summary>
        private static readonly TimeSpan UntestablyLongCadence = TimeSpan.FromMinutes(10);

        [Fact(Timeout = 5000)]
        public void TestPeriodicTimerTicksWithoutWaiting()
        {
            this.Test(async () =>
            {
                using PeriodicTimer timer = new PeriodicTimer(UntestablyLongCadence);

                int ticks = 0;
                while (ticks < 3 && await timer.WaitForNextTickAsync(CancellationToken.None))
                {
                    ticks++;
                }

                Specification.Assert(ticks is 3, "Timer ticked {0} time(s) instead of 3.", ticks);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        public void TestPeriodicTimerReportsTheConfiguredPeriod()
        {
            this.Test(() =>
            {
                using PeriodicTimer timer = new PeriodicTimer(UntestablyLongCadence);
                Specification.Assert(timer.Period == UntestablyLongCadence, "Timer reported the wrong period.");

                timer.Period = TimeSpan.FromSeconds(5);
                Specification.Assert(
                    timer.Period == TimeSpan.FromSeconds(5), "Timer did not take a new period.");
            },
            configuration: this.GetConfiguration());
        }

        [Fact(Timeout = 5000)]
        public void TestPeriodicTimerRejectsAnInvalidPeriod()
        {
            this.Test(() =>
            {
                bool threw = false;
                try
                {
                    using PeriodicTimer timer = new PeriodicTimer(TimeSpan.Zero);
                }
                catch (ArgumentOutOfRangeException)
                {
                    threw = true;
                }

                Specification.Assert(threw, "A zero period was accepted.");
            },
            configuration: this.GetConfiguration());
        }

        [Fact(Timeout = 5000)]
        public void TestDisposedPeriodicTimerReportsNoTick()
        {
            this.Test(async () =>
            {
                PeriodicTimer timer = new PeriodicTimer(UntestablyLongCadence);
                timer.Dispose();

                bool ticked = await timer.WaitForNextTickAsync(CancellationToken.None);
                Specification.Assert(!ticked, "A disposed timer reported a tick.");
            },
            configuration: this.GetConfiguration());
        }

        [Fact(Timeout = 5000)]
        public void TestCancelledPeriodicTimerWaitThrows()
        {
            this.Test(async () =>
            {
                using CancellationTokenSource source = new CancellationTokenSource();
                source.Cancel();

                using PeriodicTimer timer = new PeriodicTimer(UntestablyLongCadence);

                bool threw = false;
                try
                {
                    await timer.WaitForNextTickAsync(source.Token);
                }
                catch (OperationCanceledException)
                {
                    threw = true;
                }

                Specification.Assert(threw, "A cancelled token did not cancel the wait.");
            },
            configuration: this.GetConfiguration());
        }

        [Fact(Timeout = 5000)]
        public void TestPeriodicTimerLoopIsInterleavedWithTheFlowThatStopsIt()
        {
            this.Test(async () =>
            {
                using CancellationTokenSource stopping = new CancellationTokenSource();
                using PeriodicTimer timer = new PeriodicTimer(UntestablyLongCadence);

                bool stopped = false;

                var loop = Task.Run(async () =>
                {
                    try
                    {
                        while (await timer.WaitForNextTickAsync(stopping.Token))
                        {
                            // A loop the scheduler cannot preempt satisfies this for free, which is exactly
                            // why the tick has to be a scheduling point.
                            Specification.Assert(!stopped, "The timer loop ran after it was stopped.");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // The cadence observed the stop, which is how this loop is meant to end.
                    }
                });

                var stopper = Task.Run(() =>
                {
                    stopped = true;
                    stopping.Cancel();
                });

                await Task.WhenAll(loop, stopper);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }

        /// <summary>
        /// The tick is a scheduling point, so a loop body that is not atomic can be interleaved with itself
        /// through another flow — the class of bug the model exists to expose.
        /// </summary>
        [Fact(Timeout = 5000)]
        public void TestPeriodicTimerLoopRacingAnotherFlow()
        {
            this.TestWithError(async () =>
            {
                using PeriodicTimer timer = new PeriodicTimer(UntestablyLongCadence);

                int value = 0;
                bool isOrderHit = false;

                var loop = Task.Run(async () =>
                {
                    for (int tick = 0; tick < 2 && await timer.WaitForNextTickAsync(CancellationToken.None); tick++)
                    {
                        value++;
                        SchedulingPoint.Interleave();
                        isOrderHit |= value is 2;
                        value--;
                    }
                });

                var other = Task.Run(() =>
                {
                    value++;
                    SchedulingPoint.Interleave();
                    value--;
                });

                await Task.WhenAll(loop, other);
                Specification.Assert(!isOrderHit, "Expected assertion failed!");
            },
            configuration: this.GetConfiguration().WithTestingIterations(200),
            expectedError: "Expected assertion failed!",
            replay: true);
        }
    }
}
#endif

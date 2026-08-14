// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;
using ControlledValueTaskAwaiter = Microsoft.Coyote.Runtime.CompilerServices.ValueTaskAwaiter;

namespace Microsoft.Coyote.BugFinding.Tests
{
    public class PeriodicTimerPeriodContractRegressionTests : BaseBugFindingTest
    {
        public PeriodicTimerPeriodContractRegressionTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestPositiveSubMillisecondPeriodIsRejected()
        {
            this.Test(() =>
            {
                bool threw = false;
                try
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromTicks(1));
                }
                catch (ArgumentOutOfRangeException)
                {
                    threw = true;
                }

                Specification.Assert(threw, "A positive sub-millisecond period was accepted.");
            });
        }

        [Fact(Timeout = 5000)]
        public void TestDisposedSetterPublishesValueThenThrows()
        {
            this.Test(() =>
            {
                var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                timer.Dispose();
                TimeSpan replacement = TimeSpan.FromSeconds(2);
                bool threw = false;
                try
                {
                    timer.Period = replacement;
                }
                catch (ObjectDisposedException)
                {
                    threw = true;
                }

                Specification.Assert(threw, "Setting Period after disposal did not throw.");
                Specification.Assert(timer.Period == replacement, "The validated value was not published before the exception.");
            });
        }

        [Fact(Timeout = 5000)]
        public void TestInfinitePeriodWaitsForDisposal()
        {
            this.Test(async () =>
            {
                var timer = new PeriodicTimer(Timeout.InfiniteTimeSpan);
                ValueTask<bool> wait = timer.WaitForNextTickAsync(CancellationToken.None);
                Task disposer = Task.Run(() =>
                {
                    SchedulingPoint.Interleave();
                    Specification.Assert(!wait.IsCompleted, "An infinite-period timer produced a tick.");
                    timer.Dispose();
                });

                Specification.Assert(!await wait, "Disposal did not complete the infinite wait as false.");
                await disposer;
            }, this.GetConfiguration().WithTestingIterations(20));
        }

        [Fact(Timeout = 5000)]
        public void TestChangingInfinitePeriodToFiniteRestartsActiveWait()
        {
            this.Test(async () =>
            {
                using var timer = new PeriodicTimer(Timeout.InfiniteTimeSpan);
                ValueTask<bool> wait = timer.WaitForNextTickAsync(CancellationToken.None);
                Task<bool> completion = ArmWait(ref wait, out var awaiter);
                Specification.Assert(!completion.IsCompleted, "An infinite-period timer produced a tick.");

                timer.Period = TimeSpan.FromSeconds(1);
                await completion;
                while (!wait.IsCompleted)
                {
                    SchedulingPoint.Interleave();
                }

                Specification.Assert(awaiter.GetResult(),
                    "Changing to a finite period did not restart the active wait.");
            }, this.GetConfiguration().WithTestingIterations(20));
        }

        [Fact(Timeout = 5000)]
        public void TestChangingFinitePeriodToInfiniteInvalidatesQueuedTick()
        {
            this.Test(async () =>
            {
                var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                ValueTask<bool> wait;
                Task<bool> completion;
                System.Runtime.CompilerServices.ValueTaskAwaiter<bool> awaiter;
                SchedulingPoint.Suppress();
                try
                {
                    wait = timer.WaitForNextTickAsync(CancellationToken.None);
                    completion = ArmWait(ref wait, out awaiter);
                    timer.Period = Timeout.InfiniteTimeSpan;
                }
                finally
                {
                    SchedulingPoint.Resume();
                }

                Task disposer = Task.Run(() =>
                {
                    SchedulingPoint.Interleave();
                    timer.Dispose();
                });

                await completion;
                while (!wait.IsCompleted)
                {
                    SchedulingPoint.Interleave();
                }

                Specification.Assert(!awaiter.GetResult(),
                    "A tick queued for the old finite period survived the change to infinite.");
                await disposer;
            }, this.GetConfiguration().WithTestingIterations(50));
        }

        [Fact(Timeout = 5000)]
        public void TestChangingFinitePeriodRestartsActiveWait()
        {
            this.Test(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                ValueTask<bool> wait;
                Task<bool> completion;
                System.Runtime.CompilerServices.ValueTaskAwaiter<bool> awaiter;
                SchedulingPoint.Suppress();
                try
                {
                    wait = timer.WaitForNextTickAsync(CancellationToken.None);
                    completion = ArmWait(ref wait, out awaiter);
                    timer.Period = TimeSpan.FromSeconds(2);
                }
                finally
                {
                    SchedulingPoint.Resume();
                }

                await completion;
                while (!wait.IsCompleted)
                {
                    SchedulingPoint.Interleave();
                }

                Specification.Assert(awaiter.GetResult(),
                    "Changing a finite period did not restart the active wait.");
            }, this.GetConfiguration().WithTestingIterations(20));
        }

        private static Task<bool> ArmWait(ref ValueTask<bool> wait,
            out System.Runtime.CompilerServices.ValueTaskAwaiter<bool> awaiter)
        {
            awaiter = wait.GetAwaiter();
            awaiter.OnCompleted(() => { });
            bool found = ControlledValueTaskAwaiter.TryGetTask(ref wait, out Task<bool> completion);
            Specification.Assert(found, "The modeled timer wait did not expose a controlled backing task.");
            return completion;
        }
    }
}
#endif

// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET8_0_OR_GREATER
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
    /// Covers the <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> and delay-constructor models. The strict
    /// configuration refuses partial control, and a framework deadline cancels from the real timer queue, so a wait that
    /// only the deadline can end would park until the periodic monitor reported a hang.
    /// </summary>
    public class CurrentUseCancelAfterTests : BaseBugFindingTest
    {
        public CurrentUseCancelAfterTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestCancelAfterEndsAWaitThatOnlyTheDeadlineCanEnd()
        {
            this.Test(async () =>
            {
                using var source = new CancellationTokenSource();
                source.CancelAfter(TimeSpan.FromHours(1));
                OperationCanceledException canceled = null;
                try
                {
                    await Task.Delay(Timeout.Infinite, source.Token);
                }
                catch (OperationCanceledException ex)
                {
                    canceled = ex;
                }

                Specification.Assert(canceled != null, "The hour-long deadline did not cancel the infinite delay.");
                Specification.Assert(source.IsCancellationRequested, "The source was not canceled at its deadline.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestDelayConstructorCancelsARegistrationWaiter()
        {
            this.Test(async () =>
            {
                using var source = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using CancellationTokenRegistration registration = source.Token.Register(() => canceled.SetResult());
                await canceled.Task;
                Specification.Assert(source.IsCancellationRequested, "The registration ran before cancellation.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestMillisecondsDelayConstructorCancelsInVirtualTime()
        {
            this.Test(async () =>
            {
                using var source = new CancellationTokenSource(250);
                await Task.Delay(300);
                Specification.Assert(source.IsCancellationRequested, "A 250ms source was not canceled after 300ms.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestInfiniteDelayDisarmsAnEarlierDeadline()
        {
            this.Test(async () =>
            {
                using var source = new CancellationTokenSource();
                source.CancelAfter(10);
                source.CancelAfter(Timeout.Infinite);
                await Task.Delay(100);
                Specification.Assert(!source.IsCancellationRequested, "A disarmed deadline still canceled the source.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestLaterDeadlineReplacesAnEarlierOne()
        {
            this.Test(async () =>
            {
                CoyoteRuntime runtime = CoyoteRuntime.Current;
                long start = runtime.GetVirtualTimeTicksForTesting();
                using var source = new CancellationTokenSource();
                source.CancelAfter(10);
                source.CancelAfter(1000);
                long canceledAt = -1;
                using CancellationTokenRegistration registration =
                    source.Token.Register(() => canceledAt = runtime.GetVirtualTimeTicksForTesting() - start);
                await Task.Delay(2000);
                Specification.Assert(canceledAt == 1000 * TimeSpan.TicksPerMillisecond,
                    "The source was canceled {0} ticks after its deadline was replaced with 1000ms.", canceledAt);
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestSourceDisposedBeforeItsDeadlineIsNeverCanceled()
        {
            this.Test(async () =>
            {
                var source = new CancellationTokenSource(10);
                source.Dispose();
                await Task.Delay(50);
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestCancelAfterOnADisposedSourceThrows()
        {
            this.Test(() =>
            {
                var source = new CancellationTokenSource();
                source.Dispose();
                Assert.Throws<ObjectDisposedException>(() => source.CancelAfter(10));
            }, this.GetStrictConfiguration());
        }

        private Configuration GetStrictConfiguration() => this.GetConfiguration()
            .WithTestingIterations(100)
            .WithPartiallyControlledConcurrencyAllowed(false)
            .WithPartiallyControlledDataNondeterminismAllowed(false)
            .WithSystematicFuzzingFallbackEnabled(false);
    }
}
#endif

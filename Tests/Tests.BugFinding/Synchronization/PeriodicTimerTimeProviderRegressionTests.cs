// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET8_0_OR_GREATER
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Specifications;
using Microsoft.Coyote.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    public class PeriodicTimerTimeProviderRegressionTests : BaseBugFindingTest
    {
        public PeriodicTimerTimeProviderRegressionTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "TimeProviderFidelity")]
        public void TestCustomTimeProviderOwnsPeriodicTimerCadence()
        {
            this.Test(() =>
            {
                var provider = new RecordingTimeProvider();
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), provider);
                Specification.Assert(timer.Period == TimeSpan.FromSeconds(1), "The model lost the configured period.");
                Specification.Assert(provider.CreateCount is 1,
                    "The custom TimeProvider was not asked to create the periodic timer.");
                Specification.Assert(provider.LastTimer.DueTime == TimeSpan.FromSeconds(1) &&
                    provider.LastTimer.Period == TimeSpan.FromSeconds(1),
                    "The custom provider received the wrong periodic timer arguments.");
                Specification.Assert(provider.WasExecutionContextFlowSuppressed,
                    "ExecutionContext flow was not suppressed around TimeProvider.CreateTimer.");
            });
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "TimeProviderFidelity")]
        public void TestSystemTimeProviderPeriodicTimerRemainsVirtual()
        {
            this.Test(async () =>
            {
                long clock = Microsoft.Coyote.Runtime.CoyoteRuntime.Current.GetVirtualTimeTicksForTesting();
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(5), TimeProvider.System);
                Specification.Assert(await timer.WaitForNextTickAsync(),
                    "TimeProvider.System did not produce a controlled periodic tick.");
                Specification.Assert(
                    Microsoft.Coyote.Runtime.CoyoteRuntime.Current.GetVirtualTimeTicksForTesting() - clock ==
                    TimeSpan.FromMilliseconds(5).Ticks,
                    "TimeProvider.System PeriodicTimer no longer uses scheduler-owned virtual time.");
            }, this.GetConfiguration().WithTestingIterations(10));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "TimeProviderFidelity")]
        public void TestCustomTimeProviderPeriodicTimerCoalescesAndRetainsSingleConsumer()
        {
            this.Test(async () =>
            {
                var provider = new RecordingTimeProvider();
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), provider);

                provider.LastTimer.Fire();
                provider.LastTimer.Fire();
                ValueTask<bool> queued = timer.WaitForNextTickAsync();
                Specification.Assert(queued.IsCompletedSuccessfully,
                    "A tick fired before a wait was not available synchronously.");
                ValueTask<bool> active = timer.WaitForNextTickAsync();
                Specification.Assert(!active.IsCompleted,
                    "A queued tick incorrectly kept another wait active.");
                Specification.Assert(await queued,
                    "A tick fired before a wait was not retained.");

                provider.LastTimer.Fire();
                Specification.Assert(active.IsCompletedSuccessfully,
                    "A provider tick did not complete an active wait inline.");

                Exception overlap = null;
                try
                {
                    _ = timer.WaitForNextTickAsync();
                }
                catch (Exception ex)
                {
                    overlap = ex;
                }

                Specification.Assert(overlap is InvalidOperationException,
                    "PeriodicTimer allowed another consumer before the prior result was consumed.");
                Specification.Assert(await active,
                    "The active provider-backed wait did not produce a tick.");

                ValueTask<bool> next = timer.WaitForNextTickAsync();
                provider.LastTimer.Fire();
                Specification.Assert(await next,
                    "PeriodicTimer was not reusable after result consumption.");
            }, this.GetConfiguration().WithTestingIterations(10));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "TimeProviderFidelity")]
        public void TestCustomTimeProviderPeriodicTimerPeriodChangesPreserveQueuedTick()
        {
            this.Test(async () =>
            {
                var provider = new RecordingTimeProvider();
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), provider);
                provider.LastTimer.Fire();

                timer.Period = TimeSpan.FromSeconds(2);
                Specification.Assert(timer.Period == TimeSpan.FromSeconds(2),
                    "PeriodicTimer did not publish the changed period.");
                Specification.Assert(provider.LastTimer.ChangeCount is 1 &&
                    provider.LastTimer.DueTime == TimeSpan.FromSeconds(2) &&
                    provider.LastTimer.Period == TimeSpan.FromSeconds(2),
                    "The provider timer did not receive the changed periodic cadence.");
                Specification.Assert(await timer.WaitForNextTickAsync(),
                    "Changing Period invalidated an already queued tick.");

                provider.ChangeResult = false;
                Exception failure = null;
                try
                {
                    timer.Period = TimeSpan.FromSeconds(3);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                Specification.Assert(failure is ObjectDisposedException,
                    "A failed provider timer Change did not produce ObjectDisposedException.");
                Specification.Assert(timer.Period == TimeSpan.FromSeconds(3),
                    "PeriodicTimer did not publish its validated period before Change failed.");
            });
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "TimeProviderFidelity")]
        public void TestCustomTimeProviderPeriodicTimerCancellationAndDisposal()
        {
            this.Test(async () =>
            {
                var provider = new RecordingTimeProvider();
                var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), provider);
                using var cancellation = new CancellationTokenSource();
                ValueTask<bool> canceledWait = timer.WaitForNextTickAsync(cancellation.Token);
                cancellation.Cancel();

                OperationCanceledException failure = null;
                try
                {
                    await canceledWait;
                }
                catch (OperationCanceledException ex)
                {
                    failure = ex;
                }

                Specification.Assert(failure != null && failure.CancellationToken == cancellation.Token,
                    "PeriodicTimer cancellation did not preserve the active wait's token.");

                provider.LastTimer.Fire();
                Specification.Assert(await timer.WaitForNextTickAsync(),
                    "Canceling one wait stopped the provider-owned periodic timer.");

                provider.LastTimer.Fire();
                timer.Dispose();
                Specification.Assert(!await timer.WaitForNextTickAsync(),
                    "Disposal did not void a queued provider tick.");
                Specification.Assert(provider.LastTimer.DisposeCount is 1,
                    "PeriodicTimer disposal did not dispose its provider timer exactly once.");

                Exception periodFailure = null;
                try
                {
                    timer.Period = TimeSpan.FromSeconds(4);
                }
                catch (Exception ex)
                {
                    periodFailure = ex;
                }

                Specification.Assert(periodFailure is ObjectDisposedException,
                    "Changing Period after disposal did not produce ObjectDisposedException.");
                Specification.Assert(timer.Period == TimeSpan.FromSeconds(4),
                    "A disposed PeriodicTimer did not publish its validated period before throwing.");
            }, this.GetConfiguration().WithTestingIterations(10));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "TimeProviderFidelity")]
        public void TestCustomTimeProviderPeriodicTimerHandlesSynchronousConstructionAndFailures()
        {
            this.Test(async () =>
            {
                var synchronousProvider = new RecordingTimeProvider { FireOnCreate = true };
                using (var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), synchronousProvider))
                {
                    Specification.Assert(await timer.WaitForNextTickAsync(),
                        "A periodic tick fired synchronously during CreateTimer was lost.");
                    timer.Dispose();
                }

                Specification.Assert(synchronousProvider.LastTimer.DisposeCount is 1,
                    "The synchronously published periodic timer was disposed {0} times instead of once.",
                    synchronousProvider.LastTimer.DisposeCount);

                var throwingProvider = new RecordingTimeProvider { ThrowOnCreate = true };
                Exception failure = null;
                try
                {
                    _ = new PeriodicTimer(TimeSpan.FromSeconds(1), throwingProvider);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                Specification.Assert(failure is InvalidOperationException,
                    "PeriodicTimer did not propagate a custom provider's CreateTimer exception.");
            });
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "TimeProviderFidelity")]
        public void TestAbandonedCustomTimeProviderPeriodicTimerIsCleanedUp()
        {
            this.Test(() =>
            {
                var provider = new RecordingTimeProvider();
                WeakReference timer = CreateAbandonedTimer(provider);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                Specification.Assert(!timer.IsAlive,
                    "An abandoned controlled PeriodicTimer remained strongly rooted.");
                Specification.Assert(provider.LastTimer.DisposeCount is 1,
                    "Finalizable state did not dispose an abandoned provider timer exactly once.");
            });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CreateAbandonedTimer(RecordingTimeProvider provider)
        {
            var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), provider);
            return new WeakReference(timer);
        }
    }
}
#endif

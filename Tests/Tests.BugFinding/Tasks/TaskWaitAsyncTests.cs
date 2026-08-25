// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET6_0_OR_GREATER
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Specifications;
using Microsoft.Coyote.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    public class TaskWaitAsyncTests : BaseBugFindingTest
    {
        public TaskWaitAsyncTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestAllWaitAsyncOverloadsReturnControlledTasks()
        {
            this.Test(async () =>
            {
                var source = new TaskCompletionSource<bool>();
                var genericSource = new TaskCompletionSource<int>();
                using var cancellation = new CancellationTokenSource();
                TimeSpan timeout = TimeSpan.FromMinutes(1);

                Task[] waits =
                {
                    ((Task)source.Task).WaitAsync(cancellation.Token),
                    ((Task)source.Task).WaitAsync(timeout),
                    ((Task)source.Task).WaitAsync(timeout, cancellation.Token),
                    genericSource.Task.WaitAsync(cancellation.Token),
                    genericSource.Task.WaitAsync(timeout),
                    genericSource.Task.WaitAsync(timeout, cancellation.Token)
                };

#if NET8_0_OR_GREATER
                var provider = new RecordingTimeProvider();
                waits = new[]
                {
                    waits[0],
                    waits[1],
                    waits[2],
                    waits[3],
                    waits[4],
                    waits[5],
                    ((Task)source.Task).WaitAsync(timeout, provider),
                    ((Task)source.Task).WaitAsync(timeout, provider, cancellation.Token),
                    genericSource.Task.WaitAsync(timeout, provider),
                    genericSource.Task.WaitAsync(timeout, provider, cancellation.Token)
                };
#endif

                foreach (Task wait in waits)
                {
                    Specification.Assert(!CoyoteRuntime.Current.IsTaskUncontrolled(wait),
                        "Task.WaitAsync returned an uncontrolled proxy task.");
                }

                source.SetResult(true);
                genericSource.SetResult(42);
                foreach (Task wait in waits)
                {
                    try
                    {
                        await wait;
                    }
                    catch (TimeoutException) when (this.SchedulingPolicy is SchedulingPolicy.Fuzzing)
                    {
                        // Fuzzing deliberately contracts long delays, including WaitAsync's timeout.
                        // Exact interleaving uses virtual time and cannot time out before this source
                        // completion because construction of the wait is atomic.
                    }
                }

#if NET8_0_OR_GREATER
                Specification.Assert(provider.CreateCount is 4,
                    "The custom provider did not receive every generic and non-generic timeout.");
                foreach (RecordingTimeProvider.RecordingTimer timer in provider.Timers)
                {
                    Specification.Assert(timer.DisposeCount is 1,
                        "A source winner did not dispose its losing provider timeout exactly once.");
                }
#endif
            }, configuration: this.GetConfiguration()
                .WithTestingIterations(10)
                .WithPartiallyControlledConcurrencyAllowed(false));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestWaitAsyncPreservesFrameworkFastPathsAndValidationOrder()
        {
            this.Test(() =>
            {
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                Task completed = Task.CompletedTask;
                Task<int> completedGeneric = Task.FromResult(42);
                var pending = new TaskCompletionSource<bool>();

                Specification.Assert(ReferenceEquals(completed, completed.WaitAsync(cancellation.Token)),
                    "A completed task did not win over a pre-canceled wait token.");
                Specification.Assert(ReferenceEquals(completedGeneric, completedGeneric.WaitAsync(cancellation.Token)),
                    "A completed generic task did not win over a pre-canceled wait token.");
                Specification.Assert(ReferenceEquals(pending.Task, pending.Task.WaitAsync(Timeout.InfiniteTimeSpan)),
                    "An infinite uncancellable generic wait did not return its source task.");

                AssertThrows<ArgumentOutOfRangeException>(
                    () => completed.WaitAsync(TimeSpan.FromMilliseconds(-2)), "timeout");

#if NET8_0_OR_GREATER
                Exception failure = null;
                try
                {
                    _ = completed.WaitAsync(TimeSpan.FromMilliseconds(-2), null);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                Specification.Assert(failure is ArgumentNullException argument && argument.ParamName == "timeProvider",
                    "The TimeProvider overload did not validate a null provider before the timeout.");

                var provider = new RecordingTimeProvider();
                Specification.Assert(ReferenceEquals(completed,
                    completed.WaitAsync(TimeSpan.FromSeconds(1), provider)),
                    "A completed custom-provider wait did not return its source task.");
                Specification.Assert(ReferenceEquals(pending.Task,
                    pending.Task.WaitAsync(Timeout.InfiniteTimeSpan, provider)),
                    "An infinite custom-provider wait did not return its source task.");
                Task preCanceled = pending.Task.WaitAsync(
                    TimeSpan.FromSeconds(1), provider, cancellation.Token);
                Specification.Assert(preCanceled.IsCanceled,
                    "A pre-canceled custom-provider wait did not use its synchronous fast path.");
                Specification.Assert(provider.CreateCount is 0,
                    "WaitAsync consulted a custom provider on a completed, infinite or pre-canceled fast path.");
#endif
            });
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "VirtualTimeRemediation")]
        public void TestWaitAsyncTimeoutAndCancellationAreControlled()
        {
            this.Test(async () =>
            {
                var pending = new TaskCompletionSource<bool>();
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();

                OperationCanceledException cancellationFailure = null;
                try
                {
                    await pending.Task.WaitAsync(TimeSpan.Zero, cancellation.Token);
                }
                catch (OperationCanceledException ex)
                {
                    cancellationFailure = ex;
                }

                Specification.Assert(
                    cancellationFailure != null && cancellationFailure.CancellationToken == cancellation.Token,
                    "Pre-cancellation did not win over a zero timeout or lost its token.");

                using var pendingCancellation = new CancellationTokenSource();
                Task pendingCancellationWait = pending.Task.WaitAsync(pendingCancellation.Token);
                pendingCancellation.Cancel();
                cancellationFailure = null;
                try
                {
                    await pendingCancellationWait;
                }
                catch (OperationCanceledException ex)
                {
                    cancellationFailure = ex;
                }

                Specification.Assert(
                    cancellationFailure != null && cancellationFailure.CancellationToken == pendingCancellation.Token,
                    "Cancellation requested after WaitAsync started was ignored or lost its token.");

                Exception zeroTimeoutFailure = null;
                try
                {
                    await pending.Task.WaitAsync(TimeSpan.Zero);
                }
                catch (Exception ex)
                {
                    zeroTimeoutFailure = ex;
                }

                Specification.Assert(zeroTimeoutFailure is TimeoutException,
                    "A zero WaitAsync timeout did not produce TimeoutException.");

                Exception timeoutFailure = null;
                try
                {
                    await pending.Task.WaitAsync(TimeSpan.FromMinutes(1));
                }
                catch (Exception ex)
                {
                    timeoutFailure = ex;
                }

                Specification.Assert(timeoutFailure is TimeoutException,
                    "A finite virtual WaitAsync timeout did not produce TimeoutException.");
            }, configuration: this.GetConfiguration()
                .WithTestingIterations(10)
                .WithPartiallyControlledConcurrencyAllowed(false));
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestWaitAsyncExploresSourceCancellationAndTimeoutRaceWithoutLeakingWork()
        {
            this.Test(async () =>
            {
                using var cancellation = new CancellationTokenSource();
                var source = new TaskCompletionSource<int>();
                Task<int> wait = source.Task.WaitAsync(TimeSpan.FromMilliseconds(1), cancellation.Token);
                Task sourceCompletion = Task.Run(() => source.TrySetResult(42));
                Task cancellationCompletion = Task.Run(cancellation.Cancel);

                try
                {
                    int result = await wait;
                    Specification.Assert(result == 42, "WaitAsync returned an unexpected source result.");
                }
                catch (OperationCanceledException ex)
                {
                    Specification.Assert(ex.CancellationToken == cancellation.Token,
                        "The cancellation race lost the caller's token.");
                }
                catch (TimeoutException)
                {
                    // Virtual time is a participant in the same controlled race.
                }

                await Task.WhenAll(sourceCompletion, cancellationCompletion);
            }, configuration: this.GetConfiguration()
                .WithTestingIterations(100)
                .WithPartiallyControlledConcurrencyAllowed(false));
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestWaitAsyncMirrorsSourceOutcomeAndCleansUpLosingTimeout()
        {
            this.Test(async () =>
            {
                using var waitCancellation = new CancellationTokenSource();
                var successful = new TaskCompletionSource<int>();
                Task<int> successfulWait = successful.Task.WaitAsync(
                    Timeout.InfiniteTimeSpan, waitCancellation.Token);
                successful.SetResult(42);
                Specification.Assert(await successfulWait == 42,
                    "The generic WaitAsync proxy lost its source result.");

                var faulted = new TaskCompletionSource<bool>();
                Task faultedWait = faulted.Task.WaitAsync(
                    Timeout.InfiniteTimeSpan, waitCancellation.Token);
                faulted.SetException(new InvalidOperationException("expected"));
                Exception fault = null;
                try
                {
                    await faultedWait;
                }
                catch (Exception ex)
                {
                    fault = ex;
                }

                Specification.Assert(fault is InvalidOperationException,
                    "The WaitAsync proxy did not mirror the source exception.");

                using var sourceCancellation = new CancellationTokenSource();
                var canceled = new TaskCompletionSource<bool>();
                Task canceledWait = canceled.Task.WaitAsync(
                    Timeout.InfiniteTimeSpan, waitCancellation.Token);
                sourceCancellation.Cancel();
                canceled.SetCanceled(sourceCancellation.Token);
                OperationCanceledException canceledFailure = null;
                try
                {
                    await canceledWait;
                }
                catch (OperationCanceledException ex)
                {
                    canceledFailure = ex;
                }

                Specification.Assert(
                    canceledFailure != null && canceledFailure.CancellationToken == sourceCancellation.Token,
                    "The WaitAsync proxy did not preserve the source cancellation token.");
            }, configuration: this.GetConfiguration()
                .WithTestingIterations(20)
                .WithPartiallyControlledConcurrencyAllowed(false));
        }

        private static void AssertThrows<TException>(Action action, string parameterName)
            where TException : ArgumentException
        {
            Exception failure = null;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            Specification.Assert(
                failure is TException argument && argument.ParamName == parameterName,
                "Expected {0} for parameter '{1}', but received {2} for parameter '{3}'.",
                typeof(TException).Name,
                parameterName,
                failure?.GetType().Name ?? "no exception",
                (failure as ArgumentException)?.ParamName ?? "none");
        }

#if NET8_0_OR_GREATER
        [Fact(Timeout = 5000)]
        [Trait("Category", "TimeProviderFidelity")]
        public void TestCustomTimeProviderOwnsWaitAsyncTimeout()
        {
            this.Test(async () =>
            {
                var provider = new RecordingTimeProvider();
                var source = new TaskCompletionSource<bool>();
                Task wait = source.Task.WaitAsync(TimeSpan.FromMilliseconds(5), provider);

                Specification.Assert(provider.CreateCount is 1,
                    "The custom TimeProvider was not asked to create the WaitAsync timer.");
                Specification.Assert(!wait.IsCompleted,
                    "WaitAsync timed out before the custom provider fired.");

                provider.LastTimer.Fire();
                Exception failure = null;
                try
                {
                    await wait;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                Specification.Assert(failure is TimeoutException,
                    "The custom-provider timeout did not produce TimeoutException.");
            }, this.GetConfiguration().WithTestingIterations(10));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "TimeProviderFidelity")]
        public void TestSystemTimeProviderWaitAsyncRemainsVirtual()
        {
            if (this.SchedulingPolicy is not SchedulingPolicy.Interleaving)
            {
                return;
            }

            this.Test(async () =>
            {
                var source = new TaskCompletionSource<bool>();
                long clock = CoyoteRuntime.Current.GetVirtualTimeTicksForTesting();
                Exception failure = null;
                try
                {
                    await source.Task.WaitAsync(TimeSpan.FromMilliseconds(5), TimeProvider.System);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                Specification.Assert(failure is TimeoutException,
                    "TimeProvider.System did not produce a controlled WaitAsync timeout.");
                Specification.Assert(CoyoteRuntime.Current.GetVirtualTimeTicksForTesting() - clock ==
                    TimeSpan.FromMilliseconds(5).Ticks,
                    "TimeProvider.System WaitAsync no longer uses scheduler-owned virtual time.");
            }, this.GetConfiguration().WithTestingIterations(10));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "TimeProviderFidelity")]
        public void TestWhenAllWaitAsyncDoesNotUseVirtualTimeForCustomProvider()
        {
            this.Test(async () =>
            {
                var first = new TaskCompletionSource<bool>();
                var second = new TaskCompletionSource<bool>();
                var provider = new RecordingTimeProvider();
                long clock = CoyoteRuntime.Current.GetVirtualTimeTicksForTesting();
                Task wait = Task.WhenAll(first.Task, second.Task).WaitAsync(
                    TimeSpan.FromMilliseconds(5), provider);

                await Task.Delay(TimeSpan.FromMilliseconds(1));
                Specification.Assert(!wait.IsCompleted,
                    "Task.WhenAll(...).WaitAsync timed out from scheduler-owned virtual time.");
                if (this.SchedulingPolicy is SchedulingPolicy.Interleaving)
                {
                    Specification.Assert(CoyoteRuntime.Current.GetVirtualTimeTicksForTesting() > clock,
                        "The test did not advance scheduler-owned virtual time.");
                }

                provider.LastTimer.Fire();
                Exception failure = null;
                try
                {
                    await wait;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                Specification.Assert(failure is TimeoutException,
                    "The provider-fired Task.WhenAll timeout did not produce TimeoutException.");
                Specification.Assert(provider.LastTimer.DisposeCount is 1,
                    "The winning provider timeout was not cleaned up exactly once.");
            }, this.GetConfiguration().WithTestingIterations(10));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "TimeProviderFidelity")]
        public void TestCustomTimeProviderWaitAsyncCancellationWinner()
        {
            this.Test(async () =>
            {
                var source = new TaskCompletionSource<bool>();
                var provider = new RecordingTimeProvider();
                using var cancellation = new CancellationTokenSource();
                Task wait = ((Task)source.Task).WaitAsync(
                    TimeSpan.FromSeconds(1), provider, cancellation.Token);

                cancellation.Cancel();
                OperationCanceledException failure = null;
                try
                {
                    await wait;
                }
                catch (OperationCanceledException ex)
                {
                    failure = ex;
                }

                Specification.Assert(failure != null && failure.CancellationToken == cancellation.Token,
                    "WaitAsync did not preserve the caller's cancellation token.");
                Specification.Assert(provider.LastTimer.DisposeCount is 1,
                    "Cancellation did not dispose the losing provider timeout exactly once.");
            }, this.GetConfiguration().WithTestingIterations(10));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "TimeProviderFidelity")]
        public void TestCustomTimeProviderWaitAsyncHandlesSynchronousPublicationAndFailures()
        {
            this.Test(async () =>
            {
                var pending = new TaskCompletionSource<bool>();
                var synchronousProvider = new RecordingTimeProvider { FireOnCreate = true };
                Task synchronousWait = pending.Task.WaitAsync(
                    TimeSpan.FromMilliseconds(5), synchronousProvider);
                Exception timeout = null;
                try
                {
                    await synchronousWait;
                }
                catch (Exception ex)
                {
                    timeout = ex;
                }

                Specification.Assert(timeout is TimeoutException,
                    "A provider timeout fired synchronously during CreateTimer was lost.");
                Specification.Assert(synchronousProvider.LastTimer.DisposeCount is 1,
                    "The synchronously fired WaitAsync timer was not disposed exactly once.");

                var throwingProvider = new RecordingTimeProvider { ThrowOnCreate = true };
                Exception creation = null;
                try
                {
                    _ = pending.Task.WaitAsync(TimeSpan.FromMilliseconds(5), throwingProvider);
                }
                catch (Exception ex)
                {
                    creation = ex;
                }

                Specification.Assert(creation is InvalidOperationException,
                    "WaitAsync did not propagate a custom provider's CreateTimer exception.");
            });
        }
#endif
    }
}
#endif

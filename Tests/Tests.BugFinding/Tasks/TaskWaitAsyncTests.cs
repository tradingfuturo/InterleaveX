// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET6_0_OR_GREATER
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Specifications;
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
                var provider = new ThrowingTimeProvider();
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
        private sealed class ThrowingTimeProvider : TimeProvider
        {
            public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period) =>
                throw new InvalidOperationException("The controlled WaitAsync model invoked TimeProvider.CreateTimer.");
        }
#endif
    }
}
#endif

// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// Red-first coverage for the public Task.ContinueWith API surface used by current applications.
    /// These tests deliberately use System.Threading.Tasks.Task and TaskScheduler.Default so the
    /// rewriter must substitute a controlled implementation; no partial-control escape is allowed.
    /// </summary>
    public class CurrentUseContinuationTests : BaseBugFindingTest
    {
        public CurrentUseContinuationTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestPendingTaskActionContinuationWithDefaultScheduler()
        {
            this.Test(async () =>
            {
                var source = new TaskCompletionSource<bool>();
                bool invoked = false;
                Task continuation = source.Task.ContinueWith(antecedent =>
                {
                    Specification.Assert(antecedent.IsCompletedSuccessfully,
                        "The action continuation ran before its pending antecedent completed successfully.");
                    invoked = true;
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

                await Task.Run(() => source.SetResult(true));
                await continuation;
                Specification.Assert(invoked, "The pending action continuation did not run.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestCompletedTaskResultContinuationWithDefaultScheduler()
        {
            this.Test(async () =>
            {
                Task<int> continuation = Task.CompletedTask.ContinueWith(antecedent =>
                {
                    Specification.Assert(antecedent.IsCompletedSuccessfully,
                        "The result continuation did not observe its completed antecedent.");
                    return 42;
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

                int result = await continuation;
                Specification.Assert(result == 42, "The completed result continuation returned {0} instead of 42.", result);
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestFaultedTaskOnlyOnFaultedSynchronousContinuation()
        {
            this.Test(async () =>
            {
                var source = new TaskCompletionSource<bool>();
                bool observedFault = false;
                Task continuation = source.Task.ContinueWith(antecedent =>
                {
                    observedFault = antecedent.IsFaulted && antecedent.Exception?.InnerException is InvalidOperationException;
                }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                await Task.Run(() => source.SetException(new InvalidOperationException("expected")));
                await continuation;
                Specification.Assert(observedFault,
                    "OnlyOnFaulted ExecuteSynchronously did not observe the antecedent fault.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestSuccessfulTaskFilteredByOnlyOnFaultedCancelsContinuation()
        {
            this.Test(async () =>
            {
                bool invoked = false;
                Task continuation = Task.CompletedTask.ContinueWith(_ => invoked = true, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

                OperationCanceledException failure = await CaptureCancellationAsync(continuation);
                Specification.Assert(failure != null,
                    "A successful antecedent filtered by OnlyOnFaulted did not cancel its continuation.");
                Specification.Assert(!invoked,
                    "A continuation filtered by OnlyOnFaulted ran for a successful antecedent.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestCompletedTaskSchedulerOnlyAndSynchronousContinuations()
        {
            this.Test(async () =>
            {
                bool inlineActionRan = false;
                Task inlineAction = Task.CompletedTask.ContinueWith(_ => inlineActionRan = true,
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                Specification.Assert(inlineActionRan && inlineAction.IsCompletedSuccessfully,
                    "ExecuteSynchronously did not complete a continuation of an already-completed task inline.");

                bool schedulerOnlyActionRan = false;
                Task schedulerOnlyAction = Task.CompletedTask.ContinueWith(_ => schedulerOnlyActionRan = true,
                    TaskScheduler.Default);
                Task<int> schedulerOnlyResult = Task.CompletedTask.ContinueWith(_ => 11, TaskScheduler.Default);

                await Task.WhenAll(schedulerOnlyAction, schedulerOnlyResult);
                Specification.Assert(schedulerOnlyActionRan,
                    "The scheduler-only action continuation did not run.");
                Specification.Assert(await schedulerOnlyResult == 11,
                    "The scheduler-only result continuation returned the wrong value.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestPreCanceledContinuationDoesNotInvokeDelegate()
        {
            this.Test(async () =>
            {
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                bool invoked = false;
                Task continuation = Task.CompletedTask.ContinueWith(_ => invoked = true, cancellation.Token,
                    TaskContinuationOptions.None, TaskScheduler.Default);

                OperationCanceledException failure = await CaptureCancellationAsync(continuation);
                Specification.Assert(failure != null && failure.CancellationToken == cancellation.Token,
                    "A pre-canceled continuation did not preserve its cancellation token.");
                Specification.Assert(!invoked, "A pre-canceled continuation invoked its delegate.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestPendingContinuationCancellationDoesNotInvokeDelegate()
        {
            this.Test(async () =>
            {
                var source = new TaskCompletionSource<bool>();
                using var cancellation = new CancellationTokenSource();
                bool invoked = false;
                Task continuation = source.Task.ContinueWith(_ => invoked = true, cancellation.Token,
                    TaskContinuationOptions.None, TaskScheduler.Default);

                await Task.Run(cancellation.Cancel);
                OperationCanceledException failure = await CaptureCancellationAsync(continuation);
                Specification.Assert(failure != null && failure.CancellationToken == cancellation.Token,
                    "A pending continuation did not preserve its cancellation token.");
                Specification.Assert(!invoked, "A canceled pending continuation invoked its delegate.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestContinuationExceptionAndNestedUnwrapWithDefaultScheduler()
        {
            this.Test(async () =>
            {
                Task exceptional = Task.CompletedTask.ContinueWith(_ => throw new InvalidOperationException("expected"),
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                Exception exception = await CaptureExceptionAsync(exceptional);
                Specification.Assert(exception is InvalidOperationException,
                    "A continuation exception was not propagated as InvalidOperationException.");

                var source = new TaskCompletionSource<bool>();
                Task<Task<int>> nested = source.Task.ContinueWith(_ => Task.FromResult(99), CancellationToken.None,
                    TaskContinuationOptions.None, TaskScheduler.Default);
                Task<int> unwrapped = nested.Unwrap();
                await Task.Run(() => source.SetResult(true));
                Specification.Assert(await unwrapped == 99, "Unwrap lost the nested continuation result.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestOrderedContinuationTailsWithDefaultScheduler()
        {
            this.Test(async () =>
            {
                var source = new TaskCompletionSource<bool>();
                int tail = 0;
                Task first = source.Task.ContinueWith(_ => tail = 1, CancellationToken.None,
                    TaskContinuationOptions.None, TaskScheduler.Default);
                Task second = first.ContinueWith(_ =>
                {
                    Specification.Assert(tail == 1, "The second continuation ran before the first tail completed.");
                    tail = 2;
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                Task third = second.ContinueWith(_ =>
                {
                    Specification.Assert(tail == 2, "The third continuation ran before the second tail completed.");
                    tail = 3;
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

                await Task.Run(() => source.SetResult(true));
                await third;
                Specification.Assert(tail == 3, "The ordered continuation tail did not complete.");
            }, this.GetStrictConfiguration());
        }

        private Configuration GetStrictConfiguration() => this.GetConfiguration()
            .WithTestingIterations(100)
            .WithPartiallyControlledConcurrencyAllowed(false)
            .WithPartiallyControlledDataNondeterminismAllowed(false)
            .WithSystematicFuzzingFallbackEnabled(false);

        private static async Task<OperationCanceledException> CaptureCancellationAsync(Task task)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException ex)
            {
                return ex;
            }

            return null;
        }

        private static async Task<Exception> CaptureExceptionAsync(Task task)
        {
            try
            {
                await task;
            }
            catch (Exception ex)
            {
                return ex;
            }

            return null;
        }
    }
}

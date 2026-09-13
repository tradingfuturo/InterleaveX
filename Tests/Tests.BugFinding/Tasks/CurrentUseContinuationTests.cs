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

#pragma warning disable CA2008 // Do not create tasks without passing a TaskScheduler: the overloads without one are under test.
        [Fact(Timeout = 10000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestTaskStateActionContinuationOverloads()
        {
            this.Test(async () =>
            {
                var source = new TaskCompletionSource<bool>();
                Task antecedent = source.Task;
                var observed = new int[5];
                Action<Task, object> record = (completed, state) =>
                {
                    Specification.Assert(completed.IsCompletedSuccessfully,
                        "A state action continuation ran before its antecedent completed.");
                    observed[(int)state] = (int)state + 1;
                };

                Task[] continuations =
                {
                    antecedent.ContinueWith(record, 0),
                    antecedent.ContinueWith(record, 1, CancellationToken.None),
                    antecedent.ContinueWith(record, 2, TaskContinuationOptions.None),
                    antecedent.ContinueWith(record, 3, TaskScheduler.Default),
                    antecedent.ContinueWith(record, 4, CancellationToken.None, TaskContinuationOptions.None,
                        TaskScheduler.Default),
                };

                await Task.Run(() => source.SetResult(true));
                await Task.WhenAll(continuations);
                AssertEveryOverloadRan(observed);
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestTaskStateFunctionContinuationOverloads()
        {
            this.Test(async () =>
            {
                var source = new TaskCompletionSource<bool>();
                Task antecedent = source.Task;
                Func<Task, object, int> project = (completed, state) =>
                    completed.IsCompletedSuccessfully ? (int)state + 1 : -1;

                Task<int>[] continuations =
                {
                    antecedent.ContinueWith(project, 0),
                    antecedent.ContinueWith(project, 1, CancellationToken.None),
                    antecedent.ContinueWith(project, 2, TaskContinuationOptions.None),
                    antecedent.ContinueWith(project, 3, TaskScheduler.Default),
                    antecedent.ContinueWith(project, 4, CancellationToken.None, TaskContinuationOptions.None,
                        TaskScheduler.Default),
                };

                await Task.Run(() => source.SetResult(true));
                AssertEveryOverloadRan(await Task.WhenAll(continuations));
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestGenericTaskStateActionContinuationOverloads()
        {
            this.Test(async () =>
            {
                var source = new TaskCompletionSource<int>();
                var observed = new int[5];
                Action<Task<int>, object> record = (completed, state) =>
                {
                    observed[(int)state] = completed.Result is 7 ? (int)state + 1 : -1;
                };

                Task[] continuations =
                {
                    source.Task.ContinueWith(record, 0),
                    source.Task.ContinueWith(record, 1, CancellationToken.None),
                    source.Task.ContinueWith(record, 2, TaskContinuationOptions.None),
                    source.Task.ContinueWith(record, 3, TaskScheduler.Default),
                    source.Task.ContinueWith(record, 4, CancellationToken.None, TaskContinuationOptions.None,
                        TaskScheduler.Default),
                };

                await Task.Run(() => source.SetResult(7));
                await Task.WhenAll(continuations);
                AssertEveryOverloadRan(observed);
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestGenericTaskStateFunctionContinuationOverloads()
        {
            this.Test(async () =>
            {
                var source = new TaskCompletionSource<int>();
                Func<Task<int>, object, int> project = (completed, state) =>
                    completed.Result is 7 ? (int)state + 1 : -1;

                Task<int>[] continuations =
                {
                    source.Task.ContinueWith(project, 0),
                    source.Task.ContinueWith(project, 1, CancellationToken.None),
                    source.Task.ContinueWith(project, 2, TaskContinuationOptions.None),
                    source.Task.ContinueWith(project, 3, TaskScheduler.Default),
                    source.Task.ContinueWith(project, 4, CancellationToken.None, TaskContinuationOptions.None,
                        TaskScheduler.Default),
                };

                await Task.Run(() => source.SetResult(7));
                AssertEveryOverloadRan(await Task.WhenAll(continuations));
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestCurrentSchedulerContinuationOverloads()
        {
            this.Test(async () =>
            {
                var source = new TaskCompletionSource<int>();
                Task antecedent = source.Task;
                var observed = new int[12];
                Task[] continuations =
                {
                    antecedent.ContinueWith(_ => { observed[0] = 1; }),
                    antecedent.ContinueWith(_ => { observed[1] = 2; }, CancellationToken.None),
                    antecedent.ContinueWith(_ => { observed[2] = 3; }, TaskContinuationOptions.None),
                    antecedent.ContinueWith(_ => observed[3] = 4),
                    antecedent.ContinueWith(_ => observed[4] = 5, CancellationToken.None),
                    antecedent.ContinueWith(_ => observed[5] = 6, TaskContinuationOptions.None),
                    source.Task.ContinueWith(completed => { observed[6] = completed.Result is 7 ? 7 : -1; }),
                    source.Task.ContinueWith(completed => { observed[7] = completed.Result is 7 ? 8 : -1; },
                        CancellationToken.None),
                    source.Task.ContinueWith(completed => { observed[8] = completed.Result is 7 ? 9 : -1; },
                        TaskContinuationOptions.None),
                    source.Task.ContinueWith(completed => observed[9] = completed.Result is 7 ? 10 : -1),
                    source.Task.ContinueWith(completed => observed[10] = completed.Result is 7 ? 11 : -1,
                        CancellationToken.None),
                    source.Task.ContinueWith(completed => observed[11] = completed.Result is 7 ? 12 : -1,
                        TaskContinuationOptions.None),
                };

                await Task.Run(() => source.SetResult(7));
                await Task.WhenAll(continuations);
                AssertEveryOverloadRan(observed);
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestStateContinuationFilteredByOnlyOnFaultedIsCanceled()
        {
            this.Test(async () =>
            {
                bool invoked = false;
                Task continuation = Task.CompletedTask.ContinueWith((_, _) => { invoked = true; }, null,
                    TaskContinuationOptions.OnlyOnFaulted);

                OperationCanceledException failure = await CaptureCancellationAsync(continuation);
                Specification.Assert(failure != null,
                    "A successful antecedent filtered by OnlyOnFaulted did not cancel its state continuation.");
                Specification.Assert(!invoked, "A state continuation filtered by OnlyOnFaulted ran.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestPreCanceledStateContinuationPreservesItsToken()
        {
            this.Test(async () =>
            {
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                bool invoked = false;
                Task<int> continuation = Task.CompletedTask.ContinueWith((_, state) =>
                {
                    invoked = true;
                    return (int)state;
                }, 1, cancellation.Token);

                OperationCanceledException failure = await CaptureCancellationAsync(continuation);
                Specification.Assert(failure != null && failure.CancellationToken == cancellation.Token,
                    "A pre-canceled state continuation did not preserve its cancellation token.");
                Specification.Assert(!invoked, "A pre-canceled state continuation invoked its delegate.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestStateContinuationIsInterleavedWithTheCompletingOperation()
        {
            // A witness that the continuation is a schedulable operation rather than work the framework runs out of
            // the scheduler's sight: some schedule must let it run before the completing operation continues.
            this.TestWithError(async () =>
            {
                var source = new TaskCompletionSource<bool>();
                int observed = 0;
                Task continuation = ((Task)source.Task).ContinueWith((_, state) => { observed = (int)state; }, 1,
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                source.SetResult(true);
                Specification.Assert(observed is 0,
                    "The state continuation ran before the completing operation continued.");
                await continuation;
            },
            errorChecker: (e) => Assert.StartsWith(
                "The state continuation ran before the completing operation continued.", e),
            configuration: this.GetStrictConfiguration());
        }

#pragma warning restore CA2008 // Do not create tasks without passing a TaskScheduler

        private static void AssertEveryOverloadRan(int[] observed)
        {
            for (int idx = 0; idx < observed.Length; idx++)
            {
                Specification.Assert(observed[idx] == idx + 1,
                    "Continuation overload {0} did not run with its own state and antecedent.", idx);
            }
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

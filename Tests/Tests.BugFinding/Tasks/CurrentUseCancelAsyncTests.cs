// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET8_0_OR_GREATER
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// Covers <see cref="CancellationTokenSource.CancelAsync"/>, whose callbacks the framework runs from work it queues
    /// to the real thread pool. The strict configuration refuses partial control, so each of these would be reported
    /// as an uncontrolled wait if the callbacks were not a controlled operation.
    /// </summary>
    public class CurrentUseCancelAsyncTests : BaseBugFindingTest
    {
        public CurrentUseCancelAsyncTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestCancelAsyncCancelsSynchronouslyAndRunsCallbacksOffTheCaller()
        {
            this.Test(async () =>
            {
                using var source = new CancellationTokenSource();
                int callerThread = Environment.CurrentManagedThreadId;
                bool isInsideCancelAsync = false;
                bool ranInline = false;
                bool ran = false;
                using CancellationTokenRegistration registration = source.Token.Register(() =>
                {
                    ranInline |= isInsideCancelAsync && Environment.CurrentManagedThreadId == callerThread;
                    ran = true;
                });

                isInsideCancelAsync = true;
                Task cancellation = source.CancelAsync();
                isInsideCancelAsync = false;
                Specification.Assert(source.IsCancellationRequested,
                    "CancelAsync returned before the source was canceled.");

                await cancellation;
                Specification.Assert(ran, "Awaiting CancelAsync completed before its callback ran.");
                Specification.Assert(!ranInline, "A CancelAsync callback ran inline on the caller.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestCancelAsyncWithoutCallbacksCompletesSynchronously()
        {
            this.Test(() =>
            {
                using var source = new CancellationTokenSource();
                Task first = source.CancelAsync();
                Specification.Assert(first.IsCompletedSuccessfully && source.IsCancellationRequested,
                    "CancelAsync with nothing registered did not cancel and complete synchronously.");

                Task second = source.CancelAsync();
                Specification.Assert(second.IsCompletedSuccessfully,
                    "CancelAsync on an already canceled source did not complete synchronously.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestCancelAsyncOnADisposedSourceReturnsAFaultedTask()
        {
            this.Test(() =>
            {
                var source = new CancellationTokenSource();
                source.Dispose();
                Task cancellation = source.CancelAsync();
                Specification.Assert(cancellation.IsFaulted &&
                    cancellation.Exception?.InnerException is ObjectDisposedException,
                    "CancelAsync on a disposed source did not return a task faulted with ObjectDisposedException.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestCancelAsyncAggregatesCallbackExceptions()
        {
            this.Test(async () =>
            {
                using var source = new CancellationTokenSource();
                using CancellationTokenRegistration first = source.Token.Register(
                    () => throw new InvalidOperationException("first"));
                using CancellationTokenRegistration second = source.Token.Register(
                    () => throw new InvalidOperationException("second"));

                Exception failure = null;
                try
                {
                    await source.CancelAsync();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                Specification.Assert(failure is AggregateException aggregate && aggregate.InnerExceptions.Count is 2,
                    "CancelAsync did not aggregate both callback exceptions, observed '{0}'.", failure);
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestCancelAsyncPropagatesToLinkedSources()
        {
            this.Test(async () =>
            {
                using var source = new CancellationTokenSource();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(source.Token);
                bool ran = false;
                using CancellationTokenRegistration registration = linked.Token.Register(() => ran = true);

                await source.CancelAsync();
                Specification.Assert(linked.IsCancellationRequested && ran,
                    "CancelAsync did not reach the callbacks of a linked source.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestCancelAsyncCallbackCanRunBeforeTheCallerContinues()
        {
            // A witness that the callbacks are a schedulable operation: some schedule must run them before the caller's
            // next statement, which neither inline execution nor unmodelled thread-pool work would ever let it explore.
            this.TestWithError(async () =>
            {
                using var source = new CancellationTokenSource();
                bool ran = false;
                using CancellationTokenRegistration registration = source.Token.Register(() => ran = true);
                Task cancellation = source.CancelAsync();
                Specification.Assert(!ran, "A CancelAsync callback ran before the caller continued.");
                await cancellation;
            },
            errorChecker: (e) => Assert.StartsWith("A CancelAsync callback ran before the caller continued.", e),
            configuration: this.GetStrictConfiguration());
        }

        private Configuration GetStrictConfiguration() => this.GetConfiguration()
            .WithTestingIterations(100)
            .WithPartiallyControlledConcurrencyAllowed(false)
            .WithPartiallyControlledDataNondeterminismAllowed(false)
            .WithSystematicFuzzingFallbackEnabled(false);
    }
}
#endif

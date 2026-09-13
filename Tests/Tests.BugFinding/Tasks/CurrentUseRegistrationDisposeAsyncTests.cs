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
    /// Covers <see cref="CancellationTokenRegistration.DisposeAsync"/>, which the framework completes by polling from the
    /// real thread pool when the registration's callback is already running on another thread.
    /// </summary>
    public class CurrentUseRegistrationDisposeAsyncTests : BaseBugFindingTest
    {
        public CurrentUseRegistrationDisposeAsyncTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestDisposeAsyncBeforeCancellationRemovesTheCallbackSynchronously()
        {
            this.Test(() =>
            {
                using var source = new CancellationTokenSource();
                bool ran = false;
                CancellationTokenRegistration registration = source.Token.Register(() => ran = true);
                ValueTask disposal = registration.DisposeAsync();
                Specification.Assert(disposal.IsCompletedSuccessfully,
                    "Disposing a registration that had not run did not complete synchronously.");

                source.Cancel();
                Specification.Assert(!ran, "A callback disposed before cancellation still ran.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestDisposeAsyncWaitsForAnInFlightCallback()
        {
            this.Test(async () =>
            {
                using var source = new CancellationTokenSource();
                var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                bool finished = false;
                CancellationTokenRegistration registration = source.Token.Register(() =>
                {
                    entered.SetResult(true);
                    release.Task.Wait();
                    finished = true;
                });

                Task canceller = Task.Run(source.Cancel);
                await entered.Task;
                ValueTask disposal = registration.DisposeAsync();
                Specification.Assert(!disposal.IsCompleted,
                    "DisposeAsync completed while the callback was still running.");

                release.SetResult(true);
                await disposal;
                Specification.Assert(finished, "DisposeAsync completed before the in-flight callback returned.");
                await canceller;
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestAwaitUsingWaitsForAnInFlightCallback()
        {
            this.Test(async () =>
            {
                using var source = new CancellationTokenSource();
                var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                bool finished = false;
                Task canceller;

                // 'await using' over the struct disposes it through a constrained interface call, which is a
                // different call shape from invoking DisposeAsync directly.
                await using (CancellationTokenRegistration registration = source.Token.Register(() =>
                {
                    entered.SetResult(true);
                    release.Task.Wait();
                    finished = true;
                }))
                {
                    canceller = Task.Run(source.Cancel);
                    await entered.Task;
                    _ = Task.Run(() => release.SetResult(true));
                }

                Specification.Assert(finished, "Leaving 'await using' completed before the in-flight callback returned.");
                await canceller;
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestBoxedAsyncDisposableRegistrationWaitsForAnInFlightCallback()
        {
            this.Test(async () =>
            {
                using var source = new CancellationTokenSource();
                var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                bool finished = false;
                IAsyncDisposable disposable = source.Token.Register(() =>
                {
                    entered.SetResult(true);
                    release.Task.Wait();
                    finished = true;
                });

                Task canceller = Task.Run(source.Cancel);
                await entered.Task;
                _ = Task.Run(() => release.SetResult(true));
                await disposable.DisposeAsync();
                Specification.Assert(finished,
                    "Disposing through IAsyncDisposable completed before the in-flight callback returned.");
                await canceller;
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestDisposeAsyncInsideItsOwnCallbackCompletesSynchronously()
        {
            this.Test(() =>
            {
                using var source = new CancellationTokenSource();
                CancellationTokenRegistration registration = default;
                bool completedInline = false;
                registration = source.Token.Register(() => completedInline = registration.DisposeAsync().IsCompleted);

                source.Cancel();
                Specification.Assert(completedInline,
                    "A registration disposed from inside its own callback waited for itself.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestDisposeAsyncAfterTheCallbackReturnedCompletesSynchronously()
        {
            this.Test(() =>
            {
                using var source = new CancellationTokenSource();
                CancellationTokenRegistration registration = source.Token.Register(() => { });
                source.Cancel();
                Specification.Assert(registration.DisposeAsync().IsCompletedSuccessfully,
                    "Disposing a registration whose callback already returned did not complete synchronously.");
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

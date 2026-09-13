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
    /// Covers <see cref="CancellationTokenSource.Dispose()"/> of a linked source, which CoreLib completes by spin-waiting
    /// for a parent's in-flight linking callback. The waiter is resumed by the very cancellation walk it waits for, so
    /// every test disposes the linked source from a continuation that walk scheduled.
    /// </summary>
    public class CurrentUseLinkedSourceDisposeTests : BaseBugFindingTest
    {
        public CurrentUseLinkedSourceDisposeTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestDisposingASingleParentLinkedSourceFromItsCanceledWaiter()
        {
            this.Test(async () =>
            {
                using var parent = new CancellationTokenSource();
                var linked = CancellationTokenSource.CreateLinkedTokenSource(parent.Token);
                Task worker = DisposeWhenCanceledAsync(linked, source => source.Dispose());
                parent.Cancel();
                await worker;
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestUsingATwoParentLinkedSourceFromItsCanceledWaiter()
        {
            this.Test(async () =>
            {
                using var first = new CancellationTokenSource();
                using var second = new CancellationTokenSource();
                Task worker = Task.Run(async () =>
                {
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(first.Token, second.Token))
                    {
                        await AwaitCancellationAsync(linked.Token);
                    }
                });

                second.Cancel();
                await worker;
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestDisposingAManyParentLinkedSourceThroughIDisposableFromItsCanceledWaiter()
        {
            this.Test(async () =>
            {
                using var first = new CancellationTokenSource();
                using var second = new CancellationTokenSource();
                using var third = new CancellationTokenSource();
                var linked = CancellationTokenSource.CreateLinkedTokenSource(first.Token, second.Token, third.Token);
                Task worker = DisposeWhenCanceledAsync(linked, source => ((IDisposable)source).Dispose());
                third.Cancel();
                await worker;
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestDisposedLinkedSourceNoLongerObservesItsParent()
        {
            this.Test(() =>
            {
                using var parent = new CancellationTokenSource();
                var linked = CancellationTokenSource.CreateLinkedTokenSource(parent.Token);
                CancellationToken token = linked.Token;
                linked.Dispose();
                parent.Cancel();
                Specification.Assert(!token.IsCancellationRequested, "A disposed linked source was still canceled by its parent.");
            }, this.GetStrictConfiguration());
        }

        private static Task DisposeWhenCanceledAsync(CancellationTokenSource linked, Action<CancellationTokenSource> dispose) =>
            Task.Run(async () =>
            {
                await AwaitCancellationAsync(linked.Token);
                dispose(linked);
            });

        private static async Task AwaitCancellationAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                // The cancellation is the wake this waiter exists for.
            }
        }

        private Configuration GetStrictConfiguration() => this.GetConfiguration()
            .WithTestingIterations(200)
            .WithPartiallyControlledConcurrencyAllowed(false)
            .WithPartiallyControlledDataNondeterminismAllowed(false)
            .WithSystematicFuzzingFallbackEnabled(false);
    }
}
#endif

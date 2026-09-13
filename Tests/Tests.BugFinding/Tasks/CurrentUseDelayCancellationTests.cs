// Copyright (c) 2026 pipflow.com. Licensed under the GNU General Public License v3.0 or later.
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    public class CurrentUseDelayCancellationTests : BaseBugFindingTest
    {
        public CurrentUseDelayCancellationTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 10000)]
        public void TestTimerCancellationCallbackAndWorkerCleanupCannotBlockEachOther()
        {
            this.Test(async () =>
            {
                using var cancellation = new CancellationTokenSource();
                Task delay = Task.Delay(TimeSpan.FromSeconds(30), cancellation.Token);
                Task observer = ObserveAsync(delay);
                cancellation.Cancel();
                await observer;
            }, this.GetConfiguration().WithTestingIterations(100)
                .WithPartiallyControlledConcurrencyAllowed(false)
                .WithPartiallyControlledDataNondeterminismAllowed(false)
                .WithSystematicFuzzingFallbackEnabled(false));
        }

        private static async Task ObserveAsync(Task delay)
        {
            try
            {
                await delay;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}

// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Specifications;
using Microsoft.Extensions.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests.SystematicFuzzing
{
    public class BackgroundServiceFuzzingStopRegressionTests : BaseBugFindingTest
    {
        public BackgroundServiceFuzzingStopRegressionTests(ITestOutputHelper output)
            : base(output)
        {
        }

        private protected override SchedulingPolicy SchedulingPolicy => SchedulingPolicy.Fuzzing;

        protected override Configuration GetConfiguration() =>
            base.GetConfiguration().WithSystematicFuzzingEnabled().WithTestingIterations(10);

        [Fact(Timeout = 5000)]
        public void TestFuzzingStopWaitsForExecuteTask()
        {
            this.Test(async () =>
            {
                var service = new GatedService();
                await service.StartAsync(CancellationToken.None);
                await service.Started.Task;
                Task stop = service.StopAsync(CancellationToken.None);
                Specification.Assert(!stop.IsCompleted, "StopAsync completed while ExecuteAsync was live.");
                service.Release.TrySetResult(true);
                await stop;
            }, this.GetConfiguration());
        }

        private sealed class GatedService : BackgroundService
        {
            internal readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>();
            internal readonly TaskCompletionSource<bool> Release = new TaskCompletionSource<bool>();

            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                this.Started.TrySetResult(true);
                await this.Release.Task;
            }
        }
    }
}
#endif

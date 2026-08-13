// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET10_0_OR_GREATER
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Specifications;
using Microsoft.Extensions.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    public class BackgroundServiceNet10RegressionTests : BaseBugFindingTest
    {
        public BackgroundServiceNet10RegressionTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestNet10ExecuteAsyncDoesNotRunInline()
        {
            this.Test(async () =>
            {
                var service = new RecordingService();
                Task start = service.StartAsync(CancellationToken.None);
                Specification.Assert(start.IsCompleted, "StartAsync did not complete immediately.");
                Specification.Assert(!service.Started, "ExecuteAsync ran inline.");
                await service.ExecuteTask;
                Specification.Assert(service.Started, "ExecuteAsync was not scheduled.");
            });
        }

        [Fact(Timeout = 5000)]
        public void TestNet10PreCancelledStartDoesNotInvokeExecuteAsync()
        {
            this.Test(async () =>
            {
                using var source = new CancellationTokenSource();
                source.Cancel();
                var service = new RecordingService();
                await service.StartAsync(source.Token);
                await Task.Yield();
                Specification.Assert(!service.Started, "ExecuteAsync ran for a pre-cancelled start.");
            });
        }

        private sealed class RecordingService : BackgroundService
        {
            internal bool Started;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                this.Started = true;
                return Task.CompletedTask;
            }
        }
    }
}
#endif

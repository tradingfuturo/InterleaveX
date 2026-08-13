// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Specifications;
using Microsoft.Extensions.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    public class BackgroundServiceAsyncOverrideRegressionTests : BaseBugFindingTest
    {
        public BackgroundServiceAsyncOverrideRegressionTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestAsyncStartAndStopOverridesDispatchOnlyOnce()
        {
            this.Test(async () =>
            {
                var service = new AsyncOverrideService();
                BackgroundService asBase = service;
                await asBase.StartAsync(CancellationToken.None);
                await asBase.StopAsync(CancellationToken.None);
                Specification.Assert(service.StartCount is 1, "StartAsync override ran {0} times.", service.StartCount);
                Specification.Assert(service.StopCount is 1, "StopAsync override ran {0} times.", service.StopCount);
            }, this.GetConfiguration().WithTestingIterations(10));
        }

        private sealed class AsyncOverrideService : BackgroundService
        {
            internal int StartCount;
            internal int StopCount;

            public override async Task StartAsync(CancellationToken cancellationToken)
            {
                if (++this.StartCount > 2)
                {
                    throw new InvalidOperationException("StartAsync recursively redispatched.");
                }

                await Task.Yield();
                await base.StartAsync(cancellationToken);
            }

            public override async Task StopAsync(CancellationToken cancellationToken)
            {
                if (++this.StopCount > 2)
                {
                    throw new InvalidOperationException("StopAsync recursively redispatched.");
                }

                await Task.Yield();
                await base.StopAsync(cancellationToken);
            }

            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Yield();
                }
            }
        }
    }
}
#endif

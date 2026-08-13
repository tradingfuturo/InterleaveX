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
    public class BackgroundServiceNullTaskRegressionTests : BaseBugFindingTest
    {
        public BackgroundServiceNullTaskRegressionTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestNullExecuteTaskMatchesFrameworkVersion()
        {
            this.Test(async () =>
            {
                var service = new NullService();
#if NET10_0_OR_GREATER
                await service.StartAsync(CancellationToken.None);
                await Task.Yield();
                Specification.Assert(service.ExecuteTask != null && service.ExecuteTask.IsCanceled,
                    "The .NET 10 null execution task was not represented as canceled.");
#else
                Exception observed = null;
                try
                {
                    _ = service.StartAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    observed = ex;
                }

                Specification.Assert(observed is NullReferenceException,
                    "Expected NullReferenceException, observed {0}.", observed?.GetType().FullName ?? "none");
#endif
            });
        }

        private sealed class NullService : BackgroundService
        {
            protected override Task ExecuteAsync(CancellationToken stoppingToken) => null;
        }
    }
}
#endif

// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Specifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Xunit.Abstractions;
using ControlledHost = Microsoft.Coyote.Rewriting.Types.Hosting.Host;

namespace Microsoft.Coyote.BugFinding.Tests
{
    public class ConcurrentLifecycleCancellationRegressionTests : BaseBugFindingTest
    {
        public ConcurrentLifecycleCancellationRegressionTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestCompletedCanceledConcurrentCallbackMatchesFramework()
        {
            this.Test(async () =>
            {
                using IHost host = new HostBuilder().ConfigureServices(services =>
                {
                    services.Configure<HostOptions>(options =>
                    {
                        options.ServicesStartConcurrently = true;
                        options.StartupTimeout = Timeout.InfiniteTimeSpan;
                    });
                    services.AddSingleton<IHostedService, CanceledLifecycleService>();
                }).Build();

                Exception failure = null;
                try
                {
                    await ControlledHost.StartAsync(host, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

#if NET10_0_OR_GREATER
                Specification.Assert(failure is TaskCanceledException,
                    "Expected TaskCanceledException on .NET 10, received {0}.", failure?.GetType().Name ?? "none");
#else
                Specification.Assert(failure is null,
                    "A completed canceled callback should be ignored on this framework: {0}.",
                    failure?.GetType().Name);
#endif
            });
        }

        private sealed class CanceledLifecycleService : IHostedLifecycleService
        {
            public Task StartingAsync(CancellationToken cancellationToken) =>
                Task.FromCanceled(new CancellationToken(true));

            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
#endif

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
    public class BackgroundServiceObservationRegressionTests : BaseBugFindingTest
    {
        public BackgroundServiceObservationRegressionTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestSequentialStartObservesBackgroundFailureImmediately() =>
            this.RunObservationTest(false);

        [Fact(Timeout = 5000)]
        public void TestConcurrentStartObservesBackgroundFailureImmediately() =>
            this.RunObservationTest(true);

        private void RunObservationTest(bool concurrently)
        {
            this.Test(async () =>
            {
                var releaseFault = new TaskCompletionSource<bool>();
                using IHost host = new HostBuilder().ConfigureServices(services =>
                {
                    services.Configure<HostOptions>(options =>
                    {
                        options.ServicesStartConcurrently = concurrently;
                        options.StartupTimeout = Timeout.InfiniteTimeSpan;
                        options.ShutdownTimeout = Timeout.InfiniteTimeSpan;
                        options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
                    });
                    services.AddSingleton<IHostedService>(new FaultingBackgroundService(releaseFault.Task));
                    services.AddSingleton<IHostedService>(provider => new WaitForStoppingService(
                        releaseFault, provider.GetRequiredService<IHostApplicationLifetime>()));
                }).Build();

                await ControlledHost.StartAsync(host, CancellationToken.None);
                Specification.Assert(host.Services.GetRequiredService<IHostApplicationLifetime>()
                    .ApplicationStopping.IsCancellationRequested,
                    "The background failure was not observed during service startup.");
            });
        }

        private sealed class FaultingBackgroundService : BackgroundService
        {
            private readonly Task Release;

            internal FaultingBackgroundService(Task release) => this.Release = release;

            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                await this.Release;
                throw new InvalidOperationException("background failed");
            }
        }

        private sealed class WaitForStoppingService : IHostedService
        {
            private readonly TaskCompletionSource<bool> ReleaseFault;
            private readonly IHostApplicationLifetime Lifetime;

            internal WaitForStoppingService(
                TaskCompletionSource<bool> releaseFault, IHostApplicationLifetime lifetime)
            {
                this.ReleaseFault = releaseFault;
                this.Lifetime = lifetime;
            }

            public async Task StartAsync(CancellationToken cancellationToken)
            {
                this.ReleaseFault.TrySetResult(true);
                while (!this.Lifetime.ApplicationStopping.IsCancellationRequested)
                {
                    await Task.Yield();
                }
            }

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
#endif

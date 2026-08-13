// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Specifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    public class GenericHostBoundaryRegressionTests : BaseBugFindingTest
    {
        public GenericHostBoundaryRegressionTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestRealGenericHostUsesControlledBackgroundServiceLifecycle()
        {
            this.Test(async () =>
            {
                var events = new List<string>();
                using IHost host = new HostBuilder().ConfigureServices(services =>
                {
                    services.AddSingleton(events);
                    services.Configure<HostOptions>(options =>
                    {
                        options.StartupTimeout = Timeout.InfiniteTimeSpan;
                        options.ShutdownTimeout = Timeout.InfiniteTimeSpan;
                    });
                    services.AddSingleton<OrderedService>();
                    services.AddHostedService(provider => provider.GetRequiredService<OrderedService>());
                }).Build();

                await host.StartAsync(CancellationToken.None);
                await host.StopAsync(CancellationToken.None);

                OrderedService service = host.Services.GetRequiredService<OrderedService>();
                Specification.Assert(service.Ticks > 0, "The hosted loop never ran.");
                Specification.Assert(service.HasExited, "The hosted loop survived shutdown.");
                Specification.Assert(events.Count is 2 && events[0] is "start" && events[1] is "stop",
                    "Unexpected host lifecycle: {0}.", string.Join(",", events));
            }, this.GetConfiguration().WithTestingIterations(20));
        }

        [Fact(Timeout = 5000)]
        public void TestIHostedServiceAndDisposableInterfacesUseTheModel()
        {
            this.Test(async () =>
            {
                var service = new OrderedService(new List<string>());
                await service.StartAsync(CancellationToken.None);
                await Task.Yield();
                ((IDisposable)service).Dispose();
                await Task.Yield();
                Specification.Assert(service.StoppingToken.IsCancellationRequested,
                    "Interface disposal did not cancel the model token.");
            });
        }

        private sealed class OrderedService : BackgroundService
        {
            private readonly List<string> Events;

            public OrderedService(List<string> events) => this.Events = events;

            internal int Ticks;
            internal bool HasExited;
            internal CancellationToken StoppingToken;

            public override Task StartAsync(CancellationToken cancellationToken)
            {
                this.Events.Add("start");
                return base.StartAsync(cancellationToken);
            }

            public override Task StopAsync(CancellationToken cancellationToken)
            {
                this.Events.Add("stop");
                return base.StopAsync(cancellationToken);
            }

            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                this.StoppingToken = stoppingToken;
                while (!stoppingToken.IsCancellationRequested)
                {
                    this.Ticks++;
                    await Task.Yield();
                }

                this.HasExited = true;
            }
        }
    }
}
#endif

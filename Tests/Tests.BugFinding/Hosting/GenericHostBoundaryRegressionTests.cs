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
using ControlledHost = Microsoft.Coyote.Rewriting.Types.Hosting.Host;

namespace Microsoft.Coyote.BugFinding.Tests
{
    public class GenericHostBoundaryRegressionTests : BaseBugFindingTest
    {
        public GenericHostBoundaryRegressionTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestFrameworkGenericHostTypeIsRecognized()
        {
            using IHost host = new HostBuilder().Build();
            Assert.True(ControlledHost.IsFrameworkHost(host),
                "The target framework's built-in Generic Host type was not recognized.");
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

                await ControlledHost.StartAsync(host, CancellationToken.None);
                await ControlledHost.StopAsync(host, CancellationToken.None);

#if !NET10_0_OR_GREATER
                OrderedService service = host.Services.GetRequiredService<OrderedService>();
                Specification.Assert(service.Ticks > 0, "The hosted loop never ran.");
                Specification.Assert(events.Count is 2 && events[0] is "start" && events[1] is "stop",
                    "Unexpected host lifecycle: {0}.", string.Join(",", events));
#endif
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

        [Fact(Timeout = 5000)]
        public void TestCustomHostKeepsItsOwnLifecycleImplementation()
        {
            this.Test(async () =>
            {
                IHost host = new CustomHost();
                await host.StartAsync(CancellationToken.None);
                await host.StopAsync(CancellationToken.None);
                host.Dispose();

                var custom = (CustomHost)host;
                Specification.Assert(custom.StartCount is 1, "Custom host StartAsync was replaced.");
                Specification.Assert(custom.StopCount is 1, "Custom host StopAsync was replaced.");
                Specification.Assert(custom.DisposeCount is 1, "Custom host Dispose was replaced.");
            });
        }

        private sealed class OrderedService : BackgroundService
        {
            private readonly List<string> Events;

            public OrderedService(List<string> events) => this.Events = events;

            internal int Ticks;
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
            }
        }

        private sealed class CustomHost : IHost
        {
            internal int StartCount;
            internal int StopCount;
            internal int DisposeCount;

            public IServiceProvider Services => throw new InvalidOperationException(
                "The Generic Host model inspected a custom service provider.");

            public Task StartAsync(CancellationToken cancellationToken)
            {
                this.StartCount++;
                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken)
            {
                this.StopCount++;
                return Task.CompletedTask;
            }

            public void Dispose() => this.DisposeCount++;
        }
    }
}
#endif

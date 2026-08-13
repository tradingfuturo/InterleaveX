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
    public class PartialHostStartupRegressionTests : BaseBugFindingTest
    {
        public PartialHostStartupRegressionTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestStopRunsAfterPartialStartupFailure()
        {
            this.Test(async () =>
            {
                var events = new List<string>();
                using IHost host = new HostBuilder().ConfigureServices(services =>
                {
                    services.Configure<HostOptions>(options =>
                    {
                        options.StartupTimeout = Timeout.InfiniteTimeSpan;
                        options.ShutdownTimeout = Timeout.InfiniteTimeSpan;
                    });
                    services.AddSingleton<IHostedService>(new PartialStartService("first", events, false));
                    services.AddSingleton<IHostedService>(new PartialStartService("second", events, true));
                }).Build();

                bool startupFailed = false;
                try
                {
                    await ControlledHost.StartAsync(host, CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                    startupFailed = true;
                }

                await ControlledHost.StopAsync(host, CancellationToken.None);
                IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
                Specification.Assert(startupFailed, "The startup failure was not propagated.");
                Specification.Assert(lifetime.ApplicationStopping.IsCancellationRequested,
                    "StopAsync did not publish the stopping notification.");
                Specification.Assert(events.Contains("stop:first") && events.Contains("stop:second"),
                    "StopAsync did not traverse the resolved services after partial startup: {0}.",
                    string.Join(",", events));
            });
        }

        private sealed class PartialStartService : IHostedService
        {
            private readonly string Name;
            private readonly List<string> Events;
            private readonly bool FailOnStart;

            internal PartialStartService(string name, List<string> events, bool failOnStart)
            {
                this.Name = name;
                this.Events = events;
                this.FailOnStart = failOnStart;
            }

            public Task StartAsync(CancellationToken cancellationToken)
            {
                this.Events.Add($"start:{this.Name}");
                return this.FailOnStart ? Task.FromException(new InvalidOperationException("startup failed")) :
                    Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken)
            {
                this.Events.Add($"stop:{this.Name}");
                return Task.CompletedTask;
            }
        }
    }
}
#endif

// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Runtime;
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

        [Fact(Timeout = 5000)]
        public void TestCompletedFaultAndCancellationAggregationMatchesFramework()
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
                    services.AddSingleton<IHostedService, FaultedLifecycleService>();
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
                Specification.Assert(failure is AggregateException aggregate &&
                    aggregate.InnerExceptions.Any(ex => ex is InvalidOperationException) &&
                    aggregate.InnerExceptions.Any(ex => ex is TaskCanceledException),
                    "The .NET 10 aggregate did not contain both fault and cancellation.");
#else
                Specification.Assert(failure is InvalidOperationException,
                    "The completed cancellation was not ignored beside a fault: {0}.",
                    failure?.GetType().Name ?? "no exception");
#endif
            });
        }

#if NET8_0
        [Fact(Timeout = 5000)]
        public void TestPendingConcurrentCallbackIsWrappedWithStartupToken()
        {
            this.Test(async () =>
            {
                using var source = new CancellationTokenSource();
                var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                CoyoteRuntime.Current.RegisterKnownControlledTask(pending.Task);
                using IHost host = new HostBuilder().ConfigureServices(services =>
                {
                    services.Configure<HostOptions>(options =>
                    {
                        options.ServicesStartConcurrently = true;
                        options.StartupTimeout = Timeout.InfiniteTimeSpan;
                    });
                    services.AddSingleton<IHostedService>(
                        new CancelingPendingLifecycleService(source, pending.Task));
                }).Build();

                Exception failure = null;
                try
                {
                    await ControlledHost.StartAsync(host, source.Token);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                Specification.Assert(failure is TaskCanceledException,
                    "The .NET 8 concurrent callback wrapper produced {0}.",
                    failure?.GetType().Name ?? "no exception");
            });
        }
#endif

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

        private sealed class FaultedLifecycleService : IHostedLifecycleService
        {
            public Task StartingAsync(CancellationToken cancellationToken) =>
                Task.FromException(new InvalidOperationException("Expected lifecycle failure."));

            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }

#if NET8_0
        private sealed class CancelingPendingLifecycleService : IHostedLifecycleService
        {
            private readonly CancellationTokenSource Source;
            private readonly Task Pending;

            internal CancelingPendingLifecycleService(CancellationTokenSource source, Task pending)
            {
                this.Source = source;
                this.Pending = pending;
            }

            public Task StartingAsync(CancellationToken cancellationToken)
            {
                this.Source.Cancel();
                return this.Pending;
            }

            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
#endif
    }
}
#endif

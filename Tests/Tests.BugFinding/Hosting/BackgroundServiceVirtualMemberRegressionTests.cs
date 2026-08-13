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
    public class BackgroundServiceVirtualMemberRegressionTests : BaseBugFindingTest
    {
        public BackgroundServiceVirtualMemberRegressionTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestDisposeOverrideAndBaseRunOnce()
        {
            this.Test(async () =>
            {
                var service = new VirtualService();
                await service.StartAsync(CancellationToken.None);
                await Task.Yield();
                ((IDisposable)service).Dispose();
                await service.ModelTask;
                Specification.Assert(service.DisposeCount is 1, "Dispose override ran {0} times.", service.DisposeCount);
            });
        }

        [Fact(Timeout = 5000)]
        public void TestExecuteTaskOverrideIsNotSkippedThroughBaseType()
        {
            this.Test(() =>
            {
                var service = new VirtualService();
                BackgroundService asBase = service;
                Specification.Assert(ReferenceEquals(asBase.ExecuteTask, service.Marker),
                    "ExecuteTask override was skipped.");
            });
        }

        [Fact(Timeout = 5000)]
        public void TestHiddenLifecycleMembersAreNotTreatedAsOverrides()
        {
            this.Test(async () =>
            {
                var service = new HidingService();
                BackgroundService asBase = service;

                await asBase.StartAsync(CancellationToken.None);
                _ = asBase.ExecuteTask;
                await asBase.StopAsync(CancellationToken.None);
                asBase.Dispose();

                Specification.Assert(service.HiddenCallCount is 0,
                    "A hidden lifecycle member was invoked through a base-typed call.");
            });
        }

        private sealed class VirtualService : BackgroundService
        {
            internal readonly Task Marker = Task.FromResult(true);
            internal int DisposeCount;
            internal Task ModelTask;

            public override Task ExecuteTask => this.Marker;

            public override void Dispose()
            {
                if (++this.DisposeCount > 2)
                {
                    throw new InvalidOperationException("Dispose recursively redispatched.");
                }

                base.Dispose();
            }

            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                this.ModelTask = Task.CompletedTask;
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Yield();
                }

                this.ModelTask = Task.CompletedTask;
            }
        }

        private sealed class HidingService : BackgroundService
        {
            internal int HiddenCallCount;

            public new Task StartAsync(CancellationToken cancellationToken)
            {
                this.HiddenCallCount++;
                return Task.CompletedTask;
            }

            public new Task StopAsync(CancellationToken cancellationToken)
            {
                this.HiddenCallCount++;
                return Task.CompletedTask;
            }

            public new Task ExecuteTask
            {
                get
                {
                    this.HiddenCallCount++;
                    return Task.CompletedTask;
                }
            }

            public new void Dispose() => this.HiddenCallCount++;

            protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
        }
    }
}
#endif

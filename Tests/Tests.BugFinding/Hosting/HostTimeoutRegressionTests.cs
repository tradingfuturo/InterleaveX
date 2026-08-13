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
using ControlledExtensions = Microsoft.Coyote.Rewriting.Types.Hosting.HostingAbstractionsHostExtensions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    public class HostTimeoutRegressionTests : BaseBugFindingTest
    {
        public HostTimeoutRegressionTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestInvalidTimeoutIsRejectedBeforeWorkerLaunch()
        {
            this.Test(() =>
            {
                using IHost host = new TimeoutHost(waitForCancellation: false);
                Exception failure = null;
                try
                {
                    _ = ControlledExtensions.StopAsync(host, TimeSpan.FromMilliseconds(-2));
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                Specification.Assert(failure is ArgumentOutOfRangeException,
                    "Invalid timeout produced {0}.", failure?.GetType().Name ?? "no exception");
            });
        }

        [Fact(Timeout = 5000)]
        public void TestControlledTimeoutCancelsStopToken()
        {
            this.Test(async () =>
            {
                using var host = new TimeoutHost(waitForCancellation: true);
                await ControlledExtensions.StopAsync(host, TimeSpan.Zero);
                Specification.Assert(host.CancellationCount is 1,
                    "The owned timeout did not cancel the stop token exactly once.");
            });
        }

        [Fact(Timeout = 5000)]
        public void TestCompletedStopDoesNotReceiveLateTimeoutCancellation()
        {
            this.Test(async () =>
            {
                using var host = new TimeoutHost(waitForCancellation: false);
                await ControlledExtensions.StopAsync(host, TimeSpan.FromMilliseconds(10));
                await Task.Yield();
                await Task.Yield();
                Specification.Assert(host.CancellationCount is 0,
                    "A timeout worker canceled after StopAsync completed.");
            });
        }

        private sealed class TimeoutHost : IHost
        {
            private readonly bool WaitForCancellation;

            internal TimeoutHost(bool waitForCancellation) => this.WaitForCancellation = waitForCancellation;

            internal int CancellationCount;

            public IServiceProvider Services => null;

            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public async Task StopAsync(CancellationToken cancellationToken)
            {
                using CancellationTokenRegistration registration = cancellationToken.Register(
                    () => this.CancellationCount++);
                if (this.WaitForCancellation)
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        await Task.Yield();
                    }
                }
            }

            public void Dispose()
            {
            }
        }
    }
}
#endif

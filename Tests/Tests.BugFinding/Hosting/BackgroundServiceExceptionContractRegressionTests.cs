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
    public class BackgroundServiceExceptionContractRegressionTests : BaseBugFindingTest
    {
        public BackgroundServiceExceptionContractRegressionTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestExecuteExceptionIsNotReflectionWrapped()
        {
            this.Test(async () =>
            {
                Exception observed = null;
                try
                {
#if NET10_0_OR_GREATER
                    var service = new ThrowingExecuteService();
                    await service.StartAsync(CancellationToken.None);
                    await service.ExecuteTask;
#else
                    _ = new ThrowingExecuteService().StartAsync(CancellationToken.None);
#endif
                }
                catch (Exception ex)
                {
                    observed = ex;
                }

                Specification.Assert(observed is InvalidOperationException,
                    "Expected InvalidOperationException, observed {0}.", observed?.GetType().FullName ?? "none");
            });
        }

        [Fact(Timeout = 5000)]
        public void TestStopCancellationExceptionFaultsReturnedTask()
        {
            this.Test(async () =>
            {
                var service = new ThrowingCancellationService();
                await service.StartAsync(CancellationToken.None);
                Task stop = null;
                Exception synchronous = null;
                try
                {
                    stop = service.StopAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    synchronous = ex;
                }

                Specification.Assert(synchronous is null, "StopAsync threw synchronously: {0}.", synchronous?.GetType().Name);
                Exception asynchronous = null;
                try
                {
                    await stop;
                }
                catch (Exception ex)
                {
                    asynchronous = ex;
                }

                Specification.Assert(asynchronous is AggregateException,
                    "Expected an asynchronous AggregateException, observed {0}.", asynchronous?.GetType().FullName ?? "none");
            });
        }

        private sealed class ThrowingExecuteService : BackgroundService
        {
            protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
                throw new InvalidOperationException("execute");
        }

        private sealed class ThrowingCancellationService : BackgroundService
        {
            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                using CancellationTokenRegistration registration = stoppingToken.Register(() =>
                    throw new InvalidOperationException("cancel"));
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Yield();
                }
            }
        }
    }
}
#endif

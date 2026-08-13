// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET
#pragma warning disable CS1591 // Compiler-facing rewrite shims mirror framework signatures.
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Runtime;
using Microsoft.Extensions.Hosting;
using ControlledTask = Microsoft.Coyote.Rewriting.Types.Threading.Tasks.Task;

namespace Microsoft.Coyote.Rewriting.Types.Hosting
{
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class HostingAbstractionsHostExtensions
    {
        public static void Start(IHost host)
        {
            Task task = Host.StartAsync(host, default);
            ControlledTask.Wait(task);
            task.GetAwaiter().GetResult();
        }

        public static Task StopAsync(IHost host, TimeSpan timeout)
        {
            if (CoyoteRuntime.Current.SchedulingPolicy is SchedulingPolicy.None)
            {
                return Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.StopAsync(host, timeout);
            }

            TimeoutLease lease;
            var source = new CancellationTokenSource();
            try
            {
                lease = TimeoutLease.Start(source, timeout);
            }
            catch
            {
                source.Dispose();
                throw;
            }

            return ControlledTask.Run(async () =>
            {
                using (source)
                await using (lease)
                {
                    await Host.StopAsync(host, source.Token);
                }
            });
        }

        public static void WaitForShutdown(IHost host)
        {
            Task task = WaitForShutdownAsync(host, default);
            ControlledTask.Wait(task);
            task.GetAwaiter().GetResult();
        }

        public static void Run(IHost host)
        {
            Task task = RunAsync(host, default);
            ControlledTask.Wait(task);
            task.GetAwaiter().GetResult();
        }

        public static Task RunAsync(IHost host, CancellationToken token = default) => ControlledTask.Run(async () =>
        {
            try
            {
                await Host.StartAsync(host, token);
                await WaitForShutdownAsync(host, token);
            }
            finally
            {
                await Host.DisposeAsync(host);
            }
        });

        public static Task WaitForShutdownAsync(IHost host, CancellationToken token = default) => ControlledTask.Run(async () =>
        {
            IHostApplicationLifetime lifetime = host.Services.GetService(typeof(IHostApplicationLifetime)) as
                IHostApplicationLifetime ?? throw new InvalidOperationException("IHostApplicationLifetime is not registered.");
            var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CoyoteRuntime.Current.RegisterKnownControlledTask(source.Task);
            using CancellationTokenRegistration caller = token.Register(lifetime.StopApplication);
            using CancellationTokenRegistration stopping = lifetime.ApplicationStopping.Register(() => source.TrySetResult(true));
            if (lifetime.ApplicationStopping.IsCancellationRequested)
            {
                source.TrySetResult(true);
            }
            await source.Task;
            await Host.StopAsync(host, CancellationToken.None);
        });
    }
}
#pragma warning restore CS1591
#endif

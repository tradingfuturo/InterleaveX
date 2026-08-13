// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET
#pragma warning disable CS1591 // Compiler-facing rewrite shims mirror framework signatures.
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Runtime;
using Microsoft.Extensions.Hosting;
using SystemBackgroundService = Microsoft.Extensions.Hosting.BackgroundService;

namespace Microsoft.Coyote.Rewriting.Types.Hosting
{
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class HostedService
    {
        public static Task StartAsync<T>(ref T instance, CancellationToken cancellationToken)
            where T : IHostedService
        {
            if (typeof(T).IsValueType || CoyoteRuntime.Current.SchedulingPolicy is SchedulingPolicy.None)
            {
                return instance.StartAsync(cancellationToken);
            }

            return StartAsync((IHostedService)instance, cancellationToken);
        }

        public static Task StartAsync(IHostedService instance, CancellationToken cancellationToken)
        {
            if (CoyoteRuntime.Current.SchedulingPolicy is SchedulingPolicy.None)
            {
                return instance.StartAsync(cancellationToken);
            }

            return instance is SystemBackgroundService service ?
                Hosting.BackgroundService.StartAsync(service, cancellationToken) :
                instance.StartAsync(cancellationToken);
        }

        public static Task StopAsync<T>(ref T instance, CancellationToken cancellationToken)
            where T : IHostedService
        {
            if (typeof(T).IsValueType || CoyoteRuntime.Current.SchedulingPolicy is SchedulingPolicy.None)
            {
                return instance.StopAsync(cancellationToken);
            }

            return StopAsync((IHostedService)instance, cancellationToken);
        }

        public static Task StopAsync(IHostedService instance, CancellationToken cancellationToken)
        {
            if (CoyoteRuntime.Current.SchedulingPolicy is SchedulingPolicy.None)
            {
                return instance.StopAsync(cancellationToken);
            }

            return instance is SystemBackgroundService service ?
                Hosting.BackgroundService.StopAsync(service, cancellationToken) :
                instance.StopAsync(cancellationToken);
        }
    }
}
#pragma warning restore CS1591
#endif

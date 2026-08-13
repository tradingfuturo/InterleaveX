// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET
using System;
using System.Threading;
using System.Threading.Tasks;
using ControlledTask = Microsoft.Coyote.Rewriting.Types.Threading.Tasks.Task;

namespace Microsoft.Coyote.Rewriting.Types.Hosting
{
    /// <summary>Owns a controlled timeout worker until it has terminated.</summary>
    internal sealed class TimeoutLease : IAsyncDisposable
    {
        private const double MaxSupportedTimeoutMilliseconds = uint.MaxValue - 1d;

        private readonly CancellationTokenSource Source;
        private readonly Task Worker;
        private volatile bool IsActive;

        private TimeoutLease(CancellationTokenSource source, TimeSpan timeout)
        {
            Validate(timeout);
            this.Source = source;
            this.IsActive = timeout != Timeout.InfiniteTimeSpan;
            this.Worker = this.IsActive ? ControlledTask.Run(async () =>
            {
                await ControlledTask.Delay(timeout);
                if (this.IsActive)
                {
                    this.Source.Cancel();
                }
            }) : Task.CompletedTask;
        }

        internal static TimeoutLease Start(CancellationTokenSource source, TimeSpan timeout) =>
            new TimeoutLease(source, timeout);

        public async ValueTask DisposeAsync()
        {
            this.IsActive = false;
            await this.Worker;
        }

        private static void Validate(TimeSpan timeout)
        {
            double milliseconds = timeout.TotalMilliseconds;
            if (milliseconds < Timeout.Infinite || milliseconds > MaxSupportedTimeoutMilliseconds)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }
        }
    }
}
#endif

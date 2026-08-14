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
        private readonly CancellationTokenSource DelaySource;
        private readonly Task Worker;
        private volatile bool IsActive;

        private TimeoutLease(CancellationTokenSource source, TimeSpan timeout,
            Func<TimeSpan, CancellationToken, Task> delayAsync)
        {
            Validate(timeout);
            this.Source = source;
            this.IsActive = timeout != Timeout.InfiniteTimeSpan;
            this.DelaySource = this.IsActive ? new CancellationTokenSource() : null;
            this.Worker = this.IsActive ? ControlledTask.Run(async () =>
            {
                try
                {
                    await delayAsync(timeout, this.DelaySource.Token);
                    if (this.IsActive)
                    {
                        this.Source.Cancel();
                    }
                }
                catch (OperationCanceledException ex) when (
                    this.DelaySource.IsCancellationRequested && ex.CancellationToken == this.DelaySource.Token)
                {
                }
            }) : Task.CompletedTask;
        }

        internal static TimeoutLease Start(CancellationTokenSource source, TimeSpan timeout) =>
            new TimeoutLease(source, timeout, ControlledTask.Delay);

        internal static TimeoutLease Start(CancellationTokenSource source, TimeSpan timeout,
            Func<TimeSpan, CancellationToken, Task> delayAsync) =>
            new TimeoutLease(source, timeout, delayAsync);

        public async ValueTask DisposeAsync()
        {
            this.IsActive = false;
            this.DelaySource?.Cancel();
            try
            {
                await this.Worker;
            }
            finally
            {
                this.DelaySource?.Dispose();
            }
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

// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET8_0_OR_GREATER
using System;
using System.Threading;
using Microsoft.Coyote.Runtime;

namespace Microsoft.Coyote.Rewriting.Types.Threading
{
    /// <summary>
    /// Owns a timer created by a custom <see cref="TimeProvider"/> and routes its callbacks through
    /// the controlled runtime without losing synchronous provider semantics.
    /// </summary>
    internal sealed class ProviderTimer : IDisposable
    {
        private readonly object SyncObject = new object();

        private readonly CoyoteRuntime Runtime;

        private readonly TimerCallback Callback;

        private readonly object State;

        private ITimer Timer;

        private bool IsDisposed;

        private ProviderTimer(CoyoteRuntime runtime, TimerCallback callback, object state)
        {
            this.Runtime = runtime;
            this.Callback = callback;
            this.State = state;
        }

        internal static ProviderTimer Create(CoyoteRuntime runtime, TimeProvider timeProvider,
            TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period)
        {
            var owner = new ProviderTimer(runtime, callback, state);
            ITimer timer;
            using (ExecutionContext.SuppressFlow())
            {
                timer = timeProvider.CreateTimer(static value => ((ProviderTimer)value).OnTimer(),
                    owner, dueTime, period);
            }

            bool disposeTimer;
            lock (owner.SyncObject)
            {
                owner.Timer = timer;
                disposeTimer = owner.IsDisposed;
            }

            if (disposeTimer)
            {
                timer?.Dispose();
            }

            return owner;
        }

        internal bool Change(TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer;
            lock (this.SyncObject)
            {
                if (this.IsDisposed)
                {
                    return false;
                }

                timer = this.Timer;
            }

            return timer?.Change(dueTime, period) ?? false;
        }

        public void Dispose()
        {
            ITimer timer;
            lock (this.SyncObject)
            {
                if (this.IsDisposed)
                {
                    return;
                }

                this.IsDisposed = true;
                timer = this.Timer;
            }

            timer?.Dispose();
        }

        private void OnTimer()
        {
            lock (this.SyncObject)
            {
                if (this.IsDisposed)
                {
                    return;
                }
            }

            this.Runtime.DispatchProviderTimerCallback(() => this.Callback(this.State));
        }
    }
}
#endif

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
    internal sealed class ProviderTimer : ITimer
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

        public bool Change(TimeSpan dueTime, TimeSpan period)
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

        public System.Threading.Tasks.ValueTask DisposeAsync()
        {
            this.Dispose();
            return default;
        }

        private void OnTimer()
        {
            lock (this.SyncObject)
            {
                if (this.IsDisposed || this.Runtime.HasExecutionEnded)
                {
                    return;
                }
            }

            this.Runtime.DispatchProviderTimerCallback(this.InvokeCallback);
        }

        /// <summary>
        /// Invokes the provider callback only while its timer and runtime still belong to the active
        /// iteration. This second check covers a callback that was admitted just before disposal or
        /// runtime teardown.
        /// </summary>
        private void InvokeCallback()
        {
            TimerCallback callback;
            object state;
            lock (this.SyncObject)
            {
                if (this.IsDisposed || this.Runtime.HasExecutionEnded)
                {
                    return;
                }

                callback = this.Callback;
                state = this.State;
            }

            callback(state);
        }
    }
}
#endif

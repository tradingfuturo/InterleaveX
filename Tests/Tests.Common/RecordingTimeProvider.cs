// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Coyote.Tests.Common
{
    /// <summary>
    /// Deterministic time provider used to verify that modeled timer APIs preserve provider ownership.
    /// </summary>
    internal sealed class RecordingTimeProvider : TimeProvider
    {
        internal bool FireOnCreate { get; set; }

        internal bool ThrowOnCreate { get; set; }

        internal bool ChangeResult { get; set; } = true;

        internal int CreateCount { get; private set; }

        internal List<RecordingTimer> Timers { get; } = new List<RecordingTimer>();

        internal RecordingTimer LastTimer { get; private set; }

        internal bool WasExecutionContextFlowSuppressed { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period)
        {
            this.CreateCount++;
            this.WasExecutionContextFlowSuppressed = ExecutionContext.IsFlowSuppressed();
            if (this.ThrowOnCreate)
            {
                throw new InvalidOperationException("Expected provider timer creation failure.");
            }

            var timer = new RecordingTimer(this, callback, state, dueTime, period);
            this.Timers.Add(timer);
            this.LastTimer = timer;
            if (this.FireOnCreate)
            {
                timer.Fire();
            }

            return timer;
        }

        internal sealed class RecordingTimer : ITimer
        {
            private readonly object SyncObject = new object();

            private readonly RecordingTimeProvider Provider;

            private readonly TimerCallback Callback;

            private readonly object State;

            private bool IsDisposed;

            internal RecordingTimer(RecordingTimeProvider provider, TimerCallback callback, object state,
                TimeSpan dueTime, TimeSpan period)
            {
                this.Provider = provider;
                this.Callback = callback;
                this.State = state;
                this.DueTime = dueTime;
                this.Period = period;
            }

            internal TimeSpan DueTime { get; private set; }

            internal TimeSpan Period { get; private set; }

            internal int ChangeCount { get; private set; }

            internal int DisposeCount { get; private set; }

            internal int FireCount { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (this.SyncObject)
                {
                    this.ChangeCount++;
                    this.DueTime = dueTime;
                    this.Period = period;
                    return !this.IsDisposed && this.Provider.ChangeResult;
                }
            }

            public void Dispose()
            {
                lock (this.SyncObject)
                {
                    this.DisposeCount++;
                    this.IsDisposed = true;
                }
            }

            public ValueTask DisposeAsync()
            {
                this.Dispose();
                return default;
            }

            internal void Fire()
            {
                TimerCallback callback;
                object state;
                lock (this.SyncObject)
                {
                    if (this.IsDisposed || this.DueTime == Timeout.InfiniteTimeSpan)
                    {
                        return;
                    }

                    this.FireCount++;
                    if (this.Period == Timeout.InfiniteTimeSpan)
                    {
                        this.DueTime = Timeout.InfiniteTimeSpan;
                    }

                    callback = this.Callback;
                    state = this.State;
                }

                callback(state);
            }
        }
    }
}
#endif

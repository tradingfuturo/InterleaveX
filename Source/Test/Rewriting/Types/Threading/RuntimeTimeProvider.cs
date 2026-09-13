// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET8_0_OR_GREATER
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Runtime;

namespace Microsoft.Coyote.Rewriting.Types.Threading
{
    /// <summary>
    /// Creates the time-provider views used by the rewritten task-delay APIs.
    /// </summary>
    /// <remarks>
    /// The framework still creates and completes the delay promise. The proxy changes only timer
    /// ownership: custom provider callbacks are dispatched through the active runtime and system-time
    /// timers are scheduled against the runtime's virtual clock.
    /// </remarks>
    internal static class RuntimeTimeProvider
    {
        internal static Task Delay(CoyoteRuntime runtime, TimeSpan delay, TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            TimeProvider provider = timeProvider == TimeProvider.System ?
                new VirtualTimeProvider(runtime) : new DispatchingTimeProvider(runtime, timeProvider);
            Task task = Task.Delay(delay, provider, cancellationToken);
            runtime.RegisterKnownControlledTask(task);
            return task;
        }

        /// <summary>
        /// Delegates clock reads to a custom provider while dispatching its timer callbacks through Coyote.
        /// </summary>
        private sealed class DispatchingTimeProvider : TimeProvider
        {
            private readonly CoyoteRuntime Runtime;
            private readonly TimeProvider Provider;

            internal DispatchingTimeProvider(CoyoteRuntime runtime, TimeProvider provider)
            {
                this.Runtime = runtime;
                this.Provider = provider;
            }

            public override TimeZoneInfo LocalTimeZone => this.Provider.LocalTimeZone;

            public override long TimestampFrequency => this.Provider.TimestampFrequency;

            public override DateTimeOffset GetUtcNow() => this.Provider.GetUtcNow();

            public override long GetTimestamp() => this.Provider.GetTimestamp();

            public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime,
                TimeSpan period) => ProviderTimer.Create(
                    this.Runtime, this.Provider, callback, state, dueTime, period);
        }

        /// <summary>
        /// Uses scheduler-owned virtual delays as the timer source supplied to the BCL delay promise, and as the
        /// schedule of a modelled <see cref="System.Threading.Timer"/>.
        /// </summary>
        internal sealed class VirtualTimeProvider : TimeProvider
        {
            private readonly CoyoteRuntime Runtime;

            internal VirtualTimeProvider(CoyoteRuntime runtime)
            {
                this.Runtime = runtime;
            }

            public override TimeZoneInfo LocalTimeZone => TimeProvider.System.LocalTimeZone;

            public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;

            public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow();

            public override long GetTimestamp() => TimeProvider.System.GetTimestamp();

            public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime,
                TimeSpan period) => new VirtualTimer(this.Runtime, callback, state, dueTime, period);
        }

        /// <summary>
        /// Implements a BCL timer using a cancelable Coyote virtual delay. Disposal immediately cancels
        /// that delay, which retires its published deadline before any later clock advancement.
        /// </summary>
        private sealed class VirtualTimer : ITimer
        {
            private readonly object SyncObject = new object();
            private readonly CoyoteRuntime Runtime;
            private readonly TimerCallback Callback;
            private readonly object State;

            private ScheduledDelay CurrentDelay;
            private TimeSpan Period;
            private bool IsDisposed;

            internal VirtualTimer(CoyoteRuntime runtime, TimerCallback callback, object state, TimeSpan dueTime,
                TimeSpan period)
            {
                this.Runtime = runtime;
                this.Callback = callback;
                this.State = state;
                this.Period = period;
                this.Schedule(dueTime);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                ScheduledDelay previous;
                lock (this.SyncObject)
                {
                    if (this.IsDisposed)
                    {
                        return false;
                    }

                    this.Period = period;
                    previous = this.CurrentDelay;
                    this.CurrentDelay = null;
                }

                previous?.Cancel();
                this.Schedule(dueTime);
                return true;
            }

            public void Dispose()
            {
                ScheduledDelay delay;
                lock (this.SyncObject)
                {
                    if (this.IsDisposed)
                    {
                        return;
                    }

                    this.IsDisposed = true;
                    delay = this.CurrentDelay;
                    this.CurrentDelay = null;
                }

                delay?.Cancel();
            }

            public ValueTask DisposeAsync()
            {
                this.Dispose();
                return default;
            }

            private void Schedule(TimeSpan dueTime)
            {
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    return;
                }

                var delay = new ScheduledDelay(this);
                lock (this.SyncObject)
                {
                    if (this.IsDisposed)
                    {
                        delay.Cancel();
                        return;
                    }

                    this.CurrentDelay = delay;
                }

                if ((long)dueTime.TotalMilliseconds is 0 && this.Runtime.SchedulingPolicy is SchedulingPolicy.Interleaving)
                {
                    // A zero-delay schedule completes synchronously, and the synchronous continuation below would then
                    // run the callback inline on the thread arming the timer - inside a Timer constructor or Change
                    // call. A framework timer never does that: a due callback is always queued. So it is queued here
                    // too, as a new operation that the scheduler decides when to run.
                    delay.SetTask(Task.CompletedTask);
                    this.Runtime.Schedule(() => this.OnDelayCompleted(delay, Task.CompletedTask));
                    return;
                }

                Task task = this.Runtime.ScheduleDelay(dueTime, delay.Cancellation.Token);
                delay.SetTask(task);
                task.ContinueWith(static (completed, state) =>
                {
                    ((ScheduledDelay)state).Owner.OnDelayCompleted((ScheduledDelay)state, completed);
                },
                delay,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                this.Runtime.ControlledTaskScheduler);
            }

            private void OnDelayCompleted(ScheduledDelay delay, Task task)
            {
                // Timer machinery, not progress: a periodic callback must not re-open the clock's next optional
                // advance, or the timer alone could keep the clock running and starve every other operation.
                this.Runtime.MarkExecutingOperationAsVirtualTimerCallback();
                TimerCallback callback = null;
                object state = null;
                TimeSpan period = Timeout.InfiniteTimeSpan;
                lock (this.SyncObject)
                {
                    if (!ReferenceEquals(delay, this.CurrentDelay) || this.IsDisposed ||
                        !task.IsCompletedSuccessfully)
                    {
                        return;
                    }

                    callback = this.Callback;
                    state = this.State;
                    period = this.Period;
                    if (period == Timeout.InfiniteTimeSpan)
                    {
                        this.CurrentDelay = null;
                    }
                }

                callback(state);

                if (period == Timeout.InfiniteTimeSpan)
                {
                    delay.Dispose();
                }
                else
                {
                    bool shouldRepeat;
                    lock (this.SyncObject)
                    {
                        shouldRepeat = !this.IsDisposed && ReferenceEquals(delay, this.CurrentDelay) &&
                            this.Period == period;
                        if (shouldRepeat)
                        {
                            this.CurrentDelay = null;
                        }
                    }

                    if (shouldRepeat)
                    {
                        delay.Dispose();
                        this.Schedule(period);
                    }
                }
            }

            private sealed class ScheduledDelay : IDisposable
            {
                internal readonly VirtualTimer Owner;
                internal readonly System.Threading.CancellationTokenSource Cancellation =
                    new System.Threading.CancellationTokenSource();
                private Task Task;

                internal ScheduledDelay(VirtualTimer owner)
                {
                    this.Owner = owner;
                }

                internal void SetTask(Task task)
                {
                    this.Task = task;
                    if (this.Cancellation.IsCancellationRequested)
                    {
                        this.Cancellation.Dispose();
                    }
                }

                internal void Cancel()
                {
                    this.Cancellation.Cancel();
                    if (this.Task != null)
                    {
                        this.Cancellation.Dispose();
                    }
                }

                public void Dispose() => this.Cancellation.Dispose();
            }
        }
    }
}
#endif

// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

#if NET
using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
using Microsoft.Coyote.Runtime;
using ControlledTask = Microsoft.Coyote.Rewriting.Types.Threading.Tasks.Task;
using SystemCancellationToken = System.Threading.CancellationToken;
using SystemPeriodicTimer = System.Threading.PeriodicTimer;
using SystemTasks = System.Threading.Tasks;
using SystemTimeout = System.Threading.Timeout;

namespace Microsoft.Coyote.Rewriting.Types.Threading
{
    /// <summary>
    /// Provides methods for periodic timers that can be controlled during testing.
    /// </summary>
    /// <remarks>
    /// This type is intended for compiler use rather than use directly in code.
    /// <para>
    /// Without this model a <c>while (await timer.WaitForNextTickAsync(token))</c> loop is invisible to the
    /// scheduler: the tick arrives from a real timer on a thread the runtime has no record of, so the loop
    /// is never interleaved with anything and a test over it passes without having explored a schedule. The
    /// cadence becomes a scheduling point instead, which costs no wall-clock time — a service that renews a
    /// lease every ten seconds is tested at the same speed as one that renews every millisecond.
    /// </para>
    /// <para>The modeled wait is single-consumer and remains active until its result is consumed.</para>
    /// </remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class PeriodicTimer
    {
        /// <summary>The largest period the runtime accepts, matching <see cref="SystemPeriodicTimer"/>.</summary>
        private const uint MaxPeriodMilliseconds = 0xFFFFFFFE;

        /// <summary>
        /// State the model keeps for each controlled timer, keyed weakly so it is collected with the timer
        /// itself. A cache that had to be emptied between iterations could strand an entry when an iteration
        /// is interrupted part way through — the failure mode
        /// <c>Monitor.SynchronizedBlock.ResetCache</c> exists to undo.
        /// </summary>
        private static readonly ConditionalWeakTable<SystemPeriodicTimer, State> Timers =
            new ConditionalWeakTable<SystemPeriodicTimer, State>();

        /// <summary>
        /// Initializes a new <see cref="SystemPeriodicTimer"/> that can be controlled during testing.
        /// </summary>
        public static SystemPeriodicTimer Create(TimeSpan period)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving)
            {
                // The model owns the cadence, so the real timer is built to never fire. Every member that
                // could observe the difference is intercepted below and answers from the recorded period.
                ValidatePeriod(period);
                var timer = new SystemPeriodicTimer(SystemTimeout.InfiniteTimeSpan);
                Timers.Add(timer, new State(period));
                return timer;
            }

            return new SystemPeriodicTimer(period);
        }

        /// <summary>
        /// Gets the period between ticks.
        /// </summary>
#pragma warning disable CA1707 // Identifiers should not contain underscores
#pragma warning disable SA1300 // Element should begin with upper-case letter
#pragma warning disable IDE1006 // Naming Styles
        public static TimeSpan get_Period(SystemPeriodicTimer instance) =>
            Timers.TryGetValue(instance, out State state) ? state.Period : instance.Period;

        /// <summary>
        /// Sets the period between ticks.
        /// </summary>
        public static void set_Period(SystemPeriodicTimer instance, TimeSpan value)
#pragma warning restore IDE1006 // Naming Styles
#pragma warning restore SA1300 // Element should begin with upper-case letter
#pragma warning restore CA1707 // Identifiers should not contain underscores
        {
            if (Timers.TryGetValue(instance, out State state))
            {
                ValidatePeriod(value);
                state.Period = value;
                return;
            }

            instance.Period = value;
        }

        /// <summary>
        /// Waits for the next tick of the timer, or for the timer to be disposed.
        /// </summary>
        public static SystemTasks.ValueTask<bool> WaitForNextTickAsync(
            SystemPeriodicTimer instance, SystemCancellationToken cancellationToken = default)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                Timers.TryGetValue(instance, out State state))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return SystemTasks.ValueTask.FromCanceled<bool>(cancellationToken);
                }

                return state.BeginWait(cancellationToken, out _);
            }

            return instance.WaitForNextTickAsync(cancellationToken);
        }

        /// <summary>
        /// Disposes the timer, so that every subsequent wait reports no tick.
        /// </summary>
        public static void Dispose(SystemPeriodicTimer instance)
        {
            if (Timers.TryGetValue(instance, out State state))
            {
                state.Dispose();
            }

            instance.Dispose();
        }

        private static void ValidatePeriod(TimeSpan period)
        {
            if (period != SystemTimeout.InfiniteTimeSpan &&
                (period <= TimeSpan.Zero || period.TotalMilliseconds > MaxPeriodMilliseconds))
            {
                throw new ArgumentOutOfRangeException(nameof(period));
            }
        }

        /// <summary>
        /// The cadence and lifetime of one controlled timer.
        /// </summary>
        private sealed class State : IValueTaskSource<bool>
        {
            private readonly object SyncObject = new object();
            private ManualResetValueTaskSourceCore<bool> Source;
            private System.Threading.CancellationTokenRegistration CancellationRegistration;
            private bool IsActive;

            internal State(TimeSpan period)
            {
                this.Period = period;
                this.Source.RunContinuationsAsynchronously = true;
            }

            internal TimeSpan Period { get; set; }

            internal bool IsDisposed { get; set; }

            internal SystemTasks.ValueTask<bool> BeginWait(
                SystemCancellationToken cancellationToken, out short version)
            {
                lock (this.SyncObject)
                {
                    if (this.IsDisposed)
                    {
                        version = 0;
                        return new SystemTasks.ValueTask<bool>(false);
                    }

                    if (this.IsActive)
                    {
                        throw new InvalidOperationException(
                            "Operation is not valid due to the current state of the object.");
                    }

                    this.Source.Reset();
                    this.IsActive = true;
                    version = this.Source.Version;
                }

                System.Threading.CancellationTokenRegistration registration = cancellationToken.Register(
                    state => ((State)((object[])state)[0]).Cancel((short)((object[])state)[1],
                        (SystemCancellationToken)((object[])state)[2]),
                    new object[] { this, version, cancellationToken });
                lock (this.SyncObject)
                {
                    if (this.IsActive && version == this.Source.Version)
                    {
                        this.CancellationRegistration = registration;
                    }
                    else
                    {
                        registration.Dispose();
                    }
                }

                return new SystemTasks.ValueTask<bool>(this, version);
            }

            internal void Signal(short version, bool result)
            {
                lock (this.SyncObject)
                {
                    if (this.IsActive && version == this.Source.Version &&
                        this.Source.GetStatus(version) is ValueTaskSourceStatus.Pending)
                    {
                        this.Source.SetResult(result);
                    }
                }
            }

            internal void Dispose()
            {
                lock (this.SyncObject)
                {
                    this.IsDisposed = true;
                    if (this.IsActive && this.Source.GetStatus(this.Source.Version) is ValueTaskSourceStatus.Pending)
                    {
                        this.Source.SetResult(false);
                    }
                }
            }

            private void Cancel(short version, SystemCancellationToken token)
            {
                lock (this.SyncObject)
                {
                    if (this.IsActive && version == this.Source.Version &&
                        this.Source.GetStatus(version) is ValueTaskSourceStatus.Pending)
                    {
                        this.Source.SetException(new OperationCanceledException(token));
                    }
                }
            }

            bool IValueTaskSource<bool>.GetResult(short token)
            {
                try
                {
                    lock (this.SyncObject)
                    {
                        return this.Source.GetResult(token);
                    }
                }
                finally
                {
                    lock (this.SyncObject)
                    {
                        if (this.IsActive && token == this.Source.Version)
                        {
                            this.IsActive = false;
                            this.CancellationRegistration.Dispose();
                            this.CancellationRegistration = default;
                        }
                    }
                }
            }

            ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token)
            {
                lock (this.SyncObject)
                {
                    return this.Source.GetStatus(token);
                }
            }

            void IValueTaskSource<bool>.OnCompleted(Action<object> continuation, object state,
                short token, ValueTaskSourceOnCompletedFlags flags)
            {
                lock (this.SyncObject)
                {
                    this.Source.OnCompleted(continuation, state, token, flags);
                }

                // Publish the scheduler-owned tick only once a consumer has actually suspended on
                // this wait. This leaves a deterministic window for overlap, cancellation and disposal
                // before the tick, just as the real timer does.
                _ = ControlledTask.Run(() => this.Signal(token, true));
            }
        }
    }
}
#endif

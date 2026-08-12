// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

#if NET
using System;
using System.Runtime.CompilerServices;
using Microsoft.Coyote.Runtime;
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
    /// <para>
    /// The wait completes synchronously at that scheduling point rather than staying outstanding, so the
    /// state in which two waits overlap — which the real type rejects with an
    /// <see cref="InvalidOperationException"/> — is one this model never enters. It explores a subset of
    /// the real behaviours, never a superset, so nothing passes here that the runtime would reject.
    /// </para>
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

                if (runtime.TryGetExecutingOperation(out ControlledOperation current))
                {
                    // The tick itself. Modelled as a yield so that every other enabled flow may run between
                    // one tick and the next, which is the only property a timer-driven loop needs tested and
                    // the one an unmodelled timer silently denies.
                    runtime.ScheduleNextOperation(current, SchedulingPointType.Yield, isYielding: true);
                }
                else
                {
                    runtime.NotifyUncontrolledInvocation(nameof(SystemPeriodicTimer.WaitForNextTickAsync));
                }

                // Re-read both after the scheduling point: another flow may have cancelled or disposed while
                // this one was not running, which is exactly the interleaving the yield exists to allow.
                if (cancellationToken.IsCancellationRequested)
                {
                    return SystemTasks.ValueTask.FromCanceled<bool>(cancellationToken);
                }

                return new SystemTasks.ValueTask<bool>(!state.IsDisposed);
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
                state.IsDisposed = true;
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
        private sealed class State
        {
            internal State(TimeSpan period)
            {
                this.Period = period;
            }

            internal TimeSpan Period { get; set; }

            internal bool IsDisposed { get; set; }
        }
    }
}
#endif

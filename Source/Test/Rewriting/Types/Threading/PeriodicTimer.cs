// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

#if NET
using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Runtime.CompilerServices;
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

#if NET8_0_OR_GREATER
        /// <summary>
        /// Initializes a timer using the specified provider. System time remains scheduler-owned,
        /// while a custom provider owns cadence and dispatches its ticks into the controlled runtime.
        /// </summary>
        public static SystemPeriodicTimer Create(TimeSpan period, TimeProvider timeProvider)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving)
            {
                ValidatePeriod(period);
                ArgumentNullException.ThrowIfNull(timeProvider);
                var timer = new SystemPeriodicTimer(SystemTimeout.InfiniteTimeSpan);
                try
                {
                    State state = timeProvider == TimeProvider.System ?
                        new State(period) : State.CreateProviderOwned(period, timeProvider);
                    Timers.Add(timer, state);
                    return timer;
                }
                catch
                {
                    timer.Dispose();
                    throw;
                }
            }

            return new SystemPeriodicTimer(period, timeProvider);
        }
#endif

#if NET8_0_OR_GREATER
        /// <summary>
        /// Gets the period between ticks.
        /// </summary>
#pragma warning disable CA1707 // Identifiers should not contain underscores
#pragma warning disable SA1300 // Element should begin with upper-case letter
#pragma warning disable IDE1006 // Naming Styles
        public static TimeSpan get_Period(SystemPeriodicTimer instance) =>
            Timers.TryGetValue(instance, out State state) ? state.GetPeriod() : instance.Period;

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
                state.SetPeriod(value);
                return;
            }

            instance.Period = value;
        }
#endif

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
            long milliseconds = (long)period.TotalMilliseconds;
            if (period != SystemTimeout.InfiniteTimeSpan &&
                (milliseconds < 1 || milliseconds > MaxPeriodMilliseconds))
            {
                throw new ArgumentOutOfRangeException(nameof(period));
            }
        }

        /// <summary>
        /// The cadence and lifetime of one controlled timer.
        /// </summary>
        private sealed class State : IValueTaskSource<bool>, IControllableValueTaskSource
        {
            private readonly object SyncObject = new object();
            private ManualResetValueTaskSourceCore<bool> Source;
            private System.Threading.CancellationTokenRegistration CancellationRegistration;
            private System.Threading.Tasks.TaskCompletionSource<bool> Completion;
            private System.Threading.CancellationTokenSource CadenceCancellation;
            private System.Threading.Tasks.TaskCompletionSource<bool> CadenceSignal;
            private ProviderTimer ProviderTimer;
            private bool IsActive;
            private bool IsCadenceStarted;
            private bool IsCompleted;
            private bool IsProviderOwned;
            private bool IsSignaled;
            private long PeriodRevision;

            internal State(TimeSpan period)
            {
                this.Period = period;
                this.Source.RunContinuationsAsynchronously = true;
            }

            ~State()
            {
                this.ProviderTimer?.Dispose();
            }

            internal static State CreateProviderOwned(TimeSpan period, TimeProvider timeProvider)
            {
                var state = new State(period)
                {
                    IsProviderOwned = true
                };
                var weakState = new WeakReference<State>(state);
                state.ProviderTimer = ProviderTimer.Create(CoyoteRuntime.Current, timeProvider,
                    static value =>
                    {
                        if (((WeakReference<State>)value).TryGetTarget(out State target))
                        {
                            target.SignalProviderTick();
                        }
                    }, weakState, period, period);
                return state;
            }

            private TimeSpan Period { get; set; }

            private bool IsDisposed { get; set; }

            internal TimeSpan GetPeriod()
            {
                lock (this.SyncObject)
                {
                    return this.Period;
                }
            }

            internal void SetPeriod(TimeSpan value)
            {
                ProviderTimer providerTimer;
                System.Threading.CancellationTokenSource cadenceCancellation = null;
                System.Threading.Tasks.TaskCompletionSource<bool> cadenceSignal = null;
                bool isProviderOwned;
                bool isDisposed = false;
                lock (this.SyncObject)
                {
                    this.Period = value;
                    providerTimer = this.ProviderTimer;
                    isProviderOwned = this.IsProviderOwned;
                    if (!isProviderOwned)
                    {
                        this.PeriodRevision++;
                        isDisposed = this.IsDisposed;
                        cadenceCancellation = this.CadenceCancellation;
                        cadenceSignal = this.CadenceSignal;
                        this.CadenceCancellation = null;
                        this.CadenceSignal = null;
                    }
                }

                if (isProviderOwned)
                {
                    if (providerTimer is null || !providerTimer.Change(value, value))
                    {
                        throw new ObjectDisposedException(nameof(SystemPeriodicTimer));
                    }

                    return;
                }

                WakeCadence(cadenceCancellation, cadenceSignal);
                if (isDisposed)
                {
                    throw new ObjectDisposedException(nameof(SystemPeriodicTimer));
                }
            }

            internal SystemTasks.ValueTask<bool> BeginWait(
                SystemCancellationToken cancellationToken, out short version)
            {
                lock (this.SyncObject)
                {
                    if (this.IsActive)
                    {
                        throw new InvalidOperationException(
                            "Operation is not valid due to the current state of the object.");
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        version = 0;
                        return SystemTasks.ValueTask.FromCanceled<bool>(cancellationToken);
                    }

                    if (this.IsDisposed)
                    {
                        version = 0;
                        return new SystemTasks.ValueTask<bool>(false);
                    }

                    // A custom provider can publish a tick while no consumer is waiting. That tick is
                    // already available, so it must not reserve the single active-wait slot or require
                    // ValueTask result consumption before the next wait may begin.
                    if (this.IsProviderOwned && this.IsSignaled)
                    {
                        this.IsSignaled = false;
                        version = 0;
                        return new SystemTasks.ValueTask<bool>(true);
                    }

                    this.Source.Reset();
                    this.Completion = new System.Threading.Tasks.TaskCompletionSource<bool>(
                        System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
                    CoyoteRuntime.Current.RegisterKnownControlledTask(this.Completion.Task);
                    this.IsActive = true;
                    this.IsCadenceStarted = false;
                    this.IsCompleted = false;
                    version = this.Source.Version;
                }

                System.Threading.CancellationTokenRegistration registration = cancellationToken.Register(
                    state => ((State)((object[])state)[0]).Cancel((short)((object[])state)[1],
                        (SystemCancellationToken)((object[])state)[2]),
                    new object[] { this, version, cancellationToken });
                bool disposeRegistration = false;
                lock (this.SyncObject)
                {
                    if (this.IsActive && version == this.Source.Version)
                    {
                        this.CancellationRegistration = registration;
                    }
                    else
                    {
                        disposeRegistration = true;
                    }
                }

                if (disposeRegistration)
                {
                    registration.Unregister();
                }

                return new SystemTasks.ValueTask<bool>(this, version);
            }

            internal void Signal(short version, bool result)
            {
                System.Threading.Tasks.TaskCompletionSource<bool> completion = null;
                lock (this.SyncObject)
                {
                    if (this.IsActive && version == this.Source.Version &&
                        !this.IsCompleted)
                    {
                        this.IsCompleted = true;
                        completion = this.Completion;
                    }
                }

                if (completion != null)
                {
                    completion.TrySetResult(result);
                    this.Source.SetResult(result);
                }
            }

            internal void Dispose()
            {
                System.Threading.Tasks.TaskCompletionSource<bool> completion = null;
                ProviderTimer providerTimer;
                System.Threading.CancellationTokenSource cadenceCancellation;
                System.Threading.Tasks.TaskCompletionSource<bool> cadenceSignal;
                lock (this.SyncObject)
                {
                    this.IsDisposed = true;
                    this.IsSignaled = true;
                    providerTimer = this.ProviderTimer;
                    cadenceCancellation = this.CadenceCancellation;
                    cadenceSignal = this.CadenceSignal;
                    this.CadenceCancellation = null;
                    this.CadenceSignal = null;
                    if (this.IsActive && !this.IsCompleted)
                    {
                        this.IsCompleted = true;
                        completion = this.Completion;
                    }
                }

                providerTimer?.Dispose();
                if (completion != null)
                {
                    completion.TrySetResult(false);
                    this.Source.SetResult(false);
                }

                WakeCadence(cadenceCancellation, cadenceSignal);
            }

            private void Cancel(short version, SystemCancellationToken token)
            {
                System.Threading.Tasks.TaskCompletionSource<bool> completion = null;
                System.Threading.CancellationTokenSource cadenceCancellation = null;
                System.Threading.Tasks.TaskCompletionSource<bool> cadenceSignal = null;
                lock (this.SyncObject)
                {
                    if (this.IsActive && version == this.Source.Version &&
                        !this.IsCompleted)
                    {
                        this.IsCompleted = true;
                        this.IsSignaled = true;
                        completion = this.Completion;
                        cadenceCancellation = this.CadenceCancellation;
                        cadenceSignal = this.CadenceSignal;
                        this.CadenceCancellation = null;
                        this.CadenceSignal = null;
                    }
                }

                if (completion != null)
                {
                    completion.TrySetCanceled(token);
                    this.Source.SetException(new OperationCanceledException(token));
                }

                WakeCadence(cadenceCancellation, cadenceSignal);
            }

            bool IValueTaskSource<bool>.GetResult(short token)
            {
                System.Threading.CancellationTokenRegistration registration = default;
                try
                {
                    lock (this.SyncObject)
                    {
                        bool result = this.Source.GetResult(token);
                        return result && !this.IsDisposed;
                    }
                }
                finally
                {
                    lock (this.SyncObject)
                    {
                        if (this.IsActive && token == this.Source.Version)
                        {
                            this.IsActive = false;
                            if (!this.IsDisposed)
                            {
                                this.IsSignaled = false;
                            }

                            registration = this.CancellationRegistration;
                            this.CancellationRegistration = default;
                        }
                    }

                    registration.Unregister();
                }
            }

            ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token)
            {
                lock (this.SyncObject)
                {
                    return this.Source.GetStatus(token);
                }
            }

            System.Threading.Tasks.Task IControllableValueTaskSource.GetTask(short token)
            {
                lock (this.SyncObject)
                {
                    _ = this.Source.GetStatus(token);
                    return this.Completion.Task;
                }
            }

            void IValueTaskSource<bool>.OnCompleted(Action<object> continuation, object state,
                short token, ValueTaskSourceOnCompletedFlags flags)
            {
                bool startCadence;
                lock (this.SyncObject)
                {
                    this.Source.OnCompleted(continuation, state, token, flags);
                    startCadence = this.IsActive && token == this.Source.Version &&
                        !this.IsCompleted && !this.IsCadenceStarted && !this.IsProviderOwned;
                    this.IsCadenceStarted |= startCadence;
                }

                if (startCadence)
                {
                    _ = ControlledTask.Run(() => this.RunCadence(token));
                }
            }

            private void SignalProviderTick()
            {
                System.Threading.Tasks.TaskCompletionSource<bool> completion = null;
                lock (this.SyncObject)
                {
                    if (this.IsDisposed || this.IsSignaled)
                    {
                        return;
                    }

                    this.IsSignaled = true;
                    if (this.IsActive && !this.IsCompleted)
                    {
                        this.IsCompleted = true;
                        completion = this.Completion;
                    }
                }

                if (completion != null)
                {
                    completion.TrySetResult(true);
                    this.Source.SetResult(true);
                }
            }

            private void RunCadence(short version)
            {
                while (true)
                {
                    TimeSpan period;
                    long revision;
                    System.Threading.CancellationTokenSource delaySource = null;
                    SystemCancellationToken delayToken = default;
                    System.Threading.Tasks.TaskCompletionSource<bool> signal = null;
                    lock (this.SyncObject)
                    {
                        if (!this.IsActive || version != this.Source.Version ||
                            this.IsCompleted || this.IsDisposed)
                        {
                            return;
                        }

                        period = this.Period;
                        revision = this.PeriodRevision;
                        if (period == SystemTimeout.InfiniteTimeSpan)
                        {
                            signal = new System.Threading.Tasks.TaskCompletionSource<bool>(
                                System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
                            CoyoteRuntime.Current.RegisterKnownControlledTask(signal.Task);
                            this.CadenceSignal = signal;
                        }
                        else
                        {
                            delaySource = new System.Threading.CancellationTokenSource();
                            delayToken = delaySource.Token;
                            this.CadenceCancellation = delaySource;
                        }
                    }

                    bool delayElapsed = false;
                    if (signal != null)
                    {
                        ControlledTask.Wait(signal.Task);
                        signal.Task.GetAwaiter().GetResult();
                    }
                    else
                    {
                        try
                        {
                            SystemTasks.Task delay = ControlledTask.Delay(period, delayToken);
                            ControlledTask.Wait(delay);
                            delay.GetAwaiter().GetResult();
                            delayElapsed = true;
                        }
                        catch (OperationCanceledException ex) when (
                            delayToken.IsCancellationRequested && ex.CancellationToken == delayToken)
                        {
                        }
                    }

                    bool disposeDelay = false;
                    bool publishTick;
                    lock (this.SyncObject)
                    {
                        if (delaySource != null && ReferenceEquals(this.CadenceCancellation, delaySource))
                        {
                            this.CadenceCancellation = null;
                            disposeDelay = true;
                        }

                        if (signal != null && ReferenceEquals(this.CadenceSignal, signal))
                        {
                            this.CadenceSignal = null;
                        }

                        publishTick = delayElapsed && this.IsActive && version == this.Source.Version &&
                            !this.IsCompleted && !this.IsDisposed && revision == this.PeriodRevision;
                    }

                    if (disposeDelay)
                    {
                        delaySource.Dispose();
                    }

                    if (publishTick)
                    {
                        this.Signal(version, true);
                        return;
                    }
                }
            }

            private static void WakeCadence(
                System.Threading.CancellationTokenSource cancellation,
                System.Threading.Tasks.TaskCompletionSource<bool> signal)
            {
                if (cancellation != null)
                {
                    cancellation.Cancel();
                    cancellation.Dispose();
                }

                signal?.TrySetResult(true);
            }
        }
    }
}
#endif

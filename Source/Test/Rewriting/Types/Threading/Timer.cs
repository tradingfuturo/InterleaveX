// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET8_0_OR_GREATER
using System;
using System.Runtime.CompilerServices;
using Microsoft.Coyote.Runtime;
using SystemEventWaitHandle = System.Threading.EventWaitHandle;
using SystemExecutionContext = System.Threading.ExecutionContext;
using SystemTaskCompletionSource = System.Threading.Tasks.TaskCompletionSource;
using SystemTaskCreationOptions = System.Threading.Tasks.TaskCreationOptions;
using SystemThreadInterruptedException = System.Threading.ThreadInterruptedException;
using SystemTimeout = System.Threading.Timeout;
using SystemTimer = System.Threading.Timer;
using SystemTimerCallback = System.Threading.TimerCallback;
using SystemValueTask = System.Threading.Tasks.ValueTask;
using SystemWaitHandle = System.Threading.WaitHandle;

namespace Microsoft.Coyote.Rewriting.Types.Threading
{
    /// <summary>
    /// Provides methods for timers that can be controlled during testing.
    /// </summary>
    /// <remarks>
    /// This type is intended for compiler use rather than use directly in code.
    /// <para>
    /// A framework timer fires its callback from the timer queue on the real thread pool, which the scheduler has no
    /// record of: the callback is never interleaved with anything, and every wait on work it completes is uncontrolled.
    /// The model leaves the framework timer permanently unarmed and owns the schedule itself, through the same
    /// scheduler-owned virtual clock that system time-provider timers use elsewhere in the rewriter - a
    /// <see cref="ProviderTimer"/> over the runtime's virtual time provider. Each due time is a virtual deadline, so a
    /// timer that fires hourly costs no more wall-clock time than one that fires every millisecond, and each callback
    /// runs as a controlled operation, never inline on the thread that armed the timer.
    /// </para>
    /// <para>
    /// One deliberate divergence: a periodic callback is re-armed when it returns rather than when it fired, so two
    /// invocations of one timer never overlap. That is the policy the virtual timer already applies to time-provider
    /// timers, and it is what stops a callback that blocks from adding one new operation per period to a schedule.
    /// </para>
    /// </remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class Timer
    {
        /// <summary>
        /// The largest due time or period the framework accepts, in milliseconds.
        /// </summary>
        private const long MaxSupportedMilliseconds = 0xFFFFFFFE;

        /// <summary>
        /// The framework's message for disposing asynchronously a timer already disposed with a wait handle.
        /// </summary>
        private const string TimerAlreadyClosedMessage = "The timer was already closed using an incompatible method.";

        /// <summary>
        /// The model of each controlled timer, keyed weakly so that it is collected with the timer.
        /// </summary>
        private static readonly ConditionalWeakTable<SystemTimer, State> Timers =
            new ConditionalWeakTable<SystemTimer, State>();

        /// <summary>
        /// Initializes a timer, using 32-bit signed integers to specify the due time and period.
        /// </summary>
        public static SystemTimer Create(SystemTimerCallback callback, object state, int dueTime, int period)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(dueTime, -1, nameof(dueTime));
            ArgumentOutOfRangeException.ThrowIfLessThan(period, -1, nameof(period));
            return CreateCore(callback, state, (uint)dueTime, (uint)period, isStateTheTimer: false);
        }

        /// <summary>
        /// Initializes a timer, using time spans to specify the due time and period.
        /// </summary>
        public static SystemTimer Create(SystemTimerCallback callback, object state, TimeSpan dueTime, TimeSpan period)
        {
            uint due = GetMilliseconds(dueTime, nameof(dueTime));
            uint interval = GetMilliseconds(period, nameof(period));
            return CreateCore(callback, state, due, interval, isStateTheTimer: false);
        }

        /// <summary>
        /// Initializes a timer, using 32-bit unsigned integers to specify the due time and period.
        /// </summary>
        public static SystemTimer Create(SystemTimerCallback callback, object state, uint dueTime, uint period) =>
            CreateCore(callback, state, dueTime, period, isStateTheTimer: false);

        /// <summary>
        /// Initializes a timer, using 64-bit signed integers to specify the due time and period.
        /// </summary>
        public static SystemTimer Create(SystemTimerCallback callback, object state, long dueTime, long period)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(dueTime, -1L, nameof(dueTime));
            ArgumentOutOfRangeException.ThrowIfLessThan(period, -1L, nameof(period));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(dueTime, MaxSupportedMilliseconds, nameof(dueTime));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(period, MaxSupportedMilliseconds, nameof(period));
            return CreateCore(callback, state, (uint)dueTime, (uint)period, isStateTheTimer: false);
        }

        /// <summary>
        /// Initializes an unarmed timer whose callback receives the timer itself as its state.
        /// </summary>
        public static SystemTimer Create(SystemTimerCallback callback) =>
            CreateCore(callback, null, uint.MaxValue, uint.MaxValue, isStateTheTimer: true);

        /// <summary>
        /// Changes the start time and the interval between callbacks, using 32-bit signed integers.
        /// </summary>
        public static bool Change(SystemTimer instance, int dueTime, int period)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(dueTime, -1, nameof(dueTime));
            ArgumentOutOfRangeException.ThrowIfLessThan(period, -1, nameof(period));
            return ChangeCore(instance, (uint)dueTime, (uint)period);
        }

        /// <summary>
        /// Changes the start time and the interval between callbacks, using time spans.
        /// </summary>
        public static bool Change(SystemTimer instance, TimeSpan dueTime, TimeSpan period)
        {
            uint due = GetMilliseconds(dueTime, nameof(dueTime));
            uint interval = GetMilliseconds(period, nameof(period));
            return ChangeCore(instance, due, interval);
        }

        /// <summary>
        /// Changes the start time and the interval between callbacks, using 32-bit unsigned integers.
        /// </summary>
        public static bool Change(SystemTimer instance, uint dueTime, uint period) =>
            ChangeCore(instance, dueTime, period);

        /// <summary>
        /// Changes the start time and the interval between callbacks, using 64-bit signed integers.
        /// </summary>
        public static bool Change(SystemTimer instance, long dueTime, long period)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(dueTime, -1L, nameof(dueTime));
            ArgumentOutOfRangeException.ThrowIfLessThan(period, -1L, nameof(period));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(dueTime, MaxSupportedMilliseconds, nameof(dueTime));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(period, MaxSupportedMilliseconds, nameof(period));
            return ChangeCore(instance, (uint)dueTime, (uint)period);
        }

        /// <summary>
        /// Releases the timer without waiting for a callback that is already running.
        /// </summary>
        public static void Dispose(SystemTimer instance)
        {
            if (instance != null && Timers.TryGetValue(instance, out State model))
            {
                model.Dispose();
            }

            instance.Dispose();
        }

        /// <summary>
        /// Releases the timer and signals the specified wait handle once no callback is running.
        /// </summary>
        public static bool Dispose(SystemTimer instance, SystemWaitHandle notifyObject)
        {
            ArgumentNullException.ThrowIfNull(notifyObject);
            if (instance != null && Timers.TryGetValue(instance, out State model))
            {
                bool result = model.Dispose(notifyObject);
                instance.Dispose();
                return result;
            }

            return instance.Dispose(notifyObject);
        }

        /// <summary>
        /// Releases the timer, completing once no callback is running.
        /// </summary>
        public static SystemValueTask DisposeAsync(SystemTimer instance)
        {
            if (instance != null && Timers.TryGetValue(instance, out State model))
            {
                SystemValueTask result = model.DisposeAsync();
                instance.Dispose();
                return result;
            }

            return instance.DisposeAsync();
        }

        private static SystemTimer CreateCore(SystemTimerCallback callback, object state, uint dueTime, uint period,
            bool isStateTheTimer)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is not SchedulingPolicy.Interleaving)
            {
                return isStateTheTimer ? new SystemTimer(callback) : new SystemTimer(callback, state, dueTime, period);
            }

            // Built unarmed, so that nothing ever reaches the framework timer queue. It keeps the original callback and
            // state, so code outside the rewritten assemblies that arms it directly still gets framework behaviour.
            SystemTimer timer = isStateTheTimer ? new SystemTimer(callback) :
                new SystemTimer(callback, state, uint.MaxValue, uint.MaxValue);
            var model = new State(callback, isStateTheTimer ? timer : state);
            Timers.Add(timer, model);
            _ = model.Change(runtime, dueTime, period);
            return timer;
        }

        private static bool ChangeCore(SystemTimer instance, uint dueTime, uint period)
        {
            var runtime = CoyoteRuntime.Current;
            if (instance != null && runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                Timers.TryGetValue(instance, out State model))
            {
                return model.Change(runtime, dueTime, period);
            }

            return instance.Change(dueTime, period);
        }

        private static uint GetMilliseconds(TimeSpan time, string parameterName)
        {
            long milliseconds = (long)time.TotalMilliseconds;
            ArgumentOutOfRangeException.ThrowIfLessThan(milliseconds, -1L, parameterName);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(milliseconds, MaxSupportedMilliseconds, parameterName);
            return (uint)milliseconds;
        }

        private static TimeSpan ToTimeSpan(uint milliseconds) =>
            milliseconds is uint.MaxValue ? SystemTimeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(milliseconds);

        /// <summary>
        /// The schedule and the callback lifetime of one controlled timer.
        /// </summary>
        /// <remarks>
        /// The monitor below guards only this object's fields and is never held across a call into the runtime:
        /// arming a timer can reach a scheduling point, and the callback that point lets run takes the same monitor.
        /// </remarks>
        private sealed class State
        {
            private readonly object SyncObject = new object();

            private readonly SystemTimerCallback Callback;

            private readonly object CallbackState;

            /// <summary>
            /// The context captured when the timer was created, which the framework also runs every callback in.
            /// </summary>
            private readonly SystemExecutionContext Context;

            private ProviderTimer Schedule;

            private Guid ScheduleRuntimeId;

            private int CallbacksRunning;

            private bool IsDisposed;

            /// <summary>
            /// The wait handle or completion source to signal once the timer is disposed and no callback is running.
            /// </summary>
            private object DisposalNotification;

            internal State(SystemTimerCallback callback, object callbackState)
            {
                this.Callback = callback;
                this.CallbackState = callbackState;
                this.Context = SystemExecutionContext.Capture();
            }

            internal bool Change(CoyoteRuntime runtime, uint dueTime, uint period)
            {
                TimeSpan due = ToTimeSpan(dueTime);

                // The framework treats a zero period exactly like an infinite one: the callback fires once.
                TimeSpan interval = period is 0 ? SystemTimeout.InfiniteTimeSpan : ToTimeSpan(period);

                ProviderTimer schedule;
                ProviderTimer stale = null;
                lock (this.SyncObject)
                {
                    if (this.IsDisposed)
                    {
                        return false;
                    }

                    schedule = this.Schedule;
                    if (schedule != null && this.ScheduleRuntimeId != runtime.Id)
                    {
                        // A timer that outlives the iteration that armed it is re-armed under the runtime changing it
                        // now: the earlier runtime has ended and can no longer run its callback.
                        stale = schedule;
                        schedule = null;
                        this.Schedule = null;
                    }
                }

                stale?.Dispose();
                if (schedule != null)
                {
                    return schedule.Change(due, interval);
                }

                // Arming at a zero due time schedules the first callback as a new operation, which can run before
                // this returns. That is why the schedule is installed only after it exists, and why a disposal or a
                // later change made by that callback in the meantime wins over the schedule created here.
                ProviderTimer created = ProviderTimer.Create(runtime, new RuntimeTimeProvider.VirtualTimeProvider(runtime),
                    static value => ((State)value).OnTick(), this, due, interval);
                bool isInstalled = false;
                lock (this.SyncObject)
                {
                    if (!this.IsDisposed && this.Schedule is null)
                    {
                        this.Schedule = created;
                        this.ScheduleRuntimeId = runtime.Id;
                        isInstalled = true;
                    }
                }

                if (!isInstalled)
                {
                    created.Dispose();
                }

                return true;
            }

            internal void Dispose()
            {
                ProviderTimer schedule;
                lock (this.SyncObject)
                {
                    if (this.IsDisposed)
                    {
                        return;
                    }

                    this.IsDisposed = true;
                    schedule = this.Schedule;
                    this.Schedule = null;
                }

                schedule?.Dispose();
            }

            internal bool Dispose(SystemWaitHandle notifyObject)
            {
                if (notifyObject is not SystemEventWaitHandle)
                {
                    throw new NotSupportedException(
                        "Timer.Dispose(WaitHandle) supports only an EventWaitHandle notification during systematic testing.");
                }

                ProviderTimer schedule;
                bool isSignaledNow;
                lock (this.SyncObject)
                {
                    if (this.IsDisposed)
                    {
                        return false;
                    }

                    this.IsDisposed = true;
                    this.DisposalNotification = notifyObject;
                    schedule = this.Schedule;
                    this.Schedule = null;
                    isSignaledNow = this.CallbacksRunning is 0;
                }

                schedule?.Dispose();
                if (isSignaledNow)
                {
                    Signal(notifyObject);
                }

                return true;
            }

            internal SystemValueTask DisposeAsync()
            {
                ProviderTimer schedule = null;
                SystemTaskCompletionSource completion = null;
                lock (this.SyncObject)
                {
                    if (!this.IsDisposed)
                    {
                        this.IsDisposed = true;
                        schedule = this.Schedule;
                        this.Schedule = null;
                    }
                    else if (this.DisposalNotification is SystemWaitHandle)
                    {
                        return SystemValueTask.FromException(new InvalidOperationException(TimerAlreadyClosedMessage));
                    }

                    if (this.CallbacksRunning > 0)
                    {
                        completion = this.DisposalNotification as SystemTaskCompletionSource;
                        if (completion is null)
                        {
                            completion = new SystemTaskCompletionSource(SystemTaskCreationOptions.RunContinuationsAsynchronously);
                            CoyoteRuntime.Current.RegisterKnownControlledTask(completion.Task);
                            this.DisposalNotification = completion;
                        }
                    }
                }

                schedule?.Dispose();
                return completion is null ? default : new SystemValueTask(completion.Task);
            }

            private static void Signal(object notification)
            {
                if (notification is SystemEventWaitHandle handle)
                {
                    _ = EventWaitHandle.Set(handle);
                }
                else if (notification is SystemTaskCompletionSource completion)
                {
                    _ = completion.TrySetResult();
                }
            }

            private void OnTick()
            {
                lock (this.SyncObject)
                {
                    if (this.IsDisposed)
                    {
                        return;
                    }

                    this.CallbacksRunning++;
                }

                try
                {
                    if (this.Context is null)
                    {
                        this.Callback(this.CallbackState);
                    }
                    else
                    {
                        SystemExecutionContext.Run(this.Context,
                            static value => ((State)value).Callback(((State)value).CallbackState), this);
                    }
                }
                catch (Exception exception) when (exception is not SystemThreadInterruptedException)
                {
                    // An exception escaping a framework timer callback takes the process down. The virtual timer runs
                    // this inside a task continuation that would capture the exception and lose it, so it is reported
                    // as the unhandled exception it is.
                    CoyoteRuntime.Current.NotifyUnhandledException(exception,
                        $"Unhandled exception in a timer callback. {exception}");
                }
                finally
                {
                    object notification = null;
                    lock (this.SyncObject)
                    {
                        this.CallbacksRunning--;
                        if (this.IsDisposed && this.CallbacksRunning is 0)
                        {
                            notification = this.DisposalNotification;
                        }
                    }

                    Signal(notification);
                }
            }
        }
    }
}
#endif

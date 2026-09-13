// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Runtime.CompilerServices;
using Microsoft.Coyote.Runtime;
using SystemCancellationToken = System.Threading.CancellationToken;
using SystemManualResetEvent = System.Threading.ManualResetEvent;
using SystemManualResetEventSlim = System.Threading.ManualResetEventSlim;
using SystemTimeout = System.Threading.Timeout;
using SystemWaitHandle = System.Threading.WaitHandle;

namespace Microsoft.Coyote.Rewriting.Types.Threading
{
    /// <summary>
    /// Provides methods for slim manual-reset events that can be controlled during testing.
    /// </summary>
    /// <remarks>
    /// This type is intended for compiler use rather than use directly in code.
    /// <para>
    /// An unmodelled wait parks its thread in <c>Monitor.Wait</c> inside CoreLib, where the scheduler cannot see it:
    /// the operation stops making scheduling steps, and the periodic monitor reports that as a deadlock. The model
    /// keeps the framework event as the single source of truth for whether it is set - so a <c>Set</c> made by code the
    /// rewriter never visited is still observed - and turns every wait into a pause on that state, with a finite timeout
    /// expressed as a virtual deadline and cancellation folded into the same predicate. Construction, <c>IsSet</c> and
    /// <c>SpinCount</c> need no model for that reason: they neither block nor carry state the model keeps elsewhere.
    /// </para>
    /// <para>
    /// <see cref="SystemManualResetEventSlim.WaitHandle"/> hands out a modelled <see cref="SystemManualResetEvent"/> in
    /// place of the framework's lazily created kernel event, kept in step by the modelled <c>Set</c> and <c>Reset</c>,
    /// so that it can take part in a modelled <c>WaitHandle.WaitAny</c> or <c>WaitAll</c>.
    /// </para>
    /// </remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class ManualResetEventSlim
    {
        /// <summary>
        /// The modelled wait handles handed out so far, keyed weakly so each is collected with its event.
        /// </summary>
        private static readonly ConditionalWeakTable<SystemManualResetEventSlim, HandleState> Handles =
            new ConditionalWeakTable<SystemManualResetEventSlim, HandleState>();

        /// <summary>
        /// Sets the state of the event to signaled, which allows any operation waiting on it to proceed.
        /// </summary>
        public static void Set(SystemManualResetEventSlim instance)
        {
            instance.Set();
            UpdateWaitHandle(instance, isSet: true);
        }

        /// <summary>
        /// Sets the state of the event to nonsignaled, which causes operations to block.
        /// </summary>
        public static void Reset(SystemManualResetEventSlim instance)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                runtime.Configuration.IsLockAccessRaceCheckingEnabled &&
                runtime.TryGetExecutingOperation(out ControlledOperation current))
            {
                using (runtime.EnterSynchronizedSection())
                {
                    runtime.ScheduleNextOperation(current, SchedulingPointType.Interleave);
                }
            }

            instance.Reset();
            UpdateWaitHandle(instance, isSet: false);
        }

        /// <summary>
        /// Blocks the current operation until the event is set.
        /// </summary>
        public static void Wait(SystemManualResetEventSlim instance) =>
            _ = Wait(instance, SystemTimeout.Infinite, default);

        /// <summary>
        /// Blocks the current operation until the event is set, while observing a cancellation token.
        /// </summary>
        public static void Wait(SystemManualResetEventSlim instance, SystemCancellationToken cancellationToken) =>
            _ = Wait(instance, SystemTimeout.Infinite, cancellationToken);

        /// <summary>
        /// Blocks the current operation until the event is set, using a time span to measure the time interval.
        /// </summary>
        public static bool Wait(SystemManualResetEventSlim instance, TimeSpan timeout) =>
            Wait(instance, ToMilliseconds(timeout), default);

        /// <summary>
        /// Blocks the current operation until the event is set, using a time span to measure the time interval,
        /// while observing a cancellation token.
        /// </summary>
        public static bool Wait(SystemManualResetEventSlim instance, TimeSpan timeout,
            SystemCancellationToken cancellationToken) =>
            Wait(instance, ToMilliseconds(timeout), cancellationToken);

        /// <summary>
        /// Blocks the current operation until the event is set, using a 32-bit signed integer to measure the time interval.
        /// </summary>
        public static bool Wait(SystemManualResetEventSlim instance, int millisecondsTimeout) =>
            Wait(instance, millisecondsTimeout, default);

        /// <summary>
        /// Blocks the current operation until the event is set, using a 32-bit signed integer to measure the time
        /// interval, while observing a cancellation token.
        /// </summary>
        public static bool Wait(SystemManualResetEventSlim instance, int millisecondsTimeout,
            SystemCancellationToken cancellationToken)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is not SchedulingPolicy.Interleaving ||
                !runtime.TryGetExecutingOperation(out ControlledOperation current))
            {
                return instance.Wait(millisecondsTimeout, cancellationToken);
            }

            // A zero-timeout framework wait never blocks or spins. It performs the disposed and pre-cancellation checks
            // in the framework's own order before reporting whether the event is set.
            bool isSet = instance.Wait(0, cancellationToken);
            if (millisecondsTimeout < SystemTimeout.Infinite)
            {
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));
            }

            if (isSet)
            {
                return true;
            }

            if (millisecondsTimeout is 0)
            {
                return false;
            }

            if (runtime.Configuration.IsLockAccessRaceCheckingEnabled)
            {
                using (runtime.EnterSynchronizedSection())
                {
                    runtime.ScheduleNextOperation(current, SchedulingPointType.Acquire);
                }
            }

            Func<bool> isResolved = () => instance.IsSet || cancellationToken.IsCancellationRequested;
            if (millisecondsTimeout is SystemTimeout.Infinite)
            {
                runtime.PauseOperationUntil(current, isResolved, debugMsg: "'ManualResetEventSlim' to be set");
            }
            else
            {
                long deadline = runtime.CreateVirtualDeadline(TimeSpan.FromMilliseconds(millisecondsTimeout));
                _ = runtime.PauseOperationUntilDeadline(current, isResolved, deadline,
                    debugMsg: "'ManualResetEventSlim' to be set");
            }

            // The framework rechecks the event before the token on every wake-up, so an event that is both set and
            // canceled by the time the wait resumes reports success.
            if (instance.IsSet)
            {
                return true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }

#pragma warning disable CA1707 // Identifiers should not contain underscores
#pragma warning disable SA1300 // Element should begin with upper-case letter
#pragma warning disable IDE1006 // Naming Styles
        /// <summary>
        /// Gets the wait handle for the event, modelled so that it can take part in modelled multi-handle waits.
        /// </summary>
        public static SystemWaitHandle get_WaitHandle(SystemManualResetEventSlim instance)
#pragma warning restore IDE1006 // Naming Styles
#pragma warning restore SA1300 // Element should begin with upper-case letter
#pragma warning restore CA1707 // Identifiers should not contain underscores
        {
            SystemWaitHandle handle = instance.WaitHandle;
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is not SchedulingPolicy.Interleaving)
            {
                return handle;
            }

            lock (Handles)
            {
                if (Handles.TryGetValue(instance, out HandleState state))
                {
                    if (state.RuntimeId == runtime.Id)
                    {
                        return state.Event;
                    }

                    // An event that outlives the iteration that asked for its handle gets a handle registered with the
                    // runtime that is asking now. The earlier handle belongs to a runtime that can no longer wait on it.
                    Handles.Remove(instance);
                }

                SystemManualResetEvent modelled = ManualResetEvent.Create(instance.IsSet);
                Handles.Add(instance, new HandleState(runtime.Id, modelled));
                return modelled;
            }
        }

        /// <summary>
        /// Releases all resources used by the event, including a modelled wait handle handed out for it.
        /// </summary>
        public static void Dispose(SystemManualResetEventSlim instance)
        {
            instance.Dispose();
            HandleState state;
            lock (Handles)
            {
                if (!Handles.TryGetValue(instance, out state))
                {
                    return;
                }

                Handles.Remove(instance);
            }

            WaitHandle.Dispose(state.Event);
        }

        /// <summary>
        /// Brings a modelled wait handle handed out for the event into step with the event.
        /// </summary>
        private static void UpdateWaitHandle(SystemManualResetEventSlim instance, bool isSet)
        {
            if (!Handles.TryGetValue(instance, out HandleState state))
            {
                return;
            }

            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving && runtime.Id == state.RuntimeId)
            {
                _ = isSet ? EventWaitHandle.Set(state.Event) : EventWaitHandle.Reset(state.Event);
            }
            else
            {
                _ = isSet ? state.Event.Set() : state.Event.Reset();
            }
        }

        /// <summary>
        /// Converts a timeout to whole milliseconds, validating it as the framework does.
        /// </summary>
        private static int ToMilliseconds(TimeSpan timeout)
        {
            long milliseconds = (long)timeout.TotalMilliseconds;
            if (milliseconds < SystemTimeout.Infinite || milliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            return (int)milliseconds;
        }

        /// <summary>
        /// A modelled wait handle and the runtime it is registered with.
        /// </summary>
        private sealed class HandleState
        {
            internal HandleState(Guid runtimeId, SystemManualResetEvent handle)
            {
                this.RuntimeId = runtimeId;
                this.Event = handle;
            }

            internal Guid RuntimeId { get; }

            internal SystemManualResetEvent Event { get; }
        }
    }
}

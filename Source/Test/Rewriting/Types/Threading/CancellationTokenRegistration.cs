// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET8_0_OR_GREATER
using Microsoft.Coyote.Runtime;
using SystemCancellationToken = System.Threading.CancellationToken;
using SystemCancellationTokenRegistration = System.Threading.CancellationTokenRegistration;
using SystemTaskCreationOptions = System.Threading.Tasks.TaskCreationOptions;
using SystemValueTask = System.Threading.Tasks.ValueTask;

namespace Microsoft.Coyote.Rewriting.Types.Threading
{
    /// <summary>
    /// Provides methods for cancellation token registrations that can be controlled during testing.
    /// </summary>
    /// <remarks>
    /// This type is intended for compiler use rather than use directly in code.
    /// <para>
    /// When the callback of a registration is already running on another thread, the framework's
    /// <see cref="SystemCancellationTokenRegistration.DisposeAsync"/> returns a value task that CoreLib completes by
    /// polling the source from the real thread pool. That poll is invisible to the scheduler, so awaiting it was an
    /// uncontrolled wait. Registering the polling task as controlled instead is wrong in the other direction: the
    /// callback's operation can return while the poll has not looked yet, and with nothing else enabled that reads as a
    /// deadlock.
    /// </para>
    /// <para>
    /// The model therefore waits on the fact the poll watches - the id of the callback the source is executing - from a
    /// controlled operation, so the wait resolves at the very scheduling point where the callback returns. Every case
    /// in which the framework does not wait (the callback was removed before it ran, it already finished, it has not
    /// started, or it is running on the disposing thread) still completes synchronously.
    /// </para>
    /// </remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class CancellationTokenRegistration
    {
        /// <summary>
        /// Disposes the registration, returning once an in-flight invocation of its callback has returned.
        /// </summary>
        /// <remarks>
        /// The framework blocks the disposing thread in a spin-and-sleep loop over the same callback id. The controlled
        /// operation that runs the callback is never scheduled while the disposing operation spins, so a callback that
        /// had reached any scheduling point - completing a task, for instance - left both parked until the periodic
        /// monitor reported a hang. The model pauses the disposing operation on that id instead.
        /// </remarks>
        public static void Dispose(ref SystemCancellationTokenRegistration registration)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is not SchedulingPolicy.Interleaving ||
                !runtime.TryGetExecutingOperation(out ControlledOperation _))
            {
                registration.Dispose();
                return;
            }

            if (registration.Unregister() ||
                !CancellationTokenSource.Internals.TryGetInFlightCallback(registration, out object registrations, out long id))
            {
                return;
            }

            bool isCallbackControlled = runtime.IsManagedThreadIdControlled(
                CancellationTokenSource.Internals.GetThreadIdExecutingCallbacks(registrations));
            runtime.PauseOperationUntil(null,
                () => CancellationTokenSource.Internals.GetExecutingCallbackId(registrations) != id,
                isCallbackControlled, "an in-flight cancellation callback to return");
        }

        /// <summary>
        /// Disposes the registration, completing once an in-flight invocation of its callback has returned.
        /// </summary>
        public static SystemValueTask DisposeAsync(ref SystemCancellationTokenRegistration registration)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is not SchedulingPolicy.Interleaving ||
                !runtime.TryGetExecutingOperation(out ControlledOperation _))
            {
                return registration.DisposeAsync();
            }

            if (registration.Unregister() ||
                !CancellationTokenSource.Internals.TryGetInFlightCallback(registration, out object registrations, out long id))
            {
                return default;
            }

            // A callback walk started by uncontrolled code (a framework CancelAfter timer, for instance) makes progress
            // the scheduler cannot observe, and the wait has to say so to be treated as partially controlled.
            bool isCallbackControlled = runtime.IsManagedThreadIdControlled(
                CancellationTokenSource.Internals.GetThreadIdExecutingCallbacks(registrations));
            var factory = runtime.TaskFactory;
            return new SystemValueTask(factory.StartNew(
                () => runtime.PauseOperationUntil(null,
                    () => CancellationTokenSource.Internals.GetExecutingCallbackId(registrations) != id,
                    isCallbackControlled, "an in-flight cancellation callback to return"),
                SystemCancellationToken.None, factory.CreationOptions | SystemTaskCreationOptions.DenyChildAttach,
                factory.Scheduler));
        }
    }
}
#endif

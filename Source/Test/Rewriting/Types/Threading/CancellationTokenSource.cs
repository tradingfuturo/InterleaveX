// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET8_0_OR_GREATER
using System;
using System.Reflection;
using Microsoft.Coyote.Runtime;
using SystemCancellationToken = System.Threading.CancellationToken;
using SystemCancellationTokenRegistration = System.Threading.CancellationTokenRegistration;
using SystemCancellationTokenSource = System.Threading.CancellationTokenSource;
using SystemTask = System.Threading.Tasks.Task;
using SystemTaskCreationOptions = System.Threading.Tasks.TaskCreationOptions;

namespace Microsoft.Coyote.Rewriting.Types.Threading
{
    /// <summary>
    /// Provides methods for cancellation token sources that can be controlled during testing.
    /// </summary>
    /// <remarks>
    /// This type is intended for compiler use rather than use directly in code.
    /// <para>
    /// <see cref="SystemCancellationTokenSource.CancelAsync"/> requests cancellation synchronously and then runs the
    /// registered callbacks from a task that CoreLib starts on <c>TaskScheduler.Default</c>. CoreLib is never
    /// rewritten, so those callbacks ran on the real thread pool where the scheduler cannot see them, and awaiting the
    /// returned task was an uncontrolled wait. The model keeps everything a caller can observe synchronously exactly
    /// as the framework has it - the disposed check, the transition to the canceled state, and the completed task
    /// returned when nothing is registered - and moves only the callback walk onto a new controlled operation. The
    /// callbacks therefore still never run inline on the caller, and the scheduler now chooses when they run.
    /// </para>
    /// </remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class CancellationTokenSource
    {
        /// <summary>
        /// Communicates a request for cancellation, running the registered callbacks asynchronously.
        /// </summary>
        public static SystemTask CancelAsync(SystemCancellationTokenSource source)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is not SchedulingPolicy.Interleaving ||
                !runtime.TryGetExecutingOperation(out ControlledOperation _))
            {
                return source.CancelAsync();
            }

            try
            {
                // The Token getter performs the framework's own disposed check. CancelAsync reports the same
                // condition through the returned task, naming the source type, rather than by throwing.
                _ = source.Token;
            }
            catch (ObjectDisposedException exception)
            {
                return SystemTask.FromException(new ObjectDisposedException(source.GetType().FullName, exception.Message));
            }

            if (!Internals.TransitionToCancellationRequested(source) || !Internals.HasRegisteredCallbacks(source))
            {
                return SystemTask.CompletedTask;
            }

            var factory = runtime.TaskFactory;
            return factory.StartNew(static state => Internals.ExecuteCallbackHandlers((SystemCancellationTokenSource)state),
                source, SystemCancellationToken.None, factory.CreationOptions | SystemTaskCreationOptions.DenyChildAttach,
                factory.Scheduler);
        }

        /// <summary>
        /// Binds the private CoreLib members that the cancellation models have to reproduce exactly.
        /// </summary>
        /// <remarks>
        /// Bound once, on first use, and verified against .NET 8, 9 and 10. A framework that renames any of them fails
        /// with a <see cref="TypeInitializationException"/> naming the missing member, instead of letting a model fall
        /// back to the uncontrolled framework path without saying so.
        /// </remarks>
        internal static class Internals
        {
            private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            private static readonly Func<SystemCancellationTokenSource, bool> TransitionToCancellationRequestedMethod =
                GetMethod(typeof(SystemCancellationTokenSource), "TransitionToCancellationRequested")
                    .CreateDelegate<Func<SystemCancellationTokenSource, bool>>();

            private static readonly Action<SystemCancellationTokenSource, bool> ExecuteCallbackHandlersMethod =
                GetMethod(typeof(SystemCancellationTokenSource), "ExecuteCallbackHandlers")
                    .CreateDelegate<Action<SystemCancellationTokenSource, bool>>();

            private static readonly PropertyInfo IsCancellationCompletedProperty =
                typeof(SystemCancellationTokenSource).GetProperty("IsCancellationCompleted", InstanceMembers) ??
                throw new MissingMemberException(typeof(SystemCancellationTokenSource).FullName, "IsCancellationCompleted");

            private static readonly FieldInfo RegistrationsField = GetField(typeof(SystemCancellationTokenSource), "_registrations");

            private static readonly FieldInfo CallbacksField = GetField(RegistrationsField.FieldType, "Callbacks");

            private static readonly FieldInfo SourceField = GetField(RegistrationsField.FieldType, "Source");

            private static readonly FieldInfo ExecutingCallbackIdField = GetField(RegistrationsField.FieldType, "ExecutingCallbackId");

            private static readonly FieldInfo ThreadIdExecutingCallbacksField =
                GetField(RegistrationsField.FieldType, "ThreadIDExecutingCallbacks");

            private static readonly MethodInfo EnterLockMethod = GetMethod(RegistrationsField.FieldType, "EnterLock");

            private static readonly MethodInfo ExitLockMethod = GetMethod(RegistrationsField.FieldType, "ExitLock");

            private static readonly FieldInfo RegistrationIdField = GetField(typeof(SystemCancellationTokenRegistration), "_id");

            private static readonly FieldInfo RegistrationNodeField = GetField(typeof(SystemCancellationTokenRegistration), "_node");

            private static readonly FieldInfo NodeRegistrationsField = GetField(RegistrationNodeField.FieldType, "Registrations");

            /// <summary>
            /// Moves the source into the canceled state, returning false if cancellation was already requested.
            /// </summary>
            internal static bool TransitionToCancellationRequested(SystemCancellationTokenSource source) =>
                TransitionToCancellationRequestedMethod(source);

            /// <summary>
            /// Runs every registered callback, aggregating their exceptions exactly as <c>CancelAsync</c> does.
            /// </summary>
            internal static void ExecuteCallbackHandlers(SystemCancellationTokenSource source) =>
                ExecuteCallbackHandlersMethod(source, false);

            /// <summary>
            /// Returns whether any callback is registered, read under the registrations lock as the framework reads it.
            /// </summary>
            internal static bool HasRegisteredCallbacks(SystemCancellationTokenSource source)
            {
                object registrations = RegistrationsField.GetValue(source);
                if (registrations is null)
                {
                    return false;
                }

                EnterLockMethod.Invoke(registrations, null);
                try
                {
                    return CallbacksField.GetValue(registrations) != null;
                }
                finally
                {
                    ExitLockMethod.Invoke(registrations, null);
                }
            }

            /// <summary>
            /// Returns whether the callback of a registration that could not be unregistered is running on another
            /// thread right now, which is exactly the condition under which the framework's <c>DisposeAsync</c> waits.
            /// </summary>
            internal static bool TryGetInFlightCallback(SystemCancellationTokenRegistration registration,
                out object registrations, out long id)
            {
                object boxed = registration;
                object node = RegistrationNodeField.GetValue(boxed);
                registrations = node is null ? null : NodeRegistrationsField.GetValue(node);
                id = (long)RegistrationIdField.GetValue(boxed);
                if (registrations is null)
                {
                    return false;
                }

                var source = (SystemCancellationTokenSource)SourceField.GetValue(registrations);
                return source.IsCancellationRequested &&
                    !(bool)IsCancellationCompletedProperty.GetValue(source) &&
                    GetThreadIdExecutingCallbacks(registrations) != Environment.CurrentManagedThreadId &&
                    GetExecutingCallbackId(registrations) == id;
            }

            /// <summary>
            /// Returns the id of the callback that the source is running, or zero once the callback walk has ended.
            /// </summary>
            internal static long GetExecutingCallbackId(object registrations) =>
                (long)ExecutingCallbackIdField.GetValue(registrations);

            /// <summary>
            /// Returns the managed id of the thread that is running the callbacks of the source.
            /// </summary>
            internal static int GetThreadIdExecutingCallbacks(object registrations) =>
                (int)ThreadIdExecutingCallbacksField.GetValue(registrations);

            private static FieldInfo GetField(Type type, string name) =>
                type.GetField(name, InstanceMembers) ?? throw new MissingFieldException(type.FullName, name);

            private static MethodInfo GetMethod(Type type, string name) =>
                type.GetMethod(name, InstanceMembers) ?? throw new MissingMethodException(type.FullName, name);
        }
    }
}
#endif

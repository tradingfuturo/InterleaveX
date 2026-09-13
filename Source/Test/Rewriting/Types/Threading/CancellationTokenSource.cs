// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET8_0_OR_GREATER
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Coyote.Runtime;
using SystemCancellationToken = System.Threading.CancellationToken;
using SystemCancellationTokenRegistration = System.Threading.CancellationTokenRegistration;
using SystemCancellationTokenSource = System.Threading.CancellationTokenSource;
using SystemTask = System.Threading.Tasks.Task;
using SystemTaskCreationOptions = System.Threading.Tasks.TaskCreationOptions;
using SystemTimeout = System.Threading.Timeout;

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
        /// The largest delay the framework accepts, in milliseconds.
        /// </summary>
        private const long MaxSupportedMilliseconds = 0xFFFFFFFE;

        /// <summary>
        /// The virtual cancellation schedule of each source armed through a model, keyed weakly by the source.
        /// </summary>
        /// <remarks>
        /// <see cref="SystemCancellationTokenSource.CancelAfter(TimeSpan)"/> and the delay constructors arm a framework
        /// timer whose callback cancels the source from the real timer queue. That cancellation completes controlled
        /// waiters from a thread the scheduler has no record of, so a wait that only the deadline could end parks until
        /// the periodic monitor reports a hang. The model owns the deadline instead, as a one-shot virtual timer whose
        /// callback cancels the source on a controlled operation.
        /// <para>
        /// The deadline is idle-only: the clock never takes an optional advance to it while work is enabled, so it
        /// expires when the program is otherwise stuck, or when another deadline carries the clock past it. A budget
        /// therefore bounds a wait that cannot end, without firing against work that is still making progress.
        /// </para>
        /// </remarks>
        private static readonly ConditionalWeakTable<SystemCancellationTokenSource, CancelSchedule> Schedules =
            new ConditionalWeakTable<SystemCancellationTokenSource, CancelSchedule>();

        /// <summary>
        /// Initializes a source that is canceled after the specified delay.
        /// </summary>
        public static SystemCancellationTokenSource Create(TimeSpan delay)
        {
            long milliseconds = GetDelayMilliseconds(delay, nameof(delay));
            if (!IsControlled(out CoyoteRuntime runtime))
            {
                return new SystemCancellationTokenSource(delay);
            }

            return CreateArmed(runtime, milliseconds);
        }

        /// <summary>
        /// Initializes a source that is canceled after the specified number of milliseconds.
        /// </summary>
        public static SystemCancellationTokenSource Create(int millisecondsDelay)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(millisecondsDelay, -1, nameof(millisecondsDelay));
            if (!IsControlled(out CoyoteRuntime runtime))
            {
                return new SystemCancellationTokenSource(millisecondsDelay);
            }

            return CreateArmed(runtime, millisecondsDelay);
        }

        private static SystemCancellationTokenSource CreateArmed(CoyoteRuntime runtime, long milliseconds)
        {
            var source = new SystemCancellationTokenSource();
            if (milliseconds == 0)
            {
                // The framework constructor completes a zero delay before returning, with nothing registered to run.
                source.Cancel();
            }
            else
            {
                Arm(runtime, source, milliseconds);
            }

            return source;
        }

        /// <summary>
        /// Schedules a cancel operation on the source after the specified delay.
        /// </summary>
        public static void CancelAfter(SystemCancellationTokenSource source, TimeSpan delay)
        {
            long milliseconds = GetDelayMilliseconds(delay, nameof(delay));
            if (!IsControlled(out CoyoteRuntime runtime))
            {
                source.CancelAfter(delay);
                return;
            }

            // The Token getter performs the framework's own disposed check, which CancelAfter also throws.
            _ = source.Token;
            Arm(runtime, source, milliseconds);
        }

        /// <summary>
        /// Schedules a cancel operation on the source after the specified number of milliseconds.
        /// </summary>
        public static void CancelAfter(SystemCancellationTokenSource source, int millisecondsDelay)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(millisecondsDelay, -1, nameof(millisecondsDelay));
            if (!IsControlled(out CoyoteRuntime runtime))
            {
                source.CancelAfter(millisecondsDelay);
                return;
            }

            _ = source.Token;
            Arm(runtime, source, millisecondsDelay);
        }

        private static bool IsControlled(out CoyoteRuntime runtime)
        {
            runtime = CoyoteRuntime.Current;
            return runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                runtime.TryGetExecutingOperation(out ControlledOperation _);
        }

        private static long GetDelayMilliseconds(TimeSpan delay, string parameterName)
        {
            long milliseconds = (long)delay.TotalMilliseconds;
            if (milliseconds < -1 || milliseconds > MaxSupportedMilliseconds)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }

            return milliseconds;
        }

        private static void Arm(CoyoteRuntime runtime, SystemCancellationTokenSource source, long milliseconds)
        {
            // The framework ignores a new deadline once cancellation has been requested.
            if (source.IsCancellationRequested)
            {
                return;
            }

            Schedules.GetValue(source, static value => new CancelSchedule(value)).Arm(runtime, milliseconds);
        }

        /// <summary>
        /// The one-shot virtual deadline of a single source.
        /// </summary>
        private sealed class CancelSchedule
        {
            private readonly object SyncObject = new object();

            private readonly SystemCancellationTokenSource Source;

            private ProviderTimer Timer;

            private Guid TimerRuntimeId;

            internal CancelSchedule(SystemCancellationTokenSource source)
            {
                this.Source = source;
            }

            internal void Arm(CoyoteRuntime runtime, long milliseconds)
            {
                TimeSpan due = milliseconds == -1 ? SystemTimeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(milliseconds);
                ProviderTimer timer;
                ProviderTimer stale = null;
                lock (this.SyncObject)
                {
                    timer = this.Timer;
                    if (timer != null && this.TimerRuntimeId != runtime.Id)
                    {
                        // A source that outlives the iteration that armed it is re-armed under the runtime arming it now.
                        stale = timer;
                        timer = null;
                        this.Timer = null;
                    }
                }

                stale?.Dispose();
                if (timer != null)
                {
                    timer.Change(due, SystemTimeout.InfiniteTimeSpan);
                    return;
                }

                if (milliseconds == -1)
                {
                    return;
                }

                // A zero due time schedules the cancellation as a new operation that can run before this returns, so the
                // timer is installed only after it exists, and one installed by a racing arm in the meantime wins.
                ProviderTimer created = ProviderTimer.Create(runtime,
                    new RuntimeTimeProvider.VirtualTimeProvider(runtime, fireOnlyWhenIdle: true),
                    static value => ((CancelSchedule)value).OnDeadline(), this, due, SystemTimeout.InfiniteTimeSpan);
                ProviderTimer installed;
                lock (this.SyncObject)
                {
                    if (this.Timer is null)
                    {
                        this.Timer = created;
                        this.TimerRuntimeId = runtime.Id;
                    }

                    installed = this.Timer;
                }

                if (!ReferenceEquals(installed, created))
                {
                    created.Dispose();
                    installed.Change(due, SystemTimeout.InfiniteTimeSpan);
                }
            }

            private void OnDeadline()
            {
                try
                {
                    this.Source.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The framework disposes the deadline timer together with its source, so a source disposed before its
                    // deadline is never canceled.
                }
            }
        }

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
        /// Releases the source, first waiting for any in-flight callback that links it to its parent tokens.
        /// </summary>
        /// <remarks>
        /// A linked source disposes the registrations that link it to its parents, and CoreLib does that through the
        /// spin-and-sleep wait of <see cref="SystemCancellationTokenRegistration.Dispose"/>. When a parent's
        /// cancellation walk is running the linking callback on another operation, and that walk has reached a
        /// scheduling point - completing a canceled delay, for instance - the disposing operation spins while the
        /// walk is never scheduled again, and both park until the periodic monitor reports a hang. The linking
        /// registrations are therefore released through the controlled registration model first. CoreLib's own
        /// release then finds them unregistered, with no callback left to wait for.
        /// </remarks>
        public static void Dispose(SystemCancellationTokenSource source)
        {
            if (IsControlled(out CoyoteRuntime _))
            {
                foreach (SystemCancellationTokenRegistration linking in Internals.GetLinkingRegistrations(source))
                {
                    SystemCancellationTokenRegistration registration = linking;
                    CancellationTokenRegistration.Dispose(ref registration);
                }
            }

            source.Dispose();
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

            private static readonly Type Linked1Type = GetNestedType("Linked1CancellationTokenSource");

            private static readonly FieldInfo Linked1RegistrationField = GetField(Linked1Type, "_reg1");

            private static readonly Type Linked2Type = GetNestedType("Linked2CancellationTokenSource");

            private static readonly FieldInfo Linked2FirstRegistrationField = GetField(Linked2Type, "_reg1");

            private static readonly FieldInfo Linked2SecondRegistrationField = GetField(Linked2Type, "_reg2");

            private static readonly Type LinkedNType = GetNestedType("LinkedNCancellationTokenSource");

            private static readonly FieldInfo LinkedNRegistrationsField = GetField(LinkedNType, "_linkingRegistrations");

            /// <summary>
            /// Returns the registrations that link a source created by <c>CreateLinkedTokenSource</c> to its parent
            /// tokens, or none for any other source.
            /// </summary>
            internal static SystemCancellationTokenRegistration[] GetLinkingRegistrations(SystemCancellationTokenSource source)
            {
                Type type = source.GetType();
                if (type == Linked1Type)
                {
                    return new[] { (SystemCancellationTokenRegistration)Linked1RegistrationField.GetValue(source) };
                }

                if (type == Linked2Type)
                {
                    return new[]
                    {
                        (SystemCancellationTokenRegistration)Linked2FirstRegistrationField.GetValue(source),
                        (SystemCancellationTokenRegistration)Linked2SecondRegistrationField.GetValue(source),
                    };
                }

                if (type == LinkedNType)
                {
                    return (SystemCancellationTokenRegistration[])LinkedNRegistrationsField.GetValue(source) ??
                        Array.Empty<SystemCancellationTokenRegistration>();
                }

                return Array.Empty<SystemCancellationTokenRegistration>();
            }

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

            private static Type GetNestedType(string name) =>
                typeof(SystemCancellationTokenSource).GetNestedType(name, BindingFlags.NonPublic) ??
                throw new TypeLoadException($"{typeof(SystemCancellationTokenSource).FullName}+{name} is missing.");

            private static FieldInfo GetField(Type type, string name) =>
                type.GetField(name, InstanceMembers) ?? throw new MissingFieldException(type.FullName, name);

            private static MethodInfo GetMethod(Type type, string name) =>
                type.GetMethod(name, InstanceMembers) ?? throw new MissingMethodException(type.FullName, name);
        }
    }
}
#endif

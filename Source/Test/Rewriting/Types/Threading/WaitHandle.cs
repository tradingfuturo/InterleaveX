// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Coyote.Runtime;
using SystemThread = System.Threading.Thread;
using SystemTimeout = System.Threading.Timeout;
using SystemWaitHandle = System.Threading.WaitHandle;

namespace Microsoft.Coyote.Rewriting.Types.Threading
{
    /// <summary>
    /// Represents a thread synchronization event.
    /// </summary>
    /// <remarks>This type is intended for compiler use rather than use directly in code.</remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class WaitHandle
    {
        /// <summary>
        /// Blocks the current thread until the current handle receives a signal.
        /// </summary>
        public static bool WaitOne(SystemWaitHandle instance) => WaitOne(instance, SystemTimeout.Infinite);

        /// <summary>
        /// Blocks the current thread until the current handle receives a signal, using a time span
        /// to specify the time interval.
        /// </summary>
        public static bool WaitOne(SystemWaitHandle instance, TimeSpan timeout) => WaitOne(instance, timeout, false);

        /// <summary>
        /// Blocks the current thread until the current handle receives a signal, using a 32-bit
        /// signed integer to specify the time interval in milliseconds.
        /// </summary>
        public static bool WaitOne(SystemWaitHandle instance, int millisecondsTimeout) =>
            WaitOne(instance, millisecondsTimeout, false);

        /// <summary>
        /// Blocks the current thread until the current handle receives a signal, using a time span
        /// to specify the time interval and specifying whether to exit the synchronization domain
        /// before the wait.
        /// </summary>
        public static bool WaitOne(SystemWaitHandle instance, TimeSpan timeout, bool exitContext)
        {
            long totalMilliseconds = (long)timeout.TotalMilliseconds;
            if (totalMilliseconds < -1 || totalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            return WaitOne(instance, (int)totalMilliseconds, exitContext);
        }

        /// <summary>
        /// Blocks the current thread until the current handle receives a signal, using a 32-bit
        /// signed integer to specify the time interval and specifying whether to exit the
        /// synchronization domain before the wait.
        /// </summary>
        public static bool WaitOne(SystemWaitHandle instance, int millisecondsTimeout, bool exitContext)
        {
            if (millisecondsTimeout < SystemTimeout.Infinite)
            {
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));
            }

            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                Resource.TryFind(instance, out Resource resource))
            {
                return resource.WaitOne(runtime, millisecondsTimeout);
            }

            return instance.WaitOne(millisecondsTimeout, exitContext);
        }

        /// <summary>
        /// Waits for all the elements in the specified array to receive a signal.
        /// </summary>
        public static bool WaitAll(SystemWaitHandle[] waitHandles) => WaitAll(waitHandles, SystemTimeout.Infinite);

        /// <summary>
        /// Waits for all the elements in the specified array to receive a signal, using
        /// a time span value to specify the time interval.
        /// </summary>
        public static bool WaitAll(SystemWaitHandle[] waitHandles, TimeSpan timeout) =>
            WaitAll(waitHandles, timeout, false);

        /// <summary>
        /// Waits for all the elements in the specified array to receive a signal, using
        /// a 32-bit integer value to specify the time interval.
        /// </summary>
        public static bool WaitAll(SystemWaitHandle[] waitHandles, int millisecondsTimeout) =>
            WaitAll(waitHandles, millisecondsTimeout, false);

        /// <summary>
        /// Waits for all the elements in the specified array to receive a signal, using
        /// a time span value to specify the time interval and specifying whether to
        /// exit the synchronization domain before the wait.
        /// </summary>
        public static bool WaitAll(SystemWaitHandle[] waitHandles, TimeSpan timeout, bool exitContext)
        {
            long totalMilliseconds = (long)timeout.TotalMilliseconds;
            if (totalMilliseconds < -1 || totalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            return WaitAll(waitHandles, (int)totalMilliseconds, exitContext);
        }

        /// <summary>
        /// Waits for all the elements in the specified array to receive a signal, using
        /// a 32-bit integer value to specify the time interval and specifying whether to
        /// exit the synchronization domain before the wait.
        /// </summary>
        public static bool WaitAll(SystemWaitHandle[] waitHandles, int millisecondsTimeout, bool exitContext)
        {
            if (millisecondsTimeout < SystemTimeout.Infinite)
            {
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));
            }

            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving)
            {
                return Resource.WaitAll(runtime, waitHandles, millisecondsTimeout);
            }

            return SystemWaitHandle.WaitAll(waitHandles, millisecondsTimeout, exitContext);
        }

        /// <summary>
        /// Waits for any of the elements in the specified array to receive a signal.
        /// </summary>
        public static int WaitAny(SystemWaitHandle[] waitHandles) => WaitAny(waitHandles, SystemTimeout.Infinite);

        /// <summary>
        /// Waits for any of the elements in the specified array to receive a signal, using
        /// a time span value to specify the time interval.
        /// </summary>
        public static int WaitAny(SystemWaitHandle[] waitHandles, TimeSpan timeout) =>
            WaitAny(waitHandles, timeout, false);

        /// <summary>
        /// Waits for any of the elements in the specified array to receive a signal, using
        /// a 32-bit integer value to specify the time interval.
        /// </summary>
        public static int WaitAny(SystemWaitHandle[] waitHandles, int millisecondsTimeout) =>
            WaitAny(waitHandles, millisecondsTimeout, false);

        /// <summary>
        /// Waits for any of the elements in the specified array to receive a signal, using
        /// a time span value to specify the time interval and specifying whether to
        /// exit the synchronization domain before the wait.
        /// </summary>
        public static int WaitAny(SystemWaitHandle[] waitHandles, TimeSpan timeout, bool exitContext)
        {
            long totalMilliseconds = (long)timeout.TotalMilliseconds;
            if (totalMilliseconds < -1 || totalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            return WaitAny(waitHandles, (int)totalMilliseconds, exitContext);
        }

        /// <summary>
        /// Waits for any of the elements in the specified array to receive a signal, using
        /// a 32-bit integer value to specify the time interval and specifying whether to
        /// exit the synchronization domain before the wait.
        /// </summary>
        public static int WaitAny(SystemWaitHandle[] waitHandles, int millisecondsTimeout, bool exitContext)
        {
            if (millisecondsTimeout < SystemTimeout.Infinite)
            {
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));
            }

            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving)
            {
                return Resource.WaitAny(runtime, waitHandles, millisecondsTimeout);
            }

            return SystemWaitHandle.WaitAny(waitHandles, millisecondsTimeout, exitContext);
        }

        /// <summary>
        /// Releases all resources held by the current <see cref="SystemWaitHandle"/>.
        /// </summary>
        public static void Close(SystemWaitHandle instance)
        {
            Resource.Remove(instance);
            instance.Close();
        }

        /// <summary>
        /// Releases all resources held by the current <see cref="SystemWaitHandle"/>.
        /// </summary>
        public static void Dispose(SystemWaitHandle instance)
        {
            Resource.Remove(instance);
            instance.Dispose();
        }

        /// <summary>
        /// Resource that is used to control a <see cref="SystemWaitHandle"/> during testing.
        /// </summary>
        internal abstract class Resource : IDisposable
        {
            /// <summary>
            /// Cache from handles to resources.
            /// </summary>
            private static readonly ConcurrentDictionary<SystemWaitHandle, Resource> Cache =
                new ConcurrentDictionary<SystemWaitHandle, Resource>();

            /// <summary>
            /// The id of the <see cref="CoyoteRuntime"/> that created this handle.
            /// </summary>
            protected readonly Guid RuntimeId;

            /// <summary>
            /// The runtime that owns this resource.
            /// </summary>
            private readonly CoyoteRuntime Runtime;

            /// <summary>
            /// The resource id of this handle.
            /// </summary>
            protected readonly Guid ResourceId;

            /// <summary>
            /// The wait handle that is being controlled.
            /// </summary>
            protected readonly SystemWaitHandle Handle;

            /// <summary>
            /// The signal mode of this handle.
            /// </summary>
            private readonly SignalMode Mode;

            /// <summary>
            /// True if the handle is signaled, else false.
            /// </summary>
            protected bool IsSignaled;

            /// <summary>
            /// Set of waits that observe this resource.
            /// </summary>
            private readonly HashSet<WaitRegistration> WaitRegistrations;

            /// <summary>
            /// The debug name of this handle.
            /// </summary>
            protected readonly string DebugName;

            /// <summary>
            /// Initializes a new instance of the <see cref="Resource"/> class.
            /// </summary>
            internal Resource(CoyoteRuntime runtime, SystemWaitHandle handle, SignalMode mode, bool isSignaled)
            {
                this.RuntimeId = runtime.Id;
                this.Runtime = runtime;
                this.ResourceId = Guid.NewGuid();
                this.Handle = handle;
                this.Mode = mode;
                this.IsSignaled = isSignaled;
                this.WaitRegistrations = new HashSet<WaitRegistration>();
                this.DebugName = $"{handle.GetType().Name}({this.ResourceId})";
            }

            /// <summary>
            /// Adds the specified resource to the cache.
            /// </summary>
            internal static void Add(Resource handle) => Cache.GetOrAdd(handle.Handle, key => handle);

            /// <summary>
            /// Removes the resource associated with the specified wait handle. from the cache.
            /// </summary>
            internal static void Remove(SystemWaitHandle handle)
            {
                if (Cache.TryRemove(handle, out Resource resource))
                {
                    resource.TearDown();
                }
            }

            /// <summary>
            /// Finds the resource associated with the specified wait handle.
            /// </summary>
            internal static bool TryFind(SystemWaitHandle handle, out Resource resource) =>
                Cache.TryGetValue(handle, out resource);

            /// <summary>
            /// Pauses the current operation until it receives a signal.
            /// </summary>
            internal bool WaitOne(CoyoteRuntime runtime, int millisecondsTimeout)
            {
                using (runtime.EnterSynchronizedSection())
                {
                    this.CheckRuntime(runtime);
                    if (!runtime.TryGetExecutingOperation(out ControlledOperation current))
                    {
                        runtime.NotifyUncontrolledSynchronizationInvocation("WaitHandle.WaitOne");
                    }
                    else if (runtime.Configuration.IsLockAccessRaceCheckingEnabled)
                    {
                        runtime.ScheduleNextOperation(current, SchedulingPointType.Acquire);
                    }

                    var registration = new WaitRegistration(current, new[] { this }, WaitRegistrationKind.One);
                    if (registration.TryComplete())
                    {
                        return true;
                    }

                    if (millisecondsTimeout is 0)
                    {
                        return false;
                    }

                    runtime.LogWriter.LogDebug(
                        "[coyote::debug] Operation {0} is waiting for '{1}' to get signaled on thread '{2}'.",
                        current.DebugInfo, this.DebugName, SystemThread.CurrentThread.ManagedThreadId);
                    registration.Register();
                    try
                    {
                        if (millisecondsTimeout is SystemTimeout.Infinite)
                        {
                            current.PauseWithResource(registration.ResourceId);
                        }
                        else
                        {
                            current.PauseWithResourcesOrDelay(new[] { registration.ResourceId },
                                runtime.CreateVirtualDeadline(TimeSpan.FromMilliseconds(millisecondsTimeout)));
                        }

                        runtime.ScheduleNextOperation(current, SchedulingPointType.Pause);
                        registration.ThrowIfDisposed();
                        return current.WakeReason is not OperationWakeReason.Deadline && registration.IsCompleted;
                    }
                    finally
                    {
                        registration.Unregister();
                    }
                }
            }

            /// <summary>
            /// Pauses the current operation until it receives a signal from all the specified handles.
            /// </summary>
            internal static bool WaitAll(CoyoteRuntime runtime, SystemWaitHandle[] waitHandles, int millisecondsTimeout)
            {
                using (runtime.EnterSynchronizedSection())
                {
                    if (!runtime.TryGetExecutingOperation(out ControlledOperation current))
                    {
                        runtime.NotifyUncontrolledSynchronizationInvocation("WaitHandle.WaitAll");
                    }
                    else if (runtime.Configuration.IsLockAccessRaceCheckingEnabled)
                    {
                        runtime.ScheduleNextOperation(current, SchedulingPointType.Acquire);
                    }

                    Resource[] resources = GetResources(runtime, waitHandles);
                    var registration = new WaitRegistration(current, resources, WaitRegistrationKind.All);
                    if (registration.TryComplete())
                    {
                        return true;
                    }

                    if (millisecondsTimeout is 0)
                    {
                        return false;
                    }

                    runtime.LogWriter.LogDebug(
                        "[coyote::debug] Operation {0} is waiting for all 'WaitHandles' to get signaled on thread '{1}'.",
                        current.DebugInfo, SystemThread.CurrentThread.ManagedThreadId);
                    registration.Register();
                    try
                    {
                        if (millisecondsTimeout is SystemTimeout.Infinite)
                        {
                            current.PauseWithResource(registration.ResourceId);
                        }
                        else
                        {
                            current.PauseWithResourcesOrDelay(new[] { registration.ResourceId },
                                runtime.CreateVirtualDeadline(TimeSpan.FromMilliseconds(millisecondsTimeout)));
                        }

                        runtime.ScheduleNextOperation(current, SchedulingPointType.Pause);
                        registration.ThrowIfDisposed();
                        return current.WakeReason is not OperationWakeReason.Deadline && registration.IsCompleted;
                    }
                    finally
                    {
                        registration.Unregister();
                    }
                }
            }

            /// <summary>
            /// Pauses the current operation until it receives a signal from any of the specified handles.
            /// </summary>
            internal static int WaitAny(CoyoteRuntime runtime, SystemWaitHandle[] waitHandles, int millisecondsTimeout)
            {
                using (runtime.EnterSynchronizedSection())
                {
                    if (!runtime.TryGetExecutingOperation(out ControlledOperation current))
                    {
                        runtime.NotifyUncontrolledSynchronizationInvocation("WaitHandle.WaitAny");
                    }
                    else if (runtime.Configuration.IsLockAccessRaceCheckingEnabled)
                    {
                        runtime.ScheduleNextOperation(current, SchedulingPointType.Acquire);
                    }

                    int result = SystemWaitHandle.WaitTimeout;
                    Resource[] resources = GetResources(runtime, waitHandles);
                    var registration = new WaitRegistration(current, resources, WaitRegistrationKind.Any);
                    if (registration.TryComplete())
                    {
                        return registration.ResultIndex;
                    }

                    if (millisecondsTimeout is 0)
                    {
                        return result;
                    }

                    runtime.LogWriter.LogDebug(
                        "[coyote::debug] Operation {0} is waiting for any 'WaitHandle' to get signaled on thread '{1}'.",
                        current.DebugInfo, SystemThread.CurrentThread.ManagedThreadId);
                    registration.Register();
                    try
                    {
                        if (millisecondsTimeout is SystemTimeout.Infinite)
                        {
                            current.PauseWithResource(registration.ResourceId);
                        }
                        else
                        {
                            current.PauseWithResourcesOrDelay(new[] { registration.ResourceId },
                                runtime.CreateVirtualDeadline(TimeSpan.FromMilliseconds(millisecondsTimeout)));
                        }

                        runtime.ScheduleNextOperation(current, SchedulingPointType.Pause);
                        registration.ThrowIfDisposed();
                        return current.WakeReason is OperationWakeReason.Deadline || !registration.IsCompleted ?
                            result : registration.ResultIndex;
                    }
                    finally
                    {
                        registration.Unregister();
                    }
                }
            }

            /// <summary>
            /// Sends a signal to the next waiting operation.
            /// </summary>
            /// <remarks>
            /// It is assumed that this method runs in the scope of the runtime <see cref="SynchronizedSection"/>.
            /// </remarks>
            protected void SignalNext()
            {
                this.NotifyStateChanged();
            }

            /// <summary>
            /// Sends a signal to all waiting operations.
            /// </summary>
            /// <remarks>
            /// It is assumed that this method runs in the scope of the runtime <see cref="SynchronizedSection"/>.
            /// </remarks>
            protected void SignalAll()
            {
                this.NotifyStateChanged();
            }

            /// <summary>
            /// Re-evaluates all waits that observe this resource. A successful auto-reset grant consumes its
            /// signal while the runtime lock is held, before the selected operation can run.
            /// </summary>
            protected void NotifyStateChanged()
            {
                foreach (WaitRegistration registration in this.WaitRegistrations.ToArray())
                {
                    if (registration.TryComplete() && this.Mode is SignalMode.AutoResetSignal && !this.IsSignaled)
                    {
                        // An auto-reset event stores at most one signal, so one completion has consumed it.
                        break;
                    }
                }
            }

            /// <summary>
            /// Releases pending wait registrations when the underlying handle is closed or disposed.
            /// </summary>
            private void TearDown()
            {
                using (this.Runtime.EnterSynchronizedSection())
                {
                    foreach (WaitRegistration registration in this.WaitRegistrations.ToArray())
                    {
                        registration.Dispose();
                    }
                }
            }

            /// <summary>
            /// Represents one pending wait and atomically reserves the signals that satisfy it.
            /// </summary>
            private sealed class WaitRegistration
            {
                /// <summary>
                /// The synthetic resource used to wake the registered operation after its wait has been granted.
                /// </summary>
                internal Guid ResourceId { get; } = Guid.NewGuid();

                /// <summary>
                /// True if this wait has acquired all of the signals it needs.
                /// </summary>
                internal bool IsCompleted { get; private set; }

                /// <summary>
                /// The selected resource index for a wait-any registration.
                /// </summary>
                internal int ResultIndex { get; private set; } = SystemWaitHandle.WaitTimeout;

                /// <summary>
                /// The operation blocked by this registration.
                /// </summary>
                private readonly ControlledOperation Operation;

                /// <summary>
                /// The complete ordered set of resources participating in this wait.
                /// </summary>
                private readonly Resource[] Resources;

                /// <summary>
                /// The kind of wait being modeled.
                /// </summary>
                private readonly WaitRegistrationKind Kind;

                /// <summary>
                /// True after the registration has been attached to each observed resource.
                /// </summary>
                private bool IsRegistered;

                /// <summary>
                /// True if a participating handle was disposed before this wait acquired its signals.
                /// </summary>
                private bool IsDisposed;

                internal WaitRegistration(ControlledOperation operation, Resource[] resources, WaitRegistrationKind kind)
                {
                    this.Operation = operation;
                    this.Resources = resources;
                    this.Kind = kind;
                }

                /// <summary>
                /// Attaches this registration to every observed resource before the operation can be scheduled away.
                /// </summary>
                internal void Register()
                {
                    if (this.IsRegistered)
                    {
                        return;
                    }

                    foreach (Resource resource in this.Resources)
                    {
                        resource.WaitRegistrations.Add(this);
                    }

                    this.IsRegistered = true;
                }

                /// <summary>
                /// Removes this registration from every observed resource.
                /// </summary>
                internal void Unregister()
                {
                    if (!this.IsRegistered)
                    {
                        return;
                    }

                    foreach (Resource resource in this.Resources)
                    {
                        resource.WaitRegistrations.Remove(this);
                    }

                    this.IsRegistered = false;
                }

                /// <summary>
                /// Atomically grants this wait if the complete predicate is currently satisfied.
                /// </summary>
                internal bool TryComplete()
                {
                    if (this.IsCompleted || this.IsDisposed)
                    {
                        return false;
                    }

                    int selectedIndex;
                    if (this.Kind is WaitRegistrationKind.One)
                    {
                        selectedIndex = this.Resources[0].IsSignaled ? 0 : SystemWaitHandle.WaitTimeout;
                    }
                    else if (this.Kind is WaitRegistrationKind.Any)
                    {
                        selectedIndex = Array.FindIndex(this.Resources, resource => resource.IsSignaled);
                        if (selectedIndex < 0)
                        {
                            selectedIndex = SystemWaitHandle.WaitTimeout;
                        }
                    }
                    else
                    {
                        selectedIndex = this.Resources.All(resource => resource.IsSignaled) ? 0 :
                            SystemWaitHandle.WaitTimeout;
                    }

                    if (selectedIndex is SystemWaitHandle.WaitTimeout)
                    {
                        return false;
                    }

                    // Before registration, the current operation is still enabled and no wake-up is necessary.
                    // Once registered, enabling it and consuming auto-reset signals happen under the same runtime lock.
                    if (this.IsRegistered && !this.Operation.TryEnable(this.ResourceId))
                    {
                        return false;
                    }

                    if (this.Kind is WaitRegistrationKind.Any)
                    {
                        this.ResultIndex = selectedIndex;
                        ResetAutoSignal(this.Resources[selectedIndex]);
                    }
                    else if (this.Kind is WaitRegistrationKind.One)
                    {
                        ResetAutoSignal(this.Resources[0]);
                    }
                    else
                    {
                        foreach (Resource resource in this.Resources)
                        {
                            ResetAutoSignal(resource);
                        }
                    }

                    this.IsCompleted = true;
                    return true;
                }

                /// <summary>
                /// Wakes a pending wait during teardown so it can observe the disposed-handle transition.
                /// </summary>
                internal void Dispose()
                {
                    if (!this.IsCompleted)
                    {
                        this.IsDisposed = true;
                        if (this.IsRegistered)
                        {
                            _ = this.Operation.TryEnable(this.ResourceId);
                        }
                    }

                    this.Unregister();
                }

                /// <summary>
                /// Throws the same observable exception as an ordinary wait on a disposed handle.
                /// </summary>
                internal void ThrowIfDisposed()
                {
                    if (this.IsDisposed)
                    {
                        throw new ObjectDisposedException(this.Resources[0].Handle.GetType().Name);
                    }
                }

                /// <summary>
                /// Consumes a single stored auto-reset signal.
                /// </summary>
                private static void ResetAutoSignal(Resource resource)
                {
                    if (resource.Mode is SignalMode.AutoResetSignal)
                    {
                        resource.IsSignaled = false;
                    }
                }
            }

            /// <summary>
            /// The supported wait predicates.
            /// </summary>
            private enum WaitRegistrationKind
            {
                One,
                Any,
                All
            }

            /// <summary>
            /// Return the resources associated with the specified handles.
            /// </summary>
            /// <remarks>
            /// It is assumed that this method runs in the scope of the runtime <see cref="SynchronizedSection"/>.
            /// </remarks>
            private static Resource[] GetResources(CoyoteRuntime runtime, SystemWaitHandle[] waitHandles)
            {
                var resources = new Resource[waitHandles.Length];
                for (int idx = 0; idx < waitHandles.Length; idx++)
                {
                    if (!TryFind(waitHandles[idx], out Resource resource))
                    {
                        var trace = new StackTrace();
                        runtime.NotifyAssertionFailure("Accessing 'WaitHandle' that is not intercepted and controlled " +
                            $"during testing, so it can interfere with the ability to reproduce bug traces:\n{trace}");
                    }

                    resource.CheckRuntime(runtime);
                    resources[idx] = resource;
                }

                return resources;
            }

            /// <summary>
            /// Checks that the current runtime is the same runtime that created this resource.
            /// </summary>
            protected void CheckRuntime(CoyoteRuntime runtime)
            {
                if (runtime.Id != this.RuntimeId)
                {
                    var trace = new StackTrace();
                    runtime.NotifyAssertionFailure($"Accessing '{this.DebugName}' that was created in a " +
                        $"previous test iteration with runtime id '{this.RuntimeId}':\n{trace}");
                }
            }

            /// <summary>
            /// Releases resources used by the resource.
            /// </summary>
            protected void Dispose(bool disposing)
            {
                if (disposing)
                {
                    Resource.Remove(this.Handle);
                }
            }

            /// <summary>
            /// Releases resources used by the resource.
            /// </summary>
            public void Dispose()
            {
                this.Dispose(true);
                GC.SuppressFinalize(this);
            }

            /// <summary>
            /// The mode of this resource.
            /// </summary>
            internal enum SignalMode
            {
                None,
                AutoResetSignal
            }
        }
    }
}

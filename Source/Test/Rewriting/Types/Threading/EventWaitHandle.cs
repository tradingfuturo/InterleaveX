// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using Microsoft.Coyote.Runtime;
using SystemEventResetMode = System.Threading.EventResetMode;
using SystemEventWaitHandle = System.Threading.EventWaitHandle;
using SystemWaitHandle = System.Threading.WaitHandle;

#pragma warning disable CA1416 // Preserve the native platform contract before registry interaction.

namespace Microsoft.Coyote.Rewriting.Types.Threading
{
    /// <summary>
    /// Represents a thread synchronization event.
    /// </summary>
    /// <remarks>This type is intended for compiler use rather than use directly in code.</remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class EventWaitHandle
    {
        /// <summary>
        /// Named OS events are process-wide, but systematic control is intentionally limited to
        /// aliases created or opened by this runtime. An event that predates the runtime (or comes
        /// from another process) remains a raw BCL event rather than being modeled from a guess.
        /// </summary>
        private static readonly ConcurrentDictionary<NamedEventKey, Resource> NamedResources =
            new ConcurrentDictionary<NamedEventKey, Resource>();

        /// <summary>
        /// Initializes a new instance of the <see cref="EventWaitHandle"/> class, specifying whether the wait
        /// handle is initially signaled, and whether it resets automatically or manually.
        /// </summary>
        public static SystemEventWaitHandle Create(bool initialState, SystemEventResetMode mode) =>
            Create(initialState, mode, null, out _);

        /// <summary>
        /// Initializes a new instance of the <see cref="EventWaitHandle"/> class, specifying whether the wait
        /// handle is initially signaled if created as a result of this call, whether it resets automatically
        /// or manually, and the name of a system synchronization event.
        /// </summary>
        public static SystemEventWaitHandle Create(bool initialState, SystemEventResetMode mode, string name) =>
            Create(initialState, mode, name, out _);

        /// <summary>
        /// Initializes a new instance of the <see cref="EventWaitHandle"/> class, specifying whether the wait
        /// handle is initially signaled if created as a result of this call, whether it resets automatically
        /// or manually, the name of a system synchronization event, and a variable whose value after the call
        /// indicates whether the named system event was created.
        /// </summary>
        public static SystemEventWaitHandle Create(bool initialState, SystemEventResetMode mode, string name, out bool createdNew)
        {
            var instance = new SystemEventWaitHandle(initialState, mode, name, out createdNew);
            RegisterCreated(instance, initialState, mode, name, createdNew);
            return instance;
        }

#if NET10_0_OR_GREATER
        /// <summary>
        /// Initializes a named event using the .NET 10 named-handle options overload.
        /// </summary>
        public static SystemEventWaitHandle Create(bool initialState, SystemEventResetMode mode, string name,
            System.Threading.NamedWaitHandleOptions options) =>
            Create(initialState, mode, name, options, out _);

        /// <summary>
        /// Initializes a named event using the .NET 10 named-handle options overload and reports
        /// whether this call created the native event.
        /// </summary>
        public static SystemEventWaitHandle Create(bool initialState, SystemEventResetMode mode, string name,
            System.Threading.NamedWaitHandleOptions options, out bool createdNew)
        {
            var instance = new SystemEventWaitHandle(initialState, mode, name, options, out createdNew);
            RegisterCreated(instance, initialState, mode, name, createdNew);
            return instance;
        }
#endif

        /// <summary>
        /// Opens an existing named event and attaches it to a known same-runtime model when one exists.
        /// Native exceptions are deliberately preserved before any registry interaction.
        /// </summary>
        public static SystemEventWaitHandle OpenExisting(string name)
        {
            SystemEventWaitHandle instance = SystemEventWaitHandle.OpenExisting(name);
            RegisterOpened(instance, name);
            return instance;
        }

#if NET10_0_OR_GREATER
        /// <summary>
        /// Opens an existing named event using the .NET 10 named-handle options overload.
        /// </summary>
        public static SystemEventWaitHandle OpenExisting(string name, System.Threading.NamedWaitHandleOptions options)
        {
            SystemEventWaitHandle instance = SystemEventWaitHandle.OpenExisting(name, options);
            RegisterOpened(instance, name);
            return instance;
        }
#endif

        /// <summary>
        /// Tries to open an existing named event and attaches a same-runtime alias on success.
        /// </summary>
        public static bool TryOpenExisting(string name, out SystemEventWaitHandle result)
        {
            bool opened = SystemEventWaitHandle.TryOpenExisting(name, out result);
            if (opened)
            {
                RegisterOpened(result, name);
            }

            return opened;
        }

#if NET10_0_OR_GREATER
        /// <summary>
        /// Tries to open an existing named event using the .NET 10 named-handle options overload.
        /// </summary>
        public static bool TryOpenExisting(string name, System.Threading.NamedWaitHandleOptions options,
            out SystemEventWaitHandle result)
        {
            bool opened = SystemEventWaitHandle.TryOpenExisting(name, options, out result);
            if (opened)
            {
                RegisterOpened(result, name);
            }

            return opened;
        }
#endif

        /// <summary>
        /// Sets the state of the event to signaled, allowing one or more waiting threads to proceed.
        /// </summary>
        public static bool Set(SystemEventWaitHandle instance)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                Resource.TryFind(instance, out WaitHandle.Resource baseResource) &&
                baseResource is Resource resource)
            {
                return resource.Set(runtime);
            }

            return instance.Set();
        }

        /// <summary>
        /// Sets the state of the event to non-signaled, causing threads to block.
        /// </summary>
        public static bool Reset(SystemEventWaitHandle instance)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                Resource.TryFind(instance, out WaitHandle.Resource baseResource) &&
                baseResource is Resource resource)
            {
                return resource.Reset(runtime);
            }

            return instance.Reset();
        }

        private static void RegisterCreated(SystemEventWaitHandle instance, bool initialState,
            SystemEventResetMode mode, string name, bool createdNew)
        {
            CoyoteRuntime runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is not SchedulingPolicy.Interleaving)
            {
                return;
            }

            if (name is null)
            {
                WaitHandle.Resource.Add(new Resource(runtime, instance, initialState, mode));
                return;
            }

            var key = new NamedEventKey(runtime.Id, name);
            using (runtime.EnterSynchronizedSection())
            {
                if (createdNew)
                {
                    var resource = new Resource(runtime, instance, initialState, mode);
                    resource.LastAliasRemoved = removed => RemoveNamedResource(key, removed);
                    NamedResources[key] = resource;
                    WaitHandle.Resource.Add(resource);
                }
                else if (NamedResources.TryGetValue(key, out Resource resource))
                {
                    // The native event already existed, so the second constructor's initial state
                    // is ignored. It must observe the canonical resource's current state instead.
                    resource.AddAlias(instance);
                }
            }
        }

        private static void RegisterOpened(SystemEventWaitHandle instance, string name)
        {
            CoyoteRuntime runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is not SchedulingPolicy.Interleaving || name is null)
            {
                return;
            }

            var key = new NamedEventKey(runtime.Id, name);
            using (runtime.EnterSynchronizedSection())
            {
                if (NamedResources.TryGetValue(key, out Resource resource))
                {
                    resource.AddAlias(instance);
                }
            }
        }

        private static void RemoveNamedResource(NamedEventKey key, WaitHandle.Resource resource)
        {
            if (NamedResources.TryGetValue(key, out Resource current) && ReferenceEquals(current, resource))
            {
                _ = NamedResources.TryRemove(key, out _);
            }
        }

        private readonly struct NamedEventKey : IEquatable<NamedEventKey>
        {
            private readonly Guid RuntimeId;
            private readonly string Name;

            internal NamedEventKey(Guid runtimeId, string name)
            {
                this.RuntimeId = runtimeId;
                this.Name = name;
            }

            public bool Equals(NamedEventKey other) => this.RuntimeId == other.RuntimeId && this.Name == other.Name;

            public override bool Equals(object obj) => obj is NamedEventKey other && this.Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    return (this.RuntimeId.GetHashCode() * 397) ^ StringComparer.Ordinal.GetHashCode(this.Name);
                }
            }
        }

        /// <summary>
        /// Resource that is used to control an <see cref="EventWaitHandle"/> during testing.
        /// </summary>
        internal class Resource : WaitHandle.Resource
        {
            /// <summary>
            /// The mode of the handle.
            /// </summary>
            private readonly SystemEventResetMode Mode;

            /// <summary>
            /// Initializes a new instance of the <see cref="Resource"/> class.
            /// </summary>
            internal Resource(CoyoteRuntime runtime, SystemWaitHandle handle, bool initialState, SystemEventResetMode mode)
                : base(runtime, handle, GetReleaseMode(mode), initialState)
            {
                this.Mode = mode;
            }

            /// <summary>
            /// Sets the state of this resource to signaled, allowing any paused operation to resume executing.
            /// </summary>
            internal bool Set(CoyoteRuntime runtime)
            {
                using (runtime.EnterSynchronizedSection())
                {
                    this.CheckRuntime(runtime);
                    if (!runtime.TryGetExecutingOperation(out ControlledOperation current))
                    {
                        runtime.NotifyUncontrolledSynchronizationInvocation("EventWaitHandle.Set");
                    }

                    this.IsSignaled = true;
                    if (this.Mode is SystemEventResetMode.AutoReset)
                    {
                        this.SignalNext();
                    }
                    else
                    {
                        this.SignalAll();
                    }

                    return true;
                }
            }

            /// <summary>
            /// Resets the state of this resource to non-signaled.
            /// </summary>
            internal bool Reset(CoyoteRuntime runtime)
            {
                using (runtime.EnterSynchronizedSection())
                {
                    this.CheckRuntime(runtime);
                    if (!runtime.TryGetExecutingOperation(out ControlledOperation current))
                    {
                        runtime.NotifyUncontrolledSynchronizationInvocation("EventWaitHandle.Reset");
                    }
                    else if (runtime.Configuration.IsLockAccessRaceCheckingEnabled)
                    {
                        runtime.ScheduleNextOperation(current, SchedulingPointType.Interleave);
                    }

                    this.IsSignaled = false;
                    // Re-evaluate complete wait predicates after every state transition. A reset cannot grant a
                    // registration, but it ensures no stale observation can survive until a later signal.
                    this.NotifyStateChanged();
                    return true;
                }
            }

            /// <summary>
            /// Get the signal mode of this resource based on the specified <see cref="SystemEventResetMode"/>.
            /// </summary>
            private static WaitHandle.Resource.SignalMode GetReleaseMode(SystemEventResetMode mode) =>
                mode is SystemEventResetMode.AutoReset ?
                    WaitHandle.Resource.SignalMode.AutoResetSignal :
                    WaitHandle.Resource.SignalMode.None;
        }
    }
}

#pragma warning restore CA1416

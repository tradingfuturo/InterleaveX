// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Coyote.Runtime;
using SystemAbandonedMutexException = System.Threading.AbandonedMutexException;
using SystemMutex = System.Threading.Mutex;
using SystemTimeout = System.Threading.Timeout;
using SystemWaitHandle = System.Threading.WaitHandle;

namespace Microsoft.Coyote.Rewriting.Types.Threading
{
    /// <summary>
    /// Provides the current-use modeled subset of <see cref="SystemMutex"/>.
    /// </summary>
    /// <remarks>
    /// Ownership is deliberately associated with a controlled operation, rather than an OS thread:
    /// systematic scheduling can run different logical operations on the same physical thread.
    /// </remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class Mutex
    {
        private static readonly ConditionalWeakTable<CoyoteRuntime, RuntimeState> RuntimeStates =
            new ConditionalWeakTable<CoyoteRuntime, RuntimeState>();
        private static readonly ConditionalWeakTable<SystemMutex, Provenance> Handles =
            new ConditionalWeakTable<SystemMutex, Provenance>();

        /// <summary>
        /// Creates a mutex and attaches it to the current runtime's modeled state when interleaving.
        /// </summary>
        public static SystemMutex Create(bool initiallyOwned, string name)
        {
            CoyoteRuntime runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is not SchedulingPolicy.Interleaving)
            {
                return new SystemMutex(initiallyOwned, name);
            }

            // Never create a kernel-named mutex while systematic testing. The name is model identity
            // only; the backing handle is deliberately anonymous and non-owned.
            name = string.IsNullOrEmpty(name) ? null : name;
            var instance = new SystemMutex(false);
            Handles.Add(instance, new Provenance(runtime.Id));

            RuntimeState state = GetState(runtime);
            using (runtime.EnterSynchronizedSection())
            {
                Resource resource;
                if (name != null && state.NamedResources.TryGetValue(name, out resource))
                {
                    state.AddHandle(instance, resource);
                    return instance;
                }

                ControlledOperation owner = null;
                if (initiallyOwned)
                {
                    owner = GetExecutingOperation(runtime, "Mutex.Create");
                }

                resource = new Resource(state, name, owner, initiallyOwned);
                state.AddHandle(instance, resource);
                state.Resources.Add(resource);
                if (name != null)
                {
                    state.NamedResources.Add(name, resource);
                }
            }

            return instance;
        }

        /// <summary>
        /// Acquires a modeled mutex with the only supported current-use overload.
        /// </summary>
        internal static bool WaitOne(SystemMutex instance, CoyoteRuntime runtime, int millisecondsTimeout, bool exitContext)
        {
            ThrowIfForeign(instance, runtime);
            if (runtime.SchedulingPolicy is not SchedulingPolicy.Interleaving)
            {
                return instance.WaitOne(millisecondsTimeout, exitContext);
            }
            HandleState handle = GetHandleState(instance, runtime, "Mutex.WaitOne");
            Resource resource = handle.Resource;
            if (millisecondsTimeout != SystemTimeout.Infinite || exitContext)
            {
                throw new NotSupportedException("The current-use Mutex model supports only WaitOne().");
            }

            ControlledOperation current = GetExecutingOperation(runtime, "Mutex.WaitOne");
            using (runtime.EnterSynchronizedSection())
            {
                resource.ThrowIfDisposed(handle);
                if (runtime.Configuration.IsLockAccessRaceCheckingEnabled)
                {
                    runtime.ScheduleNextOperation(current, SchedulingPointType.Acquire);
                }

                while (true)
                {
                    if (resource.Owner is null)
                    {
                        resource.Owner = current;
                        resource.RecursionCount = 1;
                        if (resource.IsAbandoned)
                        {
                            resource.IsAbandoned = false;
                            throw new SystemAbandonedMutexException();
                        }

                        return true;
                    }

                    if (resource.Owner == current)
                    {
                        resource.RecursionCount++;
                        return true;
                    }

                    current.PauseWithResource(resource.ResourceId);
                    resource.Waiters.Enqueue(current);
                    runtime.ScheduleNextOperation(current, SchedulingPointType.Pause);
                    resource.ThrowIfDisposed(handle);
                }
            }
        }

        /// <summary>
        /// Releases one recursive acquisition by the current logical owner.
        /// </summary>
        public static void ReleaseMutex(SystemMutex instance)
        {
            CoyoteRuntime runtime = CoyoteRuntime.Current;
            ThrowIfForeign(instance, runtime);
            if (runtime.SchedulingPolicy is not SchedulingPolicy.Interleaving)
            {
                instance.ReleaseMutex();
                return;
            }

            HandleState handle = GetHandleState(instance, runtime, "Mutex.ReleaseMutex");
            Resource resource = handle.Resource;
            ControlledOperation current = GetExecutingOperation(runtime, "Mutex.ReleaseMutex");
            using (runtime.EnterSynchronizedSection())
            {
                resource.ThrowIfDisposed(handle);
                if (resource.Owner != current)
                {
                    throw new ApplicationException("Object synchronization method was called from an unsynchronized block of code.");
                }

                if (--resource.RecursionCount is 0)
                {
                    resource.Owner = null;
                    resource.EnableWaiters();
                }
            }
        }

        /// <summary>
        /// Disposes one alias without invalidating other named aliases of the same modeled mutex.
        /// </summary>
        public static void Dispose(SystemMutex instance)
        {
            CoyoteRuntime runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving)
            {
                if (!TryGetState(runtime, out RuntimeState state) ||
                    !state.ByHandle.TryGetValue(instance, out HandleState handle))
                {
                    throw new NotSupportedException("Mutex.Dispose was invoked on a Mutex outside the current-use model.");
                }

                using (runtime.EnterSynchronizedSection())
                {
                    if (!handle.IsDisposed)
                    {
                        handle.IsDisposed = true;
                        handle.Resource.RemoveAlias();
                    }
                }
            }

            instance.Dispose();
        }

        /// <summary>
        /// Rejects mutex use through WaitAll or WaitAny, which are outside the current-use model.
        /// </summary>
        internal static void ThrowIfUnsupportedMultiWait(SystemWaitHandle[] instances, CoyoteRuntime runtime,
            string operation)
        {
            if (runtime.SchedulingPolicy is not SchedulingPolicy.Interleaving || instances is null)
            {
                return;
            }

            foreach (SystemWaitHandle instance in instances)
            {
                if (instance is SystemMutex mutex && TryGetState(runtime, out RuntimeState state) &&
                    state.ByHandle.TryGetValue(mutex, out _))
                {
                    throw new NotSupportedException($"The current-use Mutex model does not support {operation}.");
                }
            }
        }

        private static RuntimeState GetState(CoyoteRuntime runtime) => RuntimeStates.GetValue(runtime,
            static currentRuntime => new RuntimeState(currentRuntime));

        private static void ThrowIfForeign(SystemMutex instance, CoyoteRuntime runtime)
        {
            if (Handles.TryGetValue(instance, out Provenance provenance) && provenance.RuntimeId != runtime.Id)
            {
                throw new InvalidOperationException("A controlled Mutex cannot escape its testing execution.");
            }
        }

        private sealed class Provenance
        {
            internal readonly Guid RuntimeId;
            internal Provenance(Guid runtimeId) => this.RuntimeId = runtimeId;
        }

        private static bool TryGetState(CoyoteRuntime runtime, out RuntimeState state) =>
            RuntimeStates.TryGetValue(runtime, out state);

        private static HandleState GetHandleState(SystemMutex instance, CoyoteRuntime runtime, string operation)
        {
            if (!TryGetState(runtime, out RuntimeState state) ||
                !state.ByHandle.TryGetValue(instance, out HandleState handle))
            {
                throw new NotSupportedException($"{operation} was invoked on a Mutex outside the current-use model.");
            }

            return handle;
        }

        private static ControlledOperation GetExecutingOperation(CoyoteRuntime runtime, string operation)
        {
            if (!runtime.TryGetExecutingOperation(out ControlledOperation current))
            {
                runtime.NotifyUncontrolledSynchronizationInvocation(operation);
                throw new InvalidOperationException($"{operation} requires a controlled operation.");
            }

            return current;
        }

        private sealed class RuntimeState
        {
            internal readonly ConditionalWeakTable<SystemMutex, HandleState> ByHandle =
                new ConditionalWeakTable<SystemMutex, HandleState>();
            internal readonly Dictionary<string, Resource> NamedResources = new Dictionary<string, Resource>(StringComparer.Ordinal);
            internal readonly List<Resource> Resources = new List<Resource>();

            internal RuntimeState(CoyoteRuntime runtime)
            {
                runtime.OperationCompleted += this.OnOperationCompleted;
            }

            internal void AddHandle(SystemMutex instance, Resource resource)
            {
                this.ByHandle.Add(instance, new HandleState(resource));
                resource.AddAlias();
            }

            internal void RemoveResource(Resource resource)
            {
                this.Resources.Remove(resource);
                if (resource.Name != null && this.NamedResources.TryGetValue(resource.Name, out Resource current) &&
                    ReferenceEquals(current, resource))
                {
                    this.NamedResources.Remove(resource.Name);
                }
            }

            private void OnOperationCompleted(ControlledOperation operation)
            {
                foreach (Resource resource in this.Resources)
                {
                    resource.MarkAbandonedIfOwnedBy(operation);
                }
            }
        }

        private sealed class HandleState
        {
            internal readonly Resource Resource;
            internal bool IsDisposed;

            internal HandleState(Resource resource)
            {
                this.Resource = resource;
            }
        }

        private sealed class Resource
        {
            internal readonly Guid ResourceId = Guid.NewGuid();
            internal readonly Queue<ControlledOperation> Waiters = new Queue<ControlledOperation>();
            private readonly RuntimeState State;
            private int ActiveAliasCount;

            internal ControlledOperation Owner;
            internal int RecursionCount;
            internal bool IsAbandoned;
            internal string Name { get; }

            internal Resource(RuntimeState state, string name, ControlledOperation owner, bool initiallyOwned)
            {
                this.State = state;
                this.Name = name;
                this.Owner = owner;
                this.RecursionCount = initiallyOwned ? 1 : 0;
            }

            internal void AddAlias()
            {
                this.ActiveAliasCount++;
            }

            internal void RemoveAlias()
            {
                if (--this.ActiveAliasCount is 0)
                {
                    this.State.RemoveResource(this);
                }
            }

            internal void ThrowIfDisposed(HandleState handle)
            {
                if (handle.IsDisposed)
                {
                    throw new ObjectDisposedException(nameof(SystemMutex));
                }
            }

            internal void MarkAbandonedIfOwnedBy(ControlledOperation operation)
            {
                if (this.Owner == operation)
                {
                    this.Owner = null;
                    this.RecursionCount = 0;
                    this.IsAbandoned = true;
                    this.EnableWaiters();
                }
            }

            internal void EnableWaiters()
            {
                while (this.Waiters.Count > 0)
                {
                    this.Waiters.Dequeue().TryEnable(this.ResourceId);
                }
            }
        }
    }
}

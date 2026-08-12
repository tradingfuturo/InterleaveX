// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

#if NET
using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Coyote.Runtime;
using SystemBackgroundService = Microsoft.Extensions.Hosting.BackgroundService;
using SystemCancellationToken = System.Threading.CancellationToken;
using SystemCancellationTokenSource = System.Threading.CancellationTokenSource;
using SystemTask = System.Threading.Tasks.Task;

namespace Microsoft.Coyote.Rewriting.Types.Hosting
{
    /// <summary>
    /// Provides methods for hosted background services that can be controlled during testing.
    /// </summary>
    /// <remarks>
    /// This type is intended for compiler use rather than use directly in code.
    /// <para>
    /// The real <c>StopAsync</c> waits on <c>WhenAny(executeTask, Task.Delay(Infinite, token))</c>, and it
    /// does so inside <c>Microsoft.Extensions.Hosting.Abstractions</c> — an assembly the rewriter does not
    /// visit, so that wait runs on real TPL over a real timer. The completion of an UNCONTROLLED task then
    /// becomes a prerequisite for the shutting-down flow, while that task's own completion depends on a
    /// CONTROLLED one, and the scheduler has no way to resolve the cycle: it reports a deadlock on code
    /// that is merely shutting down. Every test that waits for a hosted service to stop is therefore
    /// unrunnable, and the whole <c>ExecuteAsync</c> half of a background service goes unexplored.
    /// </para>
    /// <para>
    /// The model owns the lifecycle instead: the linked token source, the execute task, and a
    /// dependency-based wait that is a scheduling decision like any other and costs no wall-clock time.
    /// </para>
    /// </remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class BackgroundService
    {
        /// <summary>
        /// State the model keeps for each controlled service, keyed weakly so it is collected with the
        /// service itself.
        /// </summary>
        private static readonly ConditionalWeakTable<SystemBackgroundService, State> Services =
            new ConditionalWeakTable<SystemBackgroundService, State>();

        /// <summary>
        /// The <c>ExecuteAsync</c> of each concrete service type. It is protected and abstract, so the
        /// model reaches it the way the runtime does, and caches the lookup per type rather than per call.
        /// </summary>
        private static readonly ConcurrentDictionary<Type, MethodInfo> ExecuteMethods =
            new ConcurrentDictionary<Type, MethodInfo>();

        /// <summary>
        /// The most derived declaration of each intercepted member, per concrete service type. See
        /// <see cref="TryDispatchToOverride"/>.
        /// </summary>
        private static readonly ConcurrentDictionary<(Type, string), MethodInfo> Overrides =
            new ConcurrentDictionary<(Type, string), MethodInfo>();

        /// <summary>
        /// Starts the service, running its <c>ExecuteAsync</c> as a controlled operation.
        /// </summary>
        public static SystemTask StartAsync(SystemBackgroundService instance, SystemCancellationToken cancellationToken)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return instance.StartAsync(cancellationToken);
            }

            if (TryDispatchToOverride(instance, nameof(StartAsync), out SystemTask dispatched, cancellationToken))
            {
                return dispatched;
            }

            State state = Services.GetValue(instance, _ => new State());
            state.StoppingSource = SystemCancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // The real StartAsync invokes ExecuteAsync on the calling thread and only then decides what to
            // hand back, so the model does the same: a service that completes synchronously must surface its
            // outcome to the host through the returned task rather than swallow it.
            SystemTask execute = (SystemTask)ExecuteMethod(instance.GetType())
                .Invoke(instance, new object[] { state.StoppingSource.Token });

            state.ExecuteTask = execute;
            if (execute is null)
            {
                return SystemTask.CompletedTask;
            }

            runtime.RegisterKnownControlledTask(execute);
            return execute.IsCompleted ? execute : SystemTask.CompletedTask;
        }

        /// <summary>
        /// Stops the service, waiting for its <c>ExecuteAsync</c> as a controlled dependency.
        /// </summary>
        public static SystemTask StopAsync(SystemBackgroundService instance, SystemCancellationToken cancellationToken)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return instance.StopAsync(cancellationToken);
            }

            if (TryDispatchToOverride(instance, nameof(StopAsync), out SystemTask dispatched, cancellationToken))
            {
                return dispatched;
            }

            if (!Services.TryGetValue(instance, out State state) || state.ExecuteTask is null)
            {
                // The real StopAsync returns immediately when the service was never started, and a host that
                // stops a service it did not start relies on that.
                return SystemTask.CompletedTask;
            }

            state.StoppingSource?.Cancel();

            SystemTask execute = state.ExecuteTask;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving)
            {
                // The real wait is WhenAny(executeTask, Delay(Infinite, token)), expressed here as the one
                // dependency it really is. It is NOT written as a delay: ScheduleDelay ignores its token and
                // draws a finite budget from Configuration.TimeoutDelay, so an "infinite" delay would resolve
                // on its own and let shutdown walk past a loop that is still running. A service that never
                // observes its token leaves this unresolved, which is a deadlock and is reported as one.
                bool isExecuteUncontrolled = runtime.CheckIfAwaitedTaskIsUncontrolled(execute);
                runtime.PauseOperationUntil(
                    default,
                    () => execute.IsCompleted || cancellationToken.IsCancellationRequested,
                    !isExecuteUncontrolled,
                    "the background service to stop or its shutdown token to be canceled");
            }
            else
            {
                runtime.DelayOperation(runtime.GetExecutingOperation());
            }

            return SystemTask.CompletedTask;
        }

        /// <summary>
        /// Gets the task that is executing the service, or <see langword="null"/> if it has not started.
        /// </summary>
#pragma warning disable CA1707 // Identifiers should not contain underscores
#pragma warning disable SA1300 // Element should begin with upper-case letter
#pragma warning disable IDE1006 // Naming Styles
        public static SystemTask get_ExecuteTask(SystemBackgroundService instance)
#pragma warning restore IDE1006 // Naming Styles
#pragma warning restore SA1300 // Element should begin with upper-case letter
#pragma warning restore CA1707 // Identifiers should not contain underscores
        {
            // The model started the service, so the real private field is null and the property that reads it
            // would answer null for a service that is running. Callers use this to tell "the loop has stopped"
            // from "the wait was abandoned", which is exactly the distinction the model exists to keep honest.
            if (Services.TryGetValue(instance, out State state) && state.ExecuteTask != null)
            {
                return state.ExecuteTask;
            }

            return instance.ExecuteTask;
        }

        /// <summary>
        /// Disposes the service, canceling its execution.
        /// </summary>
        public static void Dispose(SystemBackgroundService instance)
        {
            if (Services.TryGetValue(instance, out State state))
            {
                state.StoppingSource?.Cancel();
            }

            instance.Dispose();
        }

        /// <summary>
        /// Restores virtual dispatch for a member the rewriter turned into a static call.
        /// </summary>
        /// <remarks>
        /// The intercepted members are virtual, and a rewritten <c>callvirt</c> is re-emitted as a static
        /// <c>call</c> — so a call through a variable typed as the base class would run the model and skip a
        /// derived override. The two shapes are indistinguishable from here, because <c>base.StopAsync(...)</c>
        /// inside that very override arrives identically. The flag tells them apart: the first entry for an
        /// instance dispatches to the override, and the <c>base</c> call it makes re-enters, finds the flag
        /// set, and gets the controlled implementation. Cleared in a finally, since an override is under no
        /// obligation to call base.
        /// </remarks>
        private static bool TryDispatchToOverride(
            SystemBackgroundService instance, string name, out SystemTask result, SystemCancellationToken cancellationToken)
        {
            result = null;
            MethodInfo overriding = Overrides.GetOrAdd((instance.GetType(), name), key =>
            {
                MethodInfo method = key.Item1.GetMethod(
                    key.Item2, BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(SystemCancellationToken) }, null);
                return method?.DeclaringType == typeof(SystemBackgroundService) ? null : method;
            });

            if (overriding is null)
            {
                return false;
            }

            State state = Services.GetValue(instance, _ => new State());
            if (!state.EnterOverride(name))
            {
                return false;
            }

            try
            {
                result = (SystemTask)overriding.Invoke(instance, new object[] { cancellationToken });
                return true;
            }
            finally
            {
                state.ExitOverride(name);
            }
        }

        private static MethodInfo ExecuteMethod(Type serviceType) => ExecuteMethods.GetOrAdd(serviceType, type =>
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                MethodInfo method = current.GetMethod(
                    "ExecuteAsync", BindingFlags.NonPublic | BindingFlags.Instance, null,
                    new[] { typeof(SystemCancellationToken) }, null);
                if (method != null)
                {
                    return method;
                }
            }

            throw new InvalidOperationException(
                $"Unable to find 'ExecuteAsync' on background service '{type.FullName}'.");
        });

        /// <summary>
        /// The lifecycle of one controlled background service.
        /// </summary>
        private sealed class State
        {
            private readonly ConcurrentDictionary<string, bool> DispatchingMembers =
                new ConcurrentDictionary<string, bool>();

            internal SystemCancellationTokenSource StoppingSource { get; set; }

            internal SystemTask ExecuteTask { get; set; }

            internal bool EnterOverride(string name) => this.DispatchingMembers.TryAdd(name, true);

            internal void ExitOverride(string name) => this.DispatchingMembers.TryRemove(name, out _);
        }
    }
}
#endif

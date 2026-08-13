// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET
#pragma warning disable CS1591 // Compiler-facing rewrite shims mirror framework signatures.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ControlledTask = Microsoft.Coyote.Rewriting.Types.Threading.Tasks.Task;
using SystemBackgroundService = Microsoft.Extensions.Hosting.BackgroundService;

namespace Microsoft.Coyote.Rewriting.Types.Hosting
{
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class Host
    {
        private static readonly Type FrameworkHostType = typeof(HostBuilder).Assembly.GetType(
            "Microsoft.Extensions.Hosting.Internal.Host", throwOnError: false);

        private static readonly ConditionalWeakTable<IHost, State> Hosts =
            new ConditionalWeakTable<IHost, State>();

        private static readonly Dictionary<(Type, string), MethodInfo> NotificationMethods =
            new Dictionary<(Type, string), MethodInfo>();

        public static IServiceProvider get_Services<T>(ref T instance)
            where T : IHost => instance.Services;

        public static IServiceProvider get_Services(IHost instance) => instance.Services;

        public static bool IsFrameworkHost(IHost instance) =>
            instance != null && instance.GetType() == FrameworkHostType;

        public static Task StartAsync<T>(ref T instance, CancellationToken cancellationToken = default)
            where T : IHost
        {
            if (typeof(T).IsValueType || CoyoteRuntime.Current.SchedulingPolicy is SchedulingPolicy.None)
            {
                return instance.StartAsync(cancellationToken);
            }

            return StartAsync((IHost)instance, cancellationToken);
        }

        public static Task StartAsync(IHost instance, CancellationToken cancellationToken = default)
        {
            if (CoyoteRuntime.Current.SchedulingPolicy is SchedulingPolicy.None || !IsFrameworkHost(instance))
            {
                return instance.StartAsync(cancellationToken);
            }

            return ControlledTask.Run(() => StartCoreAsync(instance, cancellationToken));
        }

        public static Task StopAsync(IHost instance, CancellationToken cancellationToken = default)
        {
            if (CoyoteRuntime.Current.SchedulingPolicy is SchedulingPolicy.None || !IsFrameworkHost(instance))
            {
                return instance.StopAsync(cancellationToken);
            }

            return ControlledTask.Run(() => StopCoreAsync(instance, cancellationToken));
        }

        public static Task StopAsync<T>(ref T instance, CancellationToken cancellationToken = default)
            where T : IHost
        {
            if (typeof(T).IsValueType || CoyoteRuntime.Current.SchedulingPolicy is SchedulingPolicy.None)
            {
                return instance.StopAsync(cancellationToken);
            }

            return StopAsync((IHost)instance, cancellationToken);
        }

        public static void Dispose(IHost instance)
        {
            if (CoyoteRuntime.Current.SchedulingPolicy is SchedulingPolicy.None || !IsFrameworkHost(instance))
            {
                instance.Dispose();
                return;
            }

            CancelModelState(instance);
            instance.Dispose();
        }

        public static ValueTask DisposeAsync(IHost instance)
        {
            if (CoyoteRuntime.Current.SchedulingPolicy is SchedulingPolicy.None || !IsFrameworkHost(instance))
            {
                return instance is IAsyncDisposable customAsyncDisposable ?
                    customAsyncDisposable.DisposeAsync() : DisposeSynchronously(instance);
            }

            CancelModelState(instance);
            return instance is IAsyncDisposable asyncDisposable ?
                asyncDisposable.DisposeAsync() : DisposeSynchronously(instance);
        }

        private static async Task StartCoreAsync(IHost instance, CancellationToken cancellationToken)
        {
            State state = Hosts.GetValue(instance, host => new State(host));
            IHostApplicationLifetime applicationLifetime = GetRequired<IHostApplicationLifetime>(instance.Services);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, applicationLifetime.ApplicationStopping);
#if NET8_0_OR_GREATER
            await using TimeoutLease timeout = TimeoutLease.Start(linked, state.Options.StartupTimeout);
#else
            await using TimeoutLease timeout = TimeoutLease.Start(linked, Timeout.InfiniteTimeSpan);
#endif
            CancellationToken token = linked.Token;

            await state.HostLifetime.WaitForStartAsync(token);
            token.ThrowIfCancellationRequested();
            state.Services = GetRequired<IEnumerable<IHostedService>>(instance.Services).ToList();
            state.Starting = true;
#if NET8_0_OR_GREATER
            state.LifecycleServices = state.Services.OfType<IHostedLifecycleService>().ToList();
            IStartupValidator validator = GetOptional<IStartupValidator>(instance.Services);
            validator?.Validate();
            await InvokeAsync(state.LifecycleServices, item => item.StartingAsync(token),
                state.Options.ServicesStartConcurrently, abortOnFirstException: !state.Options.ServicesStartConcurrently);
#endif
            await InvokeAsync(state.Services, item => StartHostedServiceAsync(
                item, token, applicationLifetime, state.Options),
#if NET8_0_OR_GREATER
                state.Options.ServicesStartConcurrently, abortOnFirstException: !state.Options.ServicesStartConcurrently);
#else
                false, abortOnFirstException: true);
#endif

#if NET8_0_OR_GREATER
            await InvokeAsync(state.LifecycleServices, item => item.StartedAsync(token),
                state.Options.ServicesStartConcurrently, abortOnFirstException: !state.Options.ServicesStartConcurrently);
#endif
            Notify(applicationLifetime, "NotifyStarted");
            state.Starting = false;
            state.Started = true;
        }

        private static async Task StopCoreAsync(IHost instance, CancellationToken cancellationToken)
        {
            State state = Hosts.GetValue(instance, host => new State(host));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await using TimeoutLease timeout = TimeoutLease.Start(linked, state.Options.ShutdownTimeout);
            CancellationToken token = linked.Token;
            var exceptions = new List<Exception>();

            if ((state.Starting || state.Started) && state.Services != null)
            {
#if NET8_0_OR_GREATER
                await CaptureAsync(exceptions, () => InvokeAsync(
                    state.LifecycleServices.AsEnumerable().Reverse(), item => item.StoppingAsync(token),
                    state.Options.ServicesStopConcurrently, abortOnFirstException: false));
#endif
                state.ApplicationLifetime.StopApplication();
                await CaptureAsync(exceptions, () => InvokeAsync(
                    state.Services.AsEnumerable().Reverse(), item => HostedService.StopAsync(item, token),
#if NET8_0_OR_GREATER
                    state.Options.ServicesStopConcurrently, abortOnFirstException: false));
#else
                    false, abortOnFirstException: false));
#endif
#if NET8_0_OR_GREATER
                await CaptureAsync(exceptions, () => InvokeAsync(
                    state.LifecycleServices.AsEnumerable().Reverse(), item => item.StoppedAsync(token),
                    state.Options.ServicesStopConcurrently, abortOnFirstException: false));
#endif
            }
            else
            {
                state.ApplicationLifetime.StopApplication();
            }

            Notify(state.ApplicationLifetime, "NotifyStopped");
            await CaptureAsync(exceptions, () => state.HostLifetime.StopAsync(token));
            state.Stopped = true;
            ThrowCollected(exceptions, "One or more hosted services failed to stop.");
        }

        private static async Task InvokeAsync<T>(IEnumerable<T> items, Func<T, Task> action,
            bool concurrently, bool abortOnFirstException)
        {
            var exceptions = new List<Exception>();
            var tasks = new List<Task>();
            foreach (T item in items ?? Enumerable.Empty<T>())
            {
                try
                {
                    Task task = action(item) ?? throw new InvalidOperationException("A host lifecycle callback returned null.");
                    if (concurrently)
                    {
#if !NET10_0_OR_GREATER
                        if (task.IsCanceled)
                        {
                            continue;
                        }
#endif
                        tasks.Add(task);
                    }
                    else
                    {
                        await task;
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                    if (abortOnFirstException)
                    {
                        break;
                    }
                }
            }

            if (concurrently && tasks.Count > 0)
            {
                try
                {
                    await ControlledTask.WhenAll(tasks);
                }
                catch
                {
                    foreach (Task task in tasks.Where(task => task.IsFaulted))
                    {
                        exceptions.AddRange(task.Exception.InnerExceptions);
                    }
                    foreach (Task task in tasks.Where(task => task.IsCanceled))
                    {
                        exceptions.Add(new TaskCanceledException(task));
                    }
                }
            }

            ThrowCollected(exceptions, "One or more hosted services failed to start.");
        }

        private static async Task StartHostedServiceAsync(
            IHostedService service, CancellationToken cancellationToken,
            IHostApplicationLifetime lifetime, HostOptions options)
        {
            await HostedService.StartAsync(service, cancellationToken);
            if (service is SystemBackgroundService backgroundService)
            {
                ObserveBackgroundService(backgroundService, lifetime, options);
            }
        }

        private static async Task CaptureAsync(List<Exception> exceptions, Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (AggregateException ex)
            {
                exceptions.AddRange(ex.InnerExceptions);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }

        private static void ObserveBackgroundService(
            SystemBackgroundService service, IHostApplicationLifetime lifetime, HostOptions options)
        {
            Task execute = Hosting.BackgroundService.get_ExecuteTask(service);
            if (execute is null)
            {
                return;
            }

            _ = ControlledTask.Run(async () =>
            {
                try
                {
                    await execute;
                }
                catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested && execute.IsCanceled)
                {
                }
                catch
                {
                    if (options.BackgroundServiceExceptionBehavior is BackgroundServiceExceptionBehavior.StopHost)
                    {
                        lifetime.StopApplication();
                    }
                }
            });
        }

        private static void CancelModelState(IHost instance)
        {
            if (Hosts.TryGetValue(instance, out State state) && state.Services != null)
            {
                foreach (SystemBackgroundService service in state.Services.OfType<SystemBackgroundService>())
                {
                    Hosting.BackgroundService.Dispose(service);
                }
            }
        }

        private static ValueTask DisposeSynchronously(IHost instance)
        {
            instance.Dispose();
            return default;
        }

        private static T GetRequired<T>(IServiceProvider provider) where T : class =>
            provider.GetService(typeof(T)) as T ??
            throw new InvalidOperationException($"No service for type '{typeof(T)}' has been registered.");

        private static T GetOptional<T>(IServiceProvider provider) where T : class =>
            provider.GetService(typeof(T)) as T;

        private static void Notify(IHostApplicationLifetime lifetime, string name)
        {
            MethodInfo method;
            lock (NotificationMethods)
            {
                if (!NotificationMethods.TryGetValue((lifetime.GetType(), name), out method))
                {
                    method = lifetime.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public |
                        BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    NotificationMethods[(lifetime.GetType(), name)] = method;
                }
            }

            if (method is null)
            {
                return;
            }

            try
            {
                method.Invoke(lifetime, null);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            }
        }

        private static void ThrowCollected(List<Exception> exceptions, string message)
        {
            if (exceptions.Count is 1)
            {
                ExceptionDispatchInfo.Capture(exceptions[0]).Throw();
            }
            if (exceptions.Count > 1)
            {
                throw new AggregateException(message, exceptions);
            }
        }

        private sealed class State
        {
            internal State(IHost host)
            {
                this.ApplicationLifetime = GetRequired<IHostApplicationLifetime>(host.Services);
                this.HostLifetime = GetRequired<IHostLifetime>(host.Services);
                this.Options = GetOptional<IOptions<HostOptions>>(host.Services)?.Value ?? new HostOptions();
            }

            internal IHostApplicationLifetime ApplicationLifetime { get; }
            internal IHostLifetime HostLifetime { get; }
            internal HostOptions Options { get; }
            internal List<IHostedService> Services { get; set; }
#if NET8_0_OR_GREATER
            internal List<IHostedLifecycleService> LifecycleServices { get; set; } = new List<IHostedLifecycleService>();
#endif
            internal bool Started { get; set; }
            internal bool Starting { get; set; }
            internal bool Stopped { get; set; }
        }

    }
}
#pragma warning restore CS1591
#endif

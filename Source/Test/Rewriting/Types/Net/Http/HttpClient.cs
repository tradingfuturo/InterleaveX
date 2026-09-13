// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#if NET
using System;
using System.Reflection;
using Microsoft.Coyote.Runtime;
using ControlledTask = Microsoft.Coyote.Rewriting.Types.Threading.Tasks.Task;
using SystemCancellationToken = System.Threading.CancellationToken;
using SystemCancellationTokenSource = System.Threading.CancellationTokenSource;
using SystemHttpClient = System.Net.Http.HttpClient;
using SystemHttpCompletionOption = System.Net.Http.HttpCompletionOption;
using SystemHttpMessageHandler = System.Net.Http.HttpMessageHandler;
using SystemHttpMessageInvoker = System.Net.Http.HttpMessageInvoker;
using SystemHttpRequestMessage = System.Net.Http.HttpRequestMessage;
using SystemHttpResponseMessage = System.Net.Http.HttpResponseMessage;
using SystemTask = System.Threading.Tasks.Task;
using SystemTasks = System.Threading.Tasks;

namespace Microsoft.Coyote.Rewriting.Types.Net.Http
{
    /// <summary>
    /// Provides methods for controlling an HTTP client during testing.
    /// </summary>
    /// <remarks>This type is intended for compiler use rather than use directly in code.</remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class HttpClient
    {
        private static readonly MethodInfo CheckRequestBeforeSendMethod = typeof(SystemHttpClient).GetMethod(
            "CheckRequestBeforeSend", BindingFlags.NonPublic | BindingFlags.Instance,
            null, new[] { typeof(SystemHttpRequestMessage) }, null);

        private static readonly FieldInfo PendingRequestsSourceField = typeof(SystemHttpClient).GetField(
            "_pendingRequestsCts", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo HandlerField = typeof(SystemHttpMessageInvoker).GetField(
            "_handler", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// Creates a new instance of the HTTP client class that is controlled during testing.
        /// </summary>
        public static SystemHttpClient Create() =>
            new SystemHttpClient(HttpMessageHandler.CreateWithDefaultHandler());

        /// <summary>
        /// Creates a new instance of the HTTP client class that is controlled during testing.
        /// </summary>
        public static SystemHttpClient Create(SystemHttpMessageHandler handler) =>
            new SystemHttpClient(HttpMessageHandler.Create(handler));

        /// <summary>
        /// Creates a new instance of the HTTP client class that is controlled during testing.
        /// </summary>
        public static SystemHttpClient Create(SystemHttpMessageHandler handler, bool disposeHandler) =>
            new SystemHttpClient(HttpMessageHandler.Create(handler), disposeHandler);

        /// <summary>
        /// Injects logic that takes control of the specified http client.
        /// </summary>
        public static SystemHttpClient Control(SystemHttpClient client)
        {
            // Calls on the returned instance are rewritten to the methods below, so replacing the
            // instance (and thereby losing state such as DefaultRequestHeaders) is unnecessary.
            return client;
        }

        /// <summary>Sends a request using the controlled handler path.</summary>
        public static SystemTasks.Task<SystemHttpResponseMessage> SendAsync(SystemHttpClient client,
            SystemHttpRequestMessage request) =>
            SendAsync(client, request, SystemHttpCompletionOption.ResponseContentRead, default);

        /// <summary>Sends a cancellable request using the controlled handler path.</summary>
        public static SystemTasks.Task<SystemHttpResponseMessage> SendAsync(SystemHttpClient client,
            SystemHttpRequestMessage request, SystemCancellationToken cancellationToken) =>
            SendAsync(client, request, SystemHttpCompletionOption.ResponseContentRead, cancellationToken);

        /// <summary>Sends a request with the specified completion mode.</summary>
        public static SystemTasks.Task<SystemHttpResponseMessage> SendAsync(SystemHttpClient client,
            SystemHttpRequestMessage request, SystemHttpCompletionOption completionOption) =>
            SendAsync(client, request, completionOption, default);

        /// <summary>Sends a cancellable request with the specified completion mode.</summary>
        public static SystemTasks.Task<SystemHttpResponseMessage> SendAsync(SystemHttpClient client,
            SystemHttpRequestMessage request, SystemHttpCompletionOption completionOption,
            SystemCancellationToken cancellationToken)
        {
            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return client.SendAsync(request, completionOption, cancellationToken);
            }

            if (CheckRequestBeforeSendMethod is null ||
                PendingRequestsSourceField is null || HandlerField is null)
            {
                throw new PlatformNotSupportedException(
                    "The controlled HttpClient model does not support this System.Net.Http implementation.");
            }

            // Preserve the BCL's synchronous validation, one-send rule, default headers, base URI and
            // version policy.  Calling HttpClient.SendAsync itself would manufacture an uncontrolled
            // outer BCL task before the rewritten handler is reached.
            Invoke(CheckRequestBeforeSendMethod, client, request);
            return SendCoreAsync(client, request, completionOption, runtime, cancellationToken);
        }

        [System.Runtime.CompilerServices.AsyncMethodBuilder(typeof(Types.Runtime.CompilerServices.AsyncTaskMethodBuilder<>))]
        private static async SystemTasks.Task<SystemHttpResponseMessage> SendCoreAsync(SystemHttpClient client,
            SystemHttpRequestMessage request, SystemHttpCompletionOption completionOption,
            CoyoteRuntime runtime, SystemCancellationToken cancellationToken)
        {
            var pendingSource = (SystemCancellationTokenSource)PendingRequestsSourceField.GetValue(client);
            // Own the links explicitly: LinkedTokenSource.Dispose waits for callbacks on other
            // threads, including a callback currently paused by the controlled scheduler.
            var links = new CancellationLinks(cancellationToken, pendingSource.Token);
            var linkedSource = links.Source;
            var timeoutLifetime = new SystemCancellationTokenSource();
            SystemTask timeout = client.Timeout == System.Threading.Timeout.InfiniteTimeSpan ? SystemTask.CompletedTask :
                CancelOnTimeoutAsync(runtime, client.Timeout, links, timeoutLifetime.Token);
            SystemHttpResponseMessage response = null;
            try
            {
                var handler = (SystemHttpMessageHandler)HandlerField.GetValue(client);
                var handlerTask = HttpMessageInvoker.SendHandlerAsync(handler,
                    HttpRequestMessage.WithRuntimeHeaders(request), runtime, linkedSource.Token);
                response = await Types.Threading.Tasks.Task<SystemHttpResponseMessage>.ConfigureAwait(handlerTask, false);
                if (response is null)
                {
                    throw new InvalidOperationException("The handler did not return a response message.");
                }

                if (completionOption is SystemHttpCompletionOption.ResponseContentRead &&
                    request.Method != System.Net.Http.HttpMethod.Head)
                {
                    SystemTask buffering = response.Content.LoadIntoBufferAsync(
                        client.MaxResponseContentBufferSize, linkedSource.Token);
                    HttpMessageInvoker.ValidateSupportedTask(buffering, runtime, "HttpContent.LoadIntoBufferAsync");
                    await ControlledTask.ConfigureAwait(buffering, false);
                }

                return response;
            }
            catch (OperationCanceledException ex)
            {
                response?.Dispose();
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new SystemTasks.TaskCanceledException(ex.Message, ex, cancellationToken);
                }

                if (!pendingSource.IsCancellationRequested && linkedSource.IsCancellationRequested)
                {
                    throw new SystemTasks.TaskCanceledException("The configured HttpClient timeout elapsed.",
                        new TimeoutException(ex.Message, ex), ex.CancellationToken);
                }

                throw;
            }
            catch (System.Net.Http.HttpRequestException ex) when (linkedSource.IsCancellationRequested)
            {
                response?.Dispose();
                throw new SystemTasks.TaskCanceledException("The HTTP request was canceled.", ex,
                    cancellationToken.IsCancellationRequested ? cancellationToken : linkedSource.Token);
            }
            catch
            {
                response?.Dispose();
                throw;
            }
            finally
            {
                timeoutLifetime.Cancel();
                await ControlledTask.ConfigureAwait(timeout, false);
                timeoutLifetime.Dispose();
                links.Dispose();
            }
        }

        /// <summary>
        /// Cancels the request's links once the client timeout elapses, unless the request ended first.
        /// </summary>
        /// <remarks>
        /// The timeout is an idle-only deadline, like a <c>CancelAfter</c> budget: the clock never advances to it
        /// while work is enabled. As a racing delay it carried the clock past every shorter cancellation budget on
        /// the request path as soon as the request was sent, so requests were cancelled while their handlers were
        /// still making progress.
        /// </remarks>
        [System.Runtime.CompilerServices.AsyncMethodBuilder(typeof(Types.Runtime.CompilerServices.AsyncTaskMethodBuilder))]
        private static async SystemTask CancelOnTimeoutAsync(CoyoteRuntime runtime, TimeSpan timeout,
            CancellationLinks links, SystemCancellationToken lifetime)
        {
            try
            {
                await ControlledTask.ConfigureAwait(runtime.ScheduleDelay(timeout, lifetime, fireOnlyWhenIdle: true), false);
                if (!lifetime.IsCancellationRequested)
                {
                    links.Cancel();
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }

        private sealed class CancellationLinks : IDisposable
        {
            internal readonly SystemCancellationTokenSource Source = new SystemCancellationTokenSource();
            private readonly object Sync = new object();
            private readonly System.Threading.CancellationTokenRegistration Caller;
            private readonly System.Threading.CancellationTokenRegistration Pending;
            private int Active;
            private bool Retired;

            internal CancellationLinks(SystemCancellationToken caller, SystemCancellationToken pending)
            {
                this.Caller = caller.Register(this.Cancel);
                this.Pending = pending.Register(this.Cancel);
            }

            internal void Cancel()
            {
                lock (this.Sync)
                {
                    if (this.Retired)
                    {
                        return;
                    }

                    this.Active++;
                }

                try
                {
                    this.Source.Cancel();
                }
                finally
                {
                    lock (this.Sync)
                    {
                        if (--this.Active == 0 && this.Retired)
                        {
                            this.Source.Dispose();
                        }
                    }
                }
            }

            public void Dispose()
            {
                lock (this.Sync)
                {
                    if (this.Retired)
                    {
                        return;
                    }

                    this.Retired = true;
                    if (this.Active == 0)
                    {
                        this.Source.Dispose();
                    }
                }

                // An in-flight forwarding callback owns final disposal after Cancel returns.
                // Never wait for it: it may be paused behind this controlled operation.
                this.Caller.Unregister();
                this.Pending.Unregister();
            }
        }

        private static object Invoke(MethodInfo method, object instance, params object[] arguments)
        {
            try
            {
                return method.Invoke(instance, arguments);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }
}
#endif

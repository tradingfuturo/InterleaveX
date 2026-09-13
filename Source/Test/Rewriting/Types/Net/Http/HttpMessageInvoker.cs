// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#if NET
using System;
using System.Reflection;
using Microsoft.Coyote.Runtime;
using ControlledTask = Microsoft.Coyote.Rewriting.Types.Threading.Tasks.Task;
using SystemCancellationToken = System.Threading.CancellationToken;
using SystemHttpMessageHandler = System.Net.Http.HttpMessageHandler;
using SystemHttpMessageInvoker = System.Net.Http.HttpMessageInvoker;
using SystemHttpRequestMessage = System.Net.Http.HttpRequestMessage;
using SystemHttpResponseMessage = System.Net.Http.HttpResponseMessage;
using SystemTasks = System.Threading.Tasks;

namespace Microsoft.Coyote.Rewriting.Types.Net.Http
{
    /// <summary>Provides controlled HTTP message dispatch during systematic testing.</summary>
    /// <remarks>This type is intended for compiler use rather than use directly in code.</remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class HttpMessageInvoker
    {
        private static readonly MethodInfo HandlerSendAsyncMethod = typeof(SystemHttpMessageHandler).GetMethod(
            "SendAsync", BindingFlags.NonPublic | BindingFlags.Instance,
            null, new[] { typeof(SystemHttpRequestMessage), typeof(SystemCancellationToken) }, null);

        /// <summary>Dispatches the observed virtual SendAsync call to its controlled handler.</summary>
        public static SystemTasks.Task<SystemHttpResponseMessage> SendAsync(SystemHttpMessageInvoker invoker,
            SystemHttpRequestMessage request, SystemCancellationToken cancellationToken)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (invoker is System.Net.Http.HttpClient client)
            {
                return HttpClient.SendAsync(client, request, cancellationToken);
            }

            var runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return invoker.SendAsync(request, cancellationToken);
            }

            FieldInfo disposedField = typeof(SystemHttpMessageInvoker).GetField(
                "_disposed", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo handlerField = typeof(SystemHttpMessageInvoker).GetField(
                "_handler", BindingFlags.NonPublic | BindingFlags.Instance);
            if (disposedField is null || handlerField is null || HandlerSendAsyncMethod is null)
            {
                throw new PlatformNotSupportedException(
                    "The controlled HttpMessageInvoker model does not support this System.Net.Http implementation.");
            }

            if ((bool)disposedField.GetValue(invoker))
            {
                throw new ObjectDisposedException(nameof(SystemHttpMessageInvoker));
            }

            var handler = (SystemHttpMessageHandler)handlerField.GetValue(invoker);
            return SendHandlerAsync(handler, HttpRequestMessage.WithRuntimeHeaders(request), runtime, cancellationToken);
        }

        internal static SystemTasks.Task<SystemHttpResponseMessage> SendHandlerAsync(SystemHttpMessageHandler handler,
            SystemHttpRequestMessage request, CoyoteRuntime runtime, SystemCancellationToken cancellationToken)
        {
            SystemHttpMessageHandler leaf = handler;
            while (leaf is System.Net.Http.DelegatingHandler delegating)
            {
                leaf = delegating.InnerHandler;
            }

            MethodInfo implementation = leaf?.GetType().GetMethod("SendAsync", BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(SystemHttpRequestMessage), typeof(SystemCancellationToken) }, null);
            if (implementation is null || !RewritingEngine.IsAssemblyRewritten(implementation.DeclaringType.Assembly))
            {
                runtime.NotifyUncontrolledInvocation("HttpMessageHandler.SendAsync (unrewritten handler)");
                throw new NotSupportedException("Controlled HTTP requires a rewritten handler; real network handlers are unsupported.");
            }

            SystemTasks.Task<SystemHttpResponseMessage> task;
            try
            {
                task = (SystemTasks.Task<SystemHttpResponseMessage>)HandlerSendAsyncMethod.Invoke(
                    handler, new object[] { request, cancellationToken });
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }

            ValidateSupportedTask(task, runtime, "HttpMessageHandler.SendAsync");
            return task;
        }

        internal static void ValidateSupportedTask(SystemTasks.Task task, CoyoteRuntime runtime, string operation)
        {
            if (task is null)
            {
                throw new InvalidOperationException($"{operation} returned a null task.");
            }

            if (!task.IsCompleted && runtime.IsTaskUncontrolled(task))
            {
                runtime.CheckIfReturnedTaskIsUncontrolled(task, operation);
                throw new NotSupportedException(
                    $"Controlled HTTP supports only handlers whose asynchronous {operation} task is rewritten " +
                    "and controlled. Network and other externally-completing handlers are not supported.");
            }
        }
    }
}
#endif

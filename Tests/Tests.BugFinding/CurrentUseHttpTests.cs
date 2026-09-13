// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    public class CurrentUseHttpTests : BaseBugFindingTest
    {
        public CurrentUseHttpTests(ITestOutputHelper output)
            : base(output)
        {
        }

        private Configuration StrictConfiguration() => this.GetConfiguration()
            .WithPartiallyControlledConcurrencyAllowed(false)
            .WithPartiallyControlledDataNondeterminismAllowed(false)
            .WithSystematicFuzzingFallbackEnabled(false)
            .WithTestingIterations(100);

        [Fact(Timeout = 10000)]
        public void TestCanceledHttpRequestExceptionProducesCanceledTask()
        {
            this.Test(async () =>
            {
                using var cancellation = new CancellationTokenSource();
                using var client = new HttpClient(new TestHandler((_, _) =>
                {
                    cancellation.Cancel();
                    return Task.FromException<HttpResponseMessage>(new HttpRequestException("canceled transport"));
                }));
                Task<HttpResponseMessage> pending = client.SendAsync(
                    new HttpRequestMessage(HttpMethod.Get, "https://example.test/canceled-fault"), cancellation.Token);
                OperationCanceledException failure = await ExpectAsync<OperationCanceledException>(() => pending);
                Specification.Assert(pending.IsCanceled && failure.CancellationToken == cancellation.Token,
                    "A canceled HTTP failure lost cancellation task status or its caller token.");
            }, this.StrictConfiguration());
        }

        [Fact(Timeout = 5000)]
        public void TestSendAsyncSuspendedResponseCompletesAfterHandlerRelease()
        {
            this.Test(async () =>
            {
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var handler = new TestHandler(async (_, _) =>
                {
                    await Task.Yield();
                    await release.Task;
                    return Response("released");
                });
                using var client = new HttpClient(handler);

                Task<HttpResponseMessage> pending = client.SendAsync(
                    new HttpRequestMessage(HttpMethod.Get, "https://example.test/suspended"),
                    CancellationToken.None);
                Specification.Assert(!pending.IsCompleted, "SendAsync completed before the handler was released.");
                release.SetResult(true);

                using HttpResponseMessage response = await pending;
                Specification.Assert(response.StatusCode == HttpStatusCode.OK &&
                    await response.Content.ReadAsStringAsync() == "released",
                    "SendAsync did not return the released handler response.");
            }, configuration: this.StrictConfiguration());
        }

        [Fact(Timeout = 5000)]
        public void TestSendAsyncPreservesPreparedRequestDefaultsAndBody()
        {
            this.Test(async () =>
            {
                var handler = new TestHandler((request, _) =>
                {
                    return Task.FromResult(Response(request.RequestUri!.ToString()));
                });
                using var client = new HttpClient(handler)
                {
                    BaseAddress = new Uri("https://example.test/api/"),
                };
                client.DefaultRequestHeaders.Add("X-Test-Default", "present");

                using var request = new HttpRequestMessage(HttpMethod.Post, "items")
                {
                    Content = new StringContent("payload"),
                };
                using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);

                string body = await request.Content.ReadAsStringAsync();
                Specification.Assert(handler.Request?.RequestUri == new Uri("https://example.test/api/items") &&
                    handler.Request.Headers.Contains("X-Test-Default") && body == "payload" &&
                    await response.Content.ReadAsStringAsync() == "https://example.test/api/items",
                    "HttpClient did not preserve request URI, default headers, or content at the handler boundary.");
            }, configuration: this.StrictConfiguration());
        }

        [Fact(Timeout = 5000)]
        public void TestSendAsyncPropagatesCallerCancellation()
        {
            this.Test(async () =>
            {
                using var callerCancellation = new CancellationTokenSource();
                bool handlerObservedCancellation = false;
                var handler = new TestHandler(async (_, token) =>
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        handlerObservedCancellation = true;
                        throw;
                    }

                    throw new InvalidOperationException("Cancellation did not interrupt the handler.");
                });
                using var client = new HttpClient(handler);
                Task<HttpResponseMessage> pending = client.SendAsync(
                    new HttpRequestMessage(HttpMethod.Get, "https://example.test/cancel"),
                    callerCancellation.Token);
                callerCancellation.Cancel();

                await ExpectAsync<OperationCanceledException>(() => pending);
                Specification.Assert(pending.IsCanceled, "Caller cancellation must produce a canceled task.");
                Specification.Assert(handlerObservedCancellation,
                    "The handler did not observe the caller cancellation token.");
            }, configuration: this.StrictConfiguration());
        }

        [Fact(Timeout = 5000)]
        public void TestSendAsyncClientDisposeCancelsInFlightHandler()
        {
            this.Test(async () =>
            {
                bool disposed = false;
                var handler = new TestHandler(async (_, token) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    throw new InvalidOperationException("Disposed handler completed unexpectedly.");
                }, () =>
                {
                    disposed = true;
                });
                var client = new HttpClient(handler);
                Task<HttpResponseMessage> pending = client.SendAsync(
                    new HttpRequestMessage(HttpMethod.Get, "https://example.test/dispose"),
                    CancellationToken.None);
                client.Dispose();

                Specification.Assert(disposed, "Disposing HttpClient did not dispose its handler.");
                await ExpectAsync<OperationCanceledException>(() => pending);
                Specification.Assert(pending.IsCanceled, "Client disposal must produce a canceled task.");
            }, configuration: this.StrictConfiguration());
        }

        [Fact(Timeout = 5000)]
        public void TestSendAsyncTimeoutSurfacesTaskCanceledWithTimeoutCause()
        {
            this.Test(async () =>
            {
                var handler = new TestHandler(async (_, token) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    throw new InvalidOperationException("Timeout did not interrupt the handler.");
                });
                using var client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromMilliseconds(20),
                };

                TaskCanceledException exception = await ExpectAsync<TaskCanceledException>(() => client.SendAsync(
                    new HttpRequestMessage(HttpMethod.Get, "https://example.test/timeout"),
                    CancellationToken.None));
                Specification.Assert(exception.InnerException is TimeoutException,
                    "HttpClient timeout did not preserve a TimeoutException cause.");
            }, configuration: this.StrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        public void TestSendAsyncTimeoutDoesNotExpireWhileTheHandlerIsProgressing()
        {
            // The timeout is an idle-only deadline. As a racing delay the clock could take it at any step, so a
            // handler still making progress was cancelled - and it carried the clock past every shorter
            // cancellation budget on the request path along the way.
            this.Test(async () =>
            {
                var handler = new TestHandler(async (_, token) =>
                {
                    for (int i = 0; i < 20; i++)
                    {
                        // A real handler observes its token between steps; that is how an expired timeout would
                        // surface here.
                        token.ThrowIfCancellationRequested();
                        await Task.Yield();
                    }

                    return Response("ok");
                });
                using var client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromMilliseconds(20),
                };

                Task sibling = Task.Run(async () =>
                {
                    for (int i = 0; i < 20; i++)
                    {
                        await Task.Yield();
                    }
                });

                using HttpResponseMessage response = await client.SendAsync(
                    new HttpRequestMessage(HttpMethod.Get, "https://example.test/progressing"),
                    CancellationToken.None);
                await sibling;
                Specification.Assert(response.IsSuccessStatusCode,
                    "The HttpClient timeout expired while the handler was still making progress.");
            }, configuration: this.StrictConfiguration());
        }

        [Fact(Timeout = 5000)]
        public void TestSendAsyncPreservesResponseFaultAndLifecycleRejections()
        {
            this.Test(async () =>
            {
                var handler = new TestHandler((_, _) =>
                    Task.FromException<HttpResponseMessage>(new InvalidOperationException("handler fault")));
                using var client = new HttpClient(handler);
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/fault");
                await ExpectAsync<InvalidOperationException>(() => client.SendAsync(request, CancellationToken.None));

                handler.Callback = (_, _) => Task.FromResult(Response("ok"));
                using HttpResponseMessage response = await client.SendAsync(
                    new HttpRequestMessage(HttpMethod.Get, "https://example.test/reuse"), CancellationToken.None);
                Specification.Assert(response.StatusCode == HttpStatusCode.OK && handler.Calls == 2,
                    "HttpClient did not preserve a successful handler response after a prior fault.");

                using var duplicate = new HttpRequestMessage(HttpMethod.Get, "https://example.test/duplicate");
                using HttpResponseMessage first = await client.SendAsync(duplicate, CancellationToken.None);
                await ExpectAsync<InvalidOperationException>(() => client.SendAsync(duplicate, CancellationToken.None));

                client.Dispose();
                Assert.Throws<ObjectDisposedException>(() =>
                {
                    _ = client.SendAsync(
                        new HttpRequestMessage(HttpMethod.Get, "https://example.test/disposed"), CancellationToken.None);
                });
            }, configuration: this.StrictConfiguration());
        }

        private static async Task<TException> ExpectAsync<TException>(Func<Task> action)
            where TException : Exception
        {
            try
            {
                await action();
            }
            catch (TException exception)
            {
                return exception;
            }

            throw new InvalidOperationException("Expected " + typeof(TException).Name);
        }

        private static HttpResponseMessage Response(string content) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content),
        };

        private sealed class TestHandler : HttpMessageHandler
        {
            private readonly Action OnDispose;

            internal TestHandler(
                Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback,
                Action onDispose = null)
            {
                this.Callback = callback;
                this.OnDispose = onDispose;
            }

            internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Callback { get; set; }

            internal HttpRequestMessage Request { get; private set; }

            internal int Calls { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                this.Request = request;
                this.Calls++;
                return this.Callback(request, cancellationToken);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    this.OnDispose?.Invoke();
                }

                base.Dispose(disposing);
            }
        }
    }
}

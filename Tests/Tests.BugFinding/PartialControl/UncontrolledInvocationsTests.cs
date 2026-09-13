// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// Covers how an invocation the rewriter does not control is reported and tolerated.
    /// </summary>
    /// <remarks>
    /// These tests need an API that is genuinely uncontrolled. <c>Task.ContinueWith</c> and
    /// <c>System.Threading.Timer</c> used to be the examples; both are modelled now, so the thread pool, which remains
    /// uncontrolled by design, stands in for them.
    /// </remarks>
    public class UncontrolledInvocationsTests : BaseBugFindingTest
    {
        public UncontrolledInvocationsTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestUncontrolledThreadPoolInvocation()
        {
            this.Test(() =>
            {
                ThreadPool.QueueUserWorkItem(_ => Console.WriteLine("Hello!"));
            },
            configuration: this.GetConfiguration()
                .WithPartiallyControlledConcurrencyAllowed()
                .WithTestingIterations(10));
        }

        [Fact(Timeout = 5000)]
        public void TestUncontrolledThreadPoolCallbackWithLock()
        {
            this.Test(async () =>
            {
                var lockObj = new object();
                var tcs = new TaskCompletionSource<bool>();
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    // Thread pool callbacks run on uncontrolled threads. Acquiring a lock
                    // inside the callback must not cause a NullReferenceException
                    // when the rewritten Monitor.Enter falls back to native locking.
                    lock (lockObj)
                    {
                        tcs.TrySetResult(true);
                    }
                });
                await tcs.Task;
            },
            configuration: this.GetConfiguration()
                .WithPartiallyControlledConcurrencyAllowed()
                .WithTestingIterations(10));
        }

        [Fact(Timeout = 5000)]
        public void TestUncontrolledThreadPoolInvocationWithNoPartialControl()
        {
            this.TestWithError(() =>
            {
                ThreadPool.QueueUserWorkItem(_ => Console.WriteLine("Hello!"));
            },
            errorChecker: (e) =>
            {
                var expectedMethodName = GetFullyQualifiedMethodName(typeof(ThreadPool), nameof(ThreadPool.QueueUserWorkItem));
                Assert.StartsWith($"Invoking '{expectedMethodName}' is not intercepted", e);
            });
        }
    }
}

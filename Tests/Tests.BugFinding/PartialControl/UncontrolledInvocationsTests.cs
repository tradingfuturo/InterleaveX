// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    public class UncontrolledInvocationsTests : BaseBugFindingTest
    {
        public UncontrolledInvocationsTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestUncontrolledContinueWithTaskInvocation()
        {
            this.Test(() =>
            {
                var task = new Task(() => { });
                task.ContinueWith(_ => { }, TaskScheduler.Current);
            },
            configuration: this.GetConfiguration()
                .WithPartiallyControlledConcurrencyAllowed()
                .WithTestingIterations(10));
        }

        [Fact(Timeout = 5000)]
        public void TestUncontrolledContinueWithTaskInvocationWithNoPartialControl()
        {
            this.TestWithError(() =>
            {
                var task = new Task(() => { });
                task.ContinueWith(_ => { }, TaskScheduler.Current);
            },
            errorChecker: (e) =>
            {
                var expectedMethodName = GetFullyQualifiedMethodName(typeof(Task), nameof(Task.ContinueWith));
                Assert.StartsWith($"Invoking '{expectedMethodName}' is not intercepted", e);
            });
        }

        [Fact(Timeout = 5000)]
        public void TestUncontrolledTimerInvocation()
        {
            this.Test(() =>
            {
                using var timer = new Timer(_ => Console.WriteLine("Hello!"), null, 1, 0);
            },
            configuration: this.GetConfiguration()
                .WithPartiallyControlledConcurrencyAllowed()
                .WithTestingIterations(10));
        }

        [Fact(Timeout = 5000)]
        public void TestUncontrolledTimerCallbackWithLock()
        {
            this.Test(async () =>
            {
                var lockObj = new object();
                var tcs = new TaskCompletionSource<bool>();
                using var timer = new Timer(_ =>
                {
                    // Timer callbacks run on uncontrolled threads. Acquiring a lock
                    // inside the callback must not cause a NullReferenceException
                    // when the rewritten Monitor.Enter falls back to native locking.
                    lock (lockObj)
                    {
                        tcs.TrySetResult(true);
                    }
                }, null, 1, Timeout.Infinite);
                await tcs.Task;
            },
            configuration: this.GetConfiguration()
                .WithPartiallyControlledConcurrencyAllowed()
                .WithTestingIterations(10));
        }

        [Fact(Timeout = 5000)]
        public void TestUncontrolledTimerInvocationWithNoPartialControl()
        {
            this.TestWithError(() =>
            {
                using var timer = new Timer(_ => Console.WriteLine("Hello!"), null, 1, 0);
            },
            errorChecker: (e) =>
            {
                var expectedMethodName = GetFullyQualifiedMethodName(typeof(Timer), ".ctor");
                Assert.StartsWith($"Invoking '{expectedMethodName}' is not intercepted", e);
            });
        }
    }
}

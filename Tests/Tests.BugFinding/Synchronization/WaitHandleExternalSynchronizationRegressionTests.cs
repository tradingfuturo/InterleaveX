// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Threading;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;
using ControlledWaitHandle = Microsoft.Coyote.Rewriting.Types.Threading.WaitHandle;

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// Red shields for a raw CLR thread waiting on a handle modeled by the active runtime.
    /// </summary>
    public class WaitHandleExternalSynchronizationRegressionTests : BaseBugFindingTest
    {
        public WaitHandleExternalSynchronizationRegressionTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestUncontrolledWaitOneRegistersAndCompletesWhenSignaled()
        {
            this.Test(() =>
            {
                using var signal = new AutoResetEvent(false);
                using var registered = new ManualResetEventSlim(false);
                ControlledWaitHandle.SetWaitRegistrationCallbackForTesting(signal, registered.Set);
                bool result = false;
                var thread = UncontrolledThreadRunner.Start(() => result = signal.WaitOne());

                try
                {
                    WaitUntil(() => registered.IsSet || thread.IsCompleted);
                    Specification.Assert(registered.IsSet,
                        "An uncontrolled WaitOne did not register with the modeled wait handle.");
                    signal.Set();
                    thread.Join();
                }
                finally
                {
                    signal.Set();
                    thread.Join();
                }

                thread.ThrowIfFailed();
                Specification.Assert(result, "The externally registered WaitOne did not complete after Set.");
            }, this.GetConfiguration().WithPartiallyControlledConcurrencyAllowed().WithTestingIterations(1));
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestUncontrolledWaitAllRegistersAndCompletesWhenAllHandlesAreSignaled()
        {
            this.Test(() =>
            {
                using var first = new AutoResetEvent(false);
                using var second = new AutoResetEvent(false);
                using var registered = new ManualResetEventSlim(false);
                ControlledWaitHandle.SetWaitRegistrationCallbackForTesting(first, registered.Set);
                bool result = false;
                var thread = UncontrolledThreadRunner.Start(() =>
                    result = WaitHandle.WaitAll(new WaitHandle[] { first, second }));

                try
                {
                    WaitUntil(() => registered.IsSet || thread.IsCompleted);
                    Specification.Assert(registered.IsSet,
                        "An uncontrolled WaitAll did not register with the modeled wait handles.");
                    first.Set();
                    second.Set();
                    thread.Join();
                }
                finally
                {
                    first.Set();
                    second.Set();
                    thread.Join();
                }

                thread.ThrowIfFailed();
                Specification.Assert(result, "The externally registered WaitAll did not complete after both Sets.");
            }, this.GetConfiguration().WithPartiallyControlledConcurrencyAllowed().WithTestingIterations(1));
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestUncontrolledWaitAnyRegistersAndCompletesWhenOneHandleIsSignaled()
        {
            this.Test(() =>
            {
                using var first = new AutoResetEvent(false);
                using var second = new AutoResetEvent(false);
                using var registered = new ManualResetEventSlim(false);
                ControlledWaitHandle.SetWaitRegistrationCallbackForTesting(first, registered.Set);
                int result = WaitHandle.WaitTimeout;
                var thread = UncontrolledThreadRunner.Start(() =>
                    result = WaitHandle.WaitAny(new WaitHandle[] { first, second }));

                try
                {
                    WaitUntil(() => registered.IsSet || thread.IsCompleted);
                    Specification.Assert(registered.IsSet,
                        "An uncontrolled WaitAny did not register with the modeled wait handles.");
                    second.Set();
                    thread.Join();
                }
                finally
                {
                    first.Set();
                    second.Set();
                    thread.Join();
                }

                thread.ThrowIfFailed();
                Specification.Assert(result is 1,
                    "The externally registered WaitAny did not report the signaled handle index.");
            }, this.GetConfiguration().WithPartiallyControlledConcurrencyAllowed().WithTestingIterations(1));
        }

        private static void WaitUntil(Func<bool> condition)
        {
            for (int step = 0; step < 200; step++)
            {
                if (condition())
                {
                    return;
                }

                SchedulingPoint.Interleave();
            }

            Assert.True(condition(), "The bounded synchronization observation did not occur.");
        }
    }
}

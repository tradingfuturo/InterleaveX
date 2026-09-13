// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// Covers the <see cref="ManualResetEventSlim"/> model. The strict configuration refuses partial control, so an
    /// unmodelled event would be reported as an uncontrolled invocation by every test here.
    /// </summary>
    public class ManualResetEventSlimTests : BaseBugFindingTest
    {
        public ManualResetEventSlimTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestSetResetAndZeroTimeoutWaits()
        {
            this.Test(() =>
            {
                using var evt = new ManualResetEventSlim(false);
                Specification.Assert(!evt.Wait(0), "An unset event reported that it was set.");
                evt.Set();
                Specification.Assert(evt.IsSet && evt.Wait(0) && evt.Wait(TimeSpan.Zero),
                    "A set event did not report that it was set.");
                evt.Reset();
                Specification.Assert(!evt.IsSet && !evt.Wait(0), "A reset event still reported that it was set.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestWaitBlocksUntilAnotherOperationSetsTheEvent()
        {
            this.Test(async () =>
            {
                using var evt = new ManualResetEventSlim(false);
                bool isSet = false;
                Task setter = Task.Run(() =>
                {
                    isSet = true;
                    evt.Set();
                });

                evt.Wait();
                Specification.Assert(isSet, "Wait returned before the event was set.");
                await setter;
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestWaitOnAnEventNothingSetsIsADeadlock()
        {
            this.TestWithError(() =>
            {
                using var evt = new ManualResetEventSlim(false);
                evt.Wait();
            },
            errorChecker: (e) => Assert.StartsWith("Deadlock detected.", e),
            configuration: this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestFiniteWaitTimesOutInVirtualTime()
        {
            this.Test(() =>
            {
                using var evt = new ManualResetEventSlim(false);
                CoyoteRuntime runtime = CoyoteRuntime.Current;
                long start = runtime.GetVirtualTimeTicksForTesting();
                Specification.Assert(!evt.Wait(TimeSpan.FromHours(1)), "A wait on an unset event did not time out.");
                Specification.Assert(runtime.GetVirtualTimeTicksForTesting() - start == TimeSpan.FromHours(1).Ticks,
                    "The timeout did not expire at its virtual deadline.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestFiniteWaitReportsSetOnlyWhenSetAndTimeoutOnlyAtItsDeadline()
        {
            // The virtual clock may be advanced while other operations are still enabled, as it is for every modelled
            // finite wait, so either outcome is legitimate here. What must hold is that each outcome is honest.
            this.Test(async () =>
            {
                using var evt = new ManualResetEventSlim(false);
                CoyoteRuntime runtime = CoyoteRuntime.Current;
                long start = runtime.GetVirtualTimeTicksForTesting();
                Task setter = Task.Run(() => evt.Set());
                bool result = evt.Wait(TimeSpan.FromHours(1));
                long elapsed = runtime.GetVirtualTimeTicksForTesting() - start;
                Specification.Assert(result ? evt.IsSet : elapsed >= TimeSpan.FromHours(1).Ticks,
                    "The wait returned {0} after {1} virtual ticks.", result, elapsed);
                await setter;
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestWaitThrowsWhenAnotherOperationCancelsIt()
        {
            this.Test(async () =>
            {
                using var evt = new ManualResetEventSlim(false);
                using var cancellation = new CancellationTokenSource();
                Task canceller = Task.Run(() => cancellation.Cancel());

                OperationCanceledException failure = null;
                try
                {
                    evt.Wait(cancellation.Token);
                }
                catch (OperationCanceledException ex)
                {
                    failure = ex;
                }

                Specification.Assert(failure != null && failure.CancellationToken == cancellation.Token,
                    "A canceled wait did not throw OperationCanceledException for its token.");
                await canceller;
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestDisposedEventRejectsWaits()
        {
            this.Test(() =>
            {
                var evt = new ManualResetEventSlim(false);
                evt.Dispose();
                bool threw = false;
                try
                {
                    evt.Wait(0);
                }
                catch (ObjectDisposedException)
                {
                    threw = true;
                }

                Specification.Assert(threw, "A wait on a disposed event did not throw ObjectDisposedException.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestWaitHandleTakesPartInAModelledWaitAny()
        {
            this.Test(async () =>
            {
                using var evt = new ManualResetEventSlim(false);
                using var other = new ManualResetEvent(false);
                Task setter = Task.Run(() => evt.Set());
                int index = WaitHandle.WaitAny(new WaitHandle[] { other, evt.WaitHandle });
                Specification.Assert(index is 1, "WaitAny over the event's wait handle returned {0}.", index);
                await setter;
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestWaitHandleFollowsSetAndReset()
        {
            this.Test(() =>
            {
                using var evt = new ManualResetEventSlim(true);
                WaitHandle handle = evt.WaitHandle;
                Specification.Assert(ReferenceEquals(handle, evt.WaitHandle), "The event handed out two wait handles.");
                Specification.Assert(handle.WaitOne(0), "The wait handle of a set event was not signaled.");
                evt.Reset();
                Specification.Assert(!handle.WaitOne(0), "The wait handle stayed signaled after Reset.");
                evt.Set();
                Specification.Assert(handle.WaitOne(0), "The wait handle was not signaled after Set.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestWaiterCanResumeBeforeTheSetterContinues()
        {
            // A witness that the wait is interleaved with the operation that sets the event.
            this.TestWithError(async () =>
            {
                using var evt = new ManualResetEventSlim(false);
                bool setterContinued = false;
                Task setter = Task.Run(async () =>
                {
                    evt.Set();
                    await Task.Yield();
                    setterContinued = true;
                });

                evt.Wait();
                Specification.Assert(setterContinued, "The waiter resumed before the setter continued.");
                await setter;
            },
            errorChecker: (e) => Assert.StartsWith("The waiter resumed before the setter continued.", e),
            configuration: this.GetStrictConfiguration());
        }

        private Configuration GetStrictConfiguration() => this.GetConfiguration()
            .WithTestingIterations(100)
            .WithPartiallyControlledConcurrencyAllowed(false)
            .WithPartiallyControlledDataNondeterminismAllowed(false)
            .WithSystematicFuzzingFallbackEnabled(false);
    }
}

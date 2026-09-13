// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET8_0_OR_GREATER
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// Covers delegates created from a method group of a modelled static method. The compiler loads such a delegate's
    /// target with <c>ldftn</c> rather than calling it, and a rewriter that only redirects calls left the delegate
    /// pointing at the framework method: <c>Func&lt;TimeSpan, CancellationToken, Task&gt; delay = Task.Delay</c> then
    /// ran every invocation on a real timer.
    /// </summary>
    public class CurrentUseMethodGroupDelegateTests : BaseBugFindingTest
    {
        public CurrentUseMethodGroupDelegateTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestDelayMethodGroupDelegateIsControlled()
        {
            this.Test(async () =>
            {
                Func<TimeSpan, CancellationToken, Task> delay = Task.Delay;
                bool elapsed = false;
                Task pause = delay(TimeSpan.FromMinutes(5), CancellationToken.None)
                    .ContinueWith(_ => elapsed = true, TaskScheduler.Default);
                await pause;
                Specification.Assert(elapsed, "The continuation of a method-group delay did not run.");
            }, this.GetStrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestRunMethodGroupDelegateIsControlled()
        {
            this.Test(async () =>
            {
                Func<Action, Task> run = Task.Run;
                int value = 0;
                await run(() => value = 1);
                Specification.Assert(value is 1, "A method-group Task.Run did not run its work.");
            }, this.GetStrictConfiguration());
        }

        private Configuration GetStrictConfiguration() => this.GetConfiguration()
            .WithTestingIterations(50)
            .WithPartiallyControlledConcurrencyAllowed(false)
            .WithPartiallyControlledDataNondeterminismAllowed(false)
            .WithSystematicFuzzingFallbackEnabled(false);
    }
}
#endif

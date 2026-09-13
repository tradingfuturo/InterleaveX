// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET8_0_OR_GREATER
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// Covers reads of <see cref="SynchronizationContext.Current"/>, which diagnostics use to detect a UI thread and
    /// which the rewriter once reported as an uncontrolled invocation even though a read schedules nothing.
    /// </summary>
    public class CurrentUseSynchronizationContextTests : BaseBugFindingTest
    {
        public CurrentUseSynchronizationContextTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestReadingTheCurrentContextIsNotAnUncontrolledInvocation()
        {
            this.Test(async () =>
            {
                bool isUiContext = false;
                Task reader = Task.Run(() =>
                {
                    SynchronizationContext context = SynchronizationContext.Current;
                    isUiContext = context is not null && context.GetType().Name.Contains("Dispatcher");
                });

                await reader;
                Specification.Assert(!isUiContext, "A controlled operation reported a UI synchronization context.");
            },
            configuration: this.GetConfiguration()
                .WithTestingIterations(20)
                .WithPartiallyControlledConcurrencyAllowed(false)
                .WithPartiallyControlledDataNondeterminismAllowed(false)
                .WithSystematicFuzzingFallbackEnabled(false));
        }
    }
}
#endif

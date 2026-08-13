// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET8_0_OR_GREATER
using System;
using System.Threading;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    public class PeriodicTimerTimeProviderRegressionTests : BaseBugFindingTest
    {
        public PeriodicTimerTimeProviderRegressionTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestInterleavingOwnsCustomTimeProviderCadence()
        {
            this.Test(() =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), new ThrowingTimeProvider());
                Specification.Assert(timer.Period == TimeSpan.FromSeconds(1), "The model lost the configured period.");
            });
        }

        private sealed class ThrowingTimeProvider : TimeProvider
        {
            public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period) =>
                throw new InvalidOperationException("The controlled constructor invoked TimeProvider.CreateTimer.");
        }
    }
}
#endif

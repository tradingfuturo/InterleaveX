// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET
using System;
using System.Threading;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    public class PeriodicTimerPeriodContractRegressionTests : BaseBugFindingTest
    {
        public PeriodicTimerPeriodContractRegressionTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestPositiveSubMillisecondPeriodIsRejected()
        {
            this.Test(() =>
            {
                bool threw = false;
                try
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromTicks(1));
                }
                catch (ArgumentOutOfRangeException)
                {
                    threw = true;
                }

                Specification.Assert(threw, "A positive sub-millisecond period was accepted.");
            });
        }

        [Fact(Timeout = 5000)]
        public void TestDisposedSetterPublishesValueThenThrows()
        {
            this.Test(() =>
            {
                var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                timer.Dispose();
                TimeSpan replacement = TimeSpan.FromSeconds(2);
                bool threw = false;
                try
                {
                    timer.Period = replacement;
                }
                catch (ObjectDisposedException)
                {
                    threw = true;
                }

                Specification.Assert(threw, "Setting Period after disposal did not throw.");
                Specification.Assert(timer.Period == replacement, "The validated value was not published before the exception.");
            });
        }
    }
}
#endif

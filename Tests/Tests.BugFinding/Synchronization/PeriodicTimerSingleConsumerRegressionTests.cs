// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    public class PeriodicTimerSingleConsumerRegressionTests : BaseBugFindingTest
    {
        public PeriodicTimerSingleConsumerRegressionTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestOverlappingWaitIsRejectedUntilFirstResultIsConsumed()
        {
            this.Test(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
                ValueTask<bool> first = timer.WaitForNextTickAsync(CancellationToken.None);
                bool threw = false;
                try
                {
                    _ = timer.WaitForNextTickAsync(CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                    threw = true;
                }

                Specification.Assert(threw, "A second active wait was accepted.");
                timer.Dispose();
                Specification.Assert(!await first, "Disposal did not complete the active wait as false.");
            }, this.GetConfiguration().WithTestingIterations(20));
        }

        [Fact(Timeout = 5000)]
        public void TestDisposedTimerStillRejectsASecondActiveWait()
        {
            this.Test(async () =>
            {
                var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
                ValueTask<bool> first = timer.WaitForNextTickAsync(CancellationToken.None);
                timer.Dispose();

                bool threw = false;
                try
                {
                    _ = timer.WaitForNextTickAsync(CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                    threw = true;
                }

                Specification.Assert(threw, "Disposal allowed a second wait before the first was consumed.");
                Specification.Assert(!await first, "Disposal did not complete the first wait as false.");
            });
        }

        [Fact(Timeout = 5000)]
        public void TestPreCanceledTokenStillRejectsASecondActiveWait()
        {
            this.Test(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
                ValueTask<bool> first = timer.WaitForNextTickAsync(CancellationToken.None);
                using var source = new CancellationTokenSource();
                source.Cancel();

                bool threw = false;
                try
                {
                    _ = timer.WaitForNextTickAsync(source.Token);
                }
                catch (InvalidOperationException)
                {
                    threw = true;
                }

                Specification.Assert(threw, "A pre-canceled token bypassed the active-wait check.");
                timer.Dispose();
                Specification.Assert(!await first, "Disposal did not complete the first wait as false.");
            });
        }
    }
}
#endif

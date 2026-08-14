// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading.Tasks;
using Microsoft.Coyote.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests.SystematicFuzzing
{
    public class TaskDelayTests : Tests.TaskDelayTests
    {
        public TaskDelayTests(ITestOutputHelper output)
            : base(output)
        {
        }

        private protected override SchedulingPolicy SchedulingPolicy => SchedulingPolicy.Fuzzing;

        protected override Configuration GetConfiguration()
        {
            return base.GetConfiguration().WithSystematicFuzzingEnabled();
        }

        [Fact(Timeout = 5000)]
        public void TestLargeTimeSpanDelayUsesConfiguredFuzzingBound()
        {
            this.Test(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds((double)int.MaxValue + 1));
            }, configuration: this.GetConfiguration()
                .WithTestingIterations(200)
                .WithRandomGeneratorSeed(0)
                .WithMaxFuzzingDelay(1));
        }
    }
}

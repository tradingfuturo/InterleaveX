// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Testing.Fuzzing;
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

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestInfiniteDelayWaitsForCancellation()
        {
            this.Test(async () =>
            {
                using var cancellation = new CancellationTokenSource();
                Task delay = Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
                Assert.False(delay.IsCompleted);
                cancellation.Cancel();
                await Assert.ThrowsAsync<TaskCanceledException>(() => delay);
            }, configuration: this.GetConfiguration()
                .WithTestingIterations(1)
                .WithRandomGeneratorSeed(0));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestBoundedRandomStrategyHonorsHardDelayMaximum()
        {
            foreach (int maximum in new[] { 0, 1, 9, 10, 11, int.MaxValue })
            {
                var configuration = Configuration.Create().WithRandomGeneratorSeed(0);
                var strategy = new BoundedRandomStrategy(configuration)
                {
                    RandomValueGenerator = new RandomValueGenerator(configuration)
                };
                strategy.InitializeNextIteration(0);
                AssertStrategyHonorsMaximum(strategy, maximum);
            }
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestPrioritizationStrategyHonorsHardDelayMaximum()
        {
            foreach (int maximum in new[] { 0, 1, 4, 5, 7, int.MaxValue })
            {
                var configuration = Configuration.Create()
                    .WithRandomGeneratorSeed(0)
                    .WithPrioritizationStrategy(false, 10);
                var strategy = new PrioritizationStrategy(configuration)
                {
                    RandomValueGenerator = new RandomValueGenerator(configuration)
                };
                AssertStrategyHonorsMaximum(strategy, maximum);
            }
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestMaximumUnsignedFuzzingDelayDoesNotOverflow()
        {
            this.Test(() => SchedulingPoint.Interleave(), configuration: this.GetConfiguration()
                .WithTestingIterations(1)
                .WithRandomGeneratorSeed(0)
                .WithMaxFuzzingDelay(uint.MaxValue));
        }

        private static void AssertStrategyHonorsMaximum(FuzzingStrategy strategy, int maximum)
        {
            bool observedDelay = false;
            for (uint iteration = 0; iteration < 10; iteration++)
            {
                strategy.InitializeNextIteration(iteration);
                for (int attempt = 0; attempt < 2000; attempt++)
                {
                    Assert.True(strategy.GetNextDelay(null, maximum, out int next));
                    Assert.InRange(next, 0, maximum);
                    observedDelay |= next > 0;
                }
            }

            Assert.Equal(maximum > 0, observedDelay);
        }
    }
}

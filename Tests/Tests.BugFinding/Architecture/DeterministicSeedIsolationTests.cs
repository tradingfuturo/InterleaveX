// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System.Collections.Generic;
using Microsoft.Coyote.Tests.Common.Architecture;

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// Tests that every test in this assembly explores from a seed it can be re-run with.
    /// </summary>
    public class DeterministicSeedIsolationTests : DeterministicSeedIsolationTestsBase
    {
        /// <inheritdoc/>
        protected override string AssemblyFileName => "Microsoft.Coyote.Tests.BugFinding.dll";

        /// <inheritdoc/>
        protected override IReadOnlyList<string> AllowedToBuildAnEngine => new[]
        {
            // Through 'GetQLearningConfiguration', which pins a seed the assertions depend on.
            "Microsoft.Coyote.BugFinding.Tests.QLearningStrategyTests::ExploreTraces",

            // Pins a seed inline; both arms run under it so that the gate is the only difference.
            "Microsoft.Coyote.BugFinding.Tests.SchedulerHashingPolicyTests::RunPortfolio",

            // Through 'WithDefaultRandomSeed', so it gets the same per-test seed as everything that
            // goes through the base class. This is the one that was missing it.
            "Microsoft.Coyote.BugFinding.Tests.SchedulerHashingPolicyTests::" +
                "TestRegisteredStateHashingFunctionRunsOnEveryIteration",

            // Pins a seed inline: the golden digests beside it are what that seed produces.
            "Microsoft.Coyote.BugFinding.Tests.SchedulingDeterminismTests::ComputeDigest",

            // Through 'GetProbeConfiguration', which pins one. A skipped probe rather than a test.
            "Microsoft.Coyote.BugFinding.Tests.SchedulingThroughputProbe::Measure"
        };
    }
}

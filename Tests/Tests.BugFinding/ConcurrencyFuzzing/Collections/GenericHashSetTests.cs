// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using Microsoft.Coyote.Runtime;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests.SystematicFuzzing
{
    /// <summary>
    /// Runs the hash set race tests against genuinely parallel threads rather than an explored
    /// schedule. See <see cref="GenericListTests"/> for why these counterparts exist.
    /// </summary>
    public class GenericHashSetTests : DataRaceChecking.GenericHashSetTests
    {
        public GenericHashSetTests(ITestOutputHelper output)
                : base(output)
        {
        }

        private protected override SchedulingPolicy SchedulingPolicy => SchedulingPolicy.Fuzzing;

        protected override Configuration GetConfiguration()
        {
            return base.GetConfiguration().WithSystematicFuzzingEnabled();
        }
    }
}

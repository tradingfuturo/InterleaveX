// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using Microsoft.Coyote.Runtime;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests.SystematicFuzzing
{
    /// <summary>
    /// Runs the guarded-window tests against genuinely parallel threads rather than an explored
    /// schedule. See <see cref="GenericListTests"/> for why these counterparts exist.
    /// </summary>
    /// <remarks>
    /// These are the tests that most need a fuzzing counterpart: what they pin is that the guard spans
    /// the real operation, and under fuzzing the overlap they provoke is a real one between real
    /// threads rather than one the scheduler was asked to produce.
    /// </remarks>
    public class GenericCollectionGuardedWindowTests : DataRaceChecking.GenericCollectionGuardedWindowTests
    {
        public GenericCollectionGuardedWindowTests(ITestOutputHelper output)
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

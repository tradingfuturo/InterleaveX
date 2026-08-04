// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using Microsoft.Coyote.Runtime;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests.SystematicFuzzing
{
    /// <summary>
    /// Runs the list race tests against genuinely parallel threads rather than an explored schedule.
    /// </summary>
    /// <remarks>
    /// There was no fuzzing counterpart to any of the collection race suites, which is how the modelling
    /// of collections came to be switched off under fuzzing without anything noticing: the guard was
    /// designed for both policies, but only ever exercised under one.
    /// </remarks>
    public class GenericListTests : DataRaceChecking.GenericListTests
    {
        public GenericListTests(ITestOutputHelper output)
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

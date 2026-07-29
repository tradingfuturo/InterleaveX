// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System.Collections.Generic;
using Microsoft.Coyote.Tests.Common.Architecture;

namespace Microsoft.Coyote.Runtime.Tests
{
    /// <summary>
    /// Tests that every test in this assembly explores from a seed it can be re-run with.
    /// </summary>
    public class DeterministicSeedIsolationTests : DeterministicSeedIsolationTestsBase
    {
        /// <inheritdoc/>
        protected override string AssemblyFileName => "Microsoft.Coyote.Tests.Runtime.dll";

        /// <inheritdoc/>
        protected override IReadOnlyList<string> AllowedToBuildAnEngine => new[]
        {
            // Both build their own engine because what they check is which delegate shapes the
            // runtime accepts, but their configuration comes from 'GetConfiguration', which is
            // already seeded.
            "Microsoft.Coyote.Runtime.Tests.RuntimeDelegateTests::TestActionWithICoyoteRuntimeDelegate",
            "Microsoft.Coyote.Runtime.Tests.RuntimeDelegateTests::TestFuncWithICoyoteRuntimeDelegate"
        };
    }
}

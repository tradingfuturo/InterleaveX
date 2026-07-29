// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System.Collections.Generic;
using Microsoft.Coyote.Tests.Common.Architecture;

namespace Microsoft.Coyote.Actors.BugFinding.Tests
{
    /// <summary>
    /// Tests that every test in this assembly explores from a seed it can be re-run with.
    /// </summary>
    /// <remarks>
    /// This assembly had no guard until the scan learned to see a test that asks the engine for an
    /// instance rather than constructing one. Both of the methods below were invisible to it, so the
    /// convention reported this project as having nothing to freeze while it was building two
    /// engines of its own.
    /// </remarks>
    public class DeterministicSeedIsolationTests : DeterministicSeedIsolationTestsBase
    {
        /// <inheritdoc/>
        protected override string AssemblyFileName => "Microsoft.Coyote.Tests.Actors.BugFinding.dll";

        /// <inheritdoc/>
        protected override IReadOnlyList<string> AllowedToBuildAnEngine => new[]
        {
            // Both ask 'TestingEngine.Create' for an engine because what they check is that a
            // finalizer runs, which needs the engine's own lifetime rather than the base class's.
            // Their configuration comes from 'GetConfiguration', which is already seeded.
            "Microsoft.Coyote.Actors.BugFinding.Tests.FinalizerTests::TestActorFinalizerInvoked",
            "Microsoft.Coyote.Actors.BugFinding.Tests.FinalizerTests::TestStateMachineFinalizerInvoked"
        };
    }
}

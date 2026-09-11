// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using Microsoft.Coyote;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// Exercises the current-use model for <see cref="Guid.NewGuid"/>.
    /// These tests deliberately run with every uncontrolled-data escape hatch disabled.
    /// </summary>
    public class CurrentUseGuidTests : BaseBugFindingTest
    {
        public CurrentUseGuidTests(ITestOutputHelper output)
            : base(output)
        {
        }

        private Configuration StrictConfiguration() => this.GetConfiguration()
            .WithPartiallyControlledConcurrencyAllowed(false)
            .WithPartiallyControlledDataNondeterminismAllowed(false)
            .WithSystematicFuzzingFallbackEnabled(false)
            .WithTestingIterations(1);

        [Fact(Timeout = 5000)]
        public void TestGuidNewGuidProducesUniqueValuesWithCanonicalFormat()
        {
            this.Test(() =>
            {
                var values = new HashSet<Guid>();
                for (int i = 0; i < 32; i++)
                {
                    Guid value = Guid.NewGuid();
                    Specification.Assert(value != Guid.Empty, "Guid.NewGuid returned Guid.Empty.");
                    Specification.Assert(values.Add(value), "Guid.NewGuid returned a duplicate value.");

                    string formatted = value.ToString("D");
                    Specification.Assert(formatted.Length == 36 && Guid.TryParseExact(formatted, "D", out Guid parsed) &&
                        parsed == value, "Guid.NewGuid returned a value with a non-canonical D format.");

                    string compact = value.ToString("N");
                    Specification.Assert(compact[12] == '4',
                        "Guid.NewGuid returned a GUID without RFC 4122 version 4 bits.");
                    Specification.Assert(compact[16] is '8' or '9' or 'a' or 'b' or 'A' or 'B',
                        "Guid.NewGuid returned a GUID without RFC 4122 variant bits.");
                }
            }, configuration: this.StrictConfiguration());
        }

        [Fact(Timeout = 5000)]
        public void TestGuidNewGuidIsIsolatedAcrossTestingExecutions()
        {
            Guid[] firstExecution = null;
            int execution = 0;

            this.Test(() =>
            {
                var currentExecution = new Guid[2];
                currentExecution[0] = Guid.NewGuid();
                currentExecution[1] = Guid.NewGuid();

                if (execution++ is 0)
                {
                    firstExecution = currentExecution;
                    return;
                }

                Specification.Assert(currentExecution[0] == firstExecution[0] &&
                    currentExecution[1] == firstExecution[1],
                    "Guid.NewGuid state leaked or reset inconsistently across testing executions.");
            }, configuration: this.StrictConfiguration().WithTestingIterations(2));
            Assert.Equal(2, execution);
        }

        [Fact(Timeout = 5000)]
        public void TestGuidNewGuidReplayReproducesCapturedValue()
        {
            Guid captured = Guid.Empty;

            this.TestWithError(() =>
            {
                Guid value = Guid.NewGuid();
                if (captured == Guid.Empty)
                {
                    captured = value;
                }

                Specification.Assert(value == captured,
                    "Guid.NewGuid did not reproduce the captured value during trace replay.");
                Specification.Assert(false, "Guid.NewGuid replay checkpoint.");
            },
            expectedError: "Guid.NewGuid replay checkpoint.",
            configuration: this.StrictConfiguration(),
            replay: true);
        }
    }
}

// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Threading;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// Regression shields for named event aliases sharing a single modeled resource.
    /// </summary>
    public class EventWaitHandleRegressionShieldTests : BaseBugFindingTest
    {
        public EventWaitHandleRegressionShieldTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestSameRuntimeNamedAliasesShareStateAndSurviveOneAliasDisposal()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            this.Test(() =>
            {
                string name = $"Coyote.NamedEvent.{Guid.NewGuid():N}";
                EventWaitHandle first = null;
                EventWaitHandle second = null;
                try
                {
                    first = new EventWaitHandle(false, EventResetMode.AutoReset, name, out bool createdFirst);
                    second = new EventWaitHandle(true, EventResetMode.AutoReset, name, out bool createdSecond);

                    Specification.Assert(createdFirst && !createdSecond,
                        "The named EventWaitHandle aliases were not created/opened as the same operating-system event.");
                    Specification.Assert(!second.WaitOne(0),
                        "A named event alias modeled its ignored initialState instead of the pre-existing event state.");

                    first.Set();
                    Specification.Assert(second.WaitOne(0),
                        "Set through one named event alias was not visible through the other alias.");

                    first.Dispose();
                    first = null;
                    second.Set();
                    Specification.Assert(second.WaitOne(0),
                        "Disposing one named event alias invalidated the surviving alias model.");
                }
                finally
                {
                    first?.Dispose();
                    second?.Dispose();
                }
            });
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestWaitAllRejectsCanonicalNamedAliasesButWaitAnyAcceptsThem()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            this.Test(() =>
            {
                string name = $"Coyote.NamedEvent.Duplicate.{Guid.NewGuid():N}";
                using var first = new EventWaitHandle(true, EventResetMode.ManualReset, name, out bool createdFirst);
                using var second = new EventWaitHandle(false, EventResetMode.ManualReset, name, out bool createdSecond);
                Specification.Assert(createdFirst && !createdSecond,
                    "The named aliases did not resolve to the same native event.");

                Assert.Throws<DuplicateWaitObjectException>(() =>
                    WaitHandle.WaitAll(new WaitHandle[] { first, second }, 0));
                Specification.Assert(WaitHandle.WaitAny(new WaitHandle[] { first, second }, 0) is 0,
                    "WaitAny rejected or selected the wrong index for duplicate canonical aliases.");
            });
        }
    }
}

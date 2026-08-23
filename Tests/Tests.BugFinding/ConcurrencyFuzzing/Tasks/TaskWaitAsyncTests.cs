// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET6_0_OR_GREATER
using Microsoft.Coyote.Runtime;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests.SystematicFuzzing
{
    public class TaskWaitAsyncTests : Tests.TaskWaitAsyncTests
    {
        public TaskWaitAsyncTests(ITestOutputHelper output)
            : base(output)
        {
        }

        private protected override SchedulingPolicy SchedulingPolicy => SchedulingPolicy.Fuzzing;

        protected override Configuration GetConfiguration() =>
            base.GetConfiguration().WithSystematicFuzzingEnabled();
    }
}
#endif

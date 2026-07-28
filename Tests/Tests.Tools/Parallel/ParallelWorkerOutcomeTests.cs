// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using Microsoft.Coyote.SystematicTesting;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Tools.Tests
{
    /// <summary>
    /// Tests the policy that decides whether a finished worker process of a parallel run failed. A
    /// worker writes its report before it exits, so a failure after that point leaves a report that
    /// merges perfectly well; without this policy the merged run reports success and a failing test
    /// run looks green.
    /// </summary>
    public class ParallelWorkerOutcomeTests : BaseToolsTest
    {
        public ParallelWorkerOutcomeTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestCleanRunIsAccepted()
        {
            Assert.Null(ParallelWorkerOutcome.Evaluate(0, (int)ExitCode.Success, reportExists: true));
        }

        [Fact(Timeout = 5000)]
        public void TestBugFoundIsAccepted()
        {
            // A worker that found a bug did its job. The bug travels in the report it wrote, and the
            // coordinator uses this code to stop the other workers, so it is an expected outcome.
            Assert.Null(ParallelWorkerOutcome.Evaluate(1, (int)ExitCode.BugFound, reportExists: true));
        }

        [Fact(Timeout = 5000)]
        public void TestErrorIsReportedEvenWithAReport()
        {
            // The case this policy exists for: the worker saved a report and then failed.
            string failure = ParallelWorkerOutcome.Evaluate(2, (int)ExitCode.Error, reportExists: true);
            Assert.NotNull(failure);
            Assert.Contains("2", failure);
            Assert.Contains("1", failure);
        }

        [Fact(Timeout = 5000)]
        public void TestInternalErrorIsReportedEvenWithAReport()
        {
            string failure = ParallelWorkerOutcome.Evaluate(3, (int)ExitCode.InternalError, reportExists: true);
            Assert.NotNull(failure);
            Assert.Contains("3", failure);
        }

        [Fact(Timeout = 5000)]
        public void TestUnrecognizedExitCodeIsReported()
        {
            // A process that dies outside the tool's own error handling reports whatever the operating
            // system gave it, which on Windows is -1 for a terminated process. Anything that is not a
            // code the tool itself returns has to count as a failure rather than fall through as one.
            string failure = ParallelWorkerOutcome.Evaluate(4, -1, reportExists: true);
            Assert.NotNull(failure);
            Assert.Contains("-1", failure);
        }

        [Fact(Timeout = 5000)]
        public void TestMissingReportIsReportedWhateverTheExitCode()
        {
            // The missing report is the more informative of the two, so it is what gets said.
            foreach (int exitCode in new int[] { (int)ExitCode.Success, (int)ExitCode.BugFound, (int)ExitCode.Error })
            {
                string failure = ParallelWorkerOutcome.Evaluate(5, exitCode, reportExists: false);
                Assert.NotNull(failure);
                Assert.Contains("no test report", failure);
                Assert.Contains("5", failure);
            }
        }
    }
}

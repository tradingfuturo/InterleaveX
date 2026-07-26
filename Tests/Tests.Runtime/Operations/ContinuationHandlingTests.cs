// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Runtime.Tests
{
    /// <summary>
    /// Tests for how the awaiters decide whether a failure raised while handing a prepared
    /// continuation to the underlying awaiter may be swallowed.
    /// </summary>
    public class ContinuationHandlingTests : BaseRuntimeTest
    {
        public ContinuationHandlingTests(ITestOutputHelper output)
            : base(output)
        {
        }

        /// <summary>
        /// Verifies which failures count as an orphaned continuation.
        /// </summary>
        /// <remarks>
        /// Every awaiter wraps its hand-off in a filtered catch over this predicate. The filter is what
        /// keeps a genuine registration error visible: swallowing one leaves the awaiting operation
        /// paused forever, and the run reports a deadlock that has nothing to do with the program under
        /// test. The predicate is checked directly because the call sites cannot be: the code it
        /// replaced caught every exception, so reverting it only widens the catch, and no test can
        /// observe an error that is being hidden more thoroughly than before.
        /// </remarks>
        [Fact(Timeout = 5000)]
        public void TestOrphanedContinuationsAreTheOnlySwallowedFailures()
        {
            CoyoteRuntime captured = null;
            this.RunSystematicTest(() =>
            {
                CoyoteRuntime runtime = CoyoteRuntime.Current;
                captured = runtime;

                // The interrupt that teardown raises on the threads it interrupts means the
                // continuation is going nowhere, even while the runtime is still running.
                Specification.Assert(runtime.IsContinuationOrphaned(new ThreadInterruptedException()),
                    "A thread interrupt should always mean the continuation is orphaned.");

                // Anything else, while the runtime is running, is a real failure that has to surface.
                Specification.Assert(!runtime.IsContinuationOrphaned(new InvalidOperationException()),
                    "A registration failure should surface while the runtime is running.");
                Specification.Assert(!runtime.IsContinuationOrphaned(new NullReferenceException()),
                    "A null reference should surface while the runtime is running.");
                Specification.Assert(!runtime.IsContinuationOrphaned(new ObjectDisposedException("test")),
                    "A disposal failure should surface while the runtime is running.");
            },
            configuration: this.GetConfiguration().WithTestingIterations(1));

            // Once the runtime has stopped, the awaiting operation is gone and every failure means the
            // same thing, so the teardown window is the one place a failure is dropped.
            Assert.NotNull(captured);
            Assert.NotEqual(ExecutionStatus.Running, captured.ExecutionStatus);
            Assert.True(captured.IsContinuationOrphaned(new ThreadInterruptedException()));
            Assert.True(captured.IsContinuationOrphaned(new InvalidOperationException()));
            Assert.True(captured.IsContinuationOrphaned(new NullReferenceException()));
        }
    }
}

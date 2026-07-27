// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// Tests that <see cref="AsyncLocal{T}"/> values flow through controlled operations with the same
    /// semantics as they do outside of testing.
    /// </summary>
    /// <remarks>
    /// These tests guard the <see cref="System.Threading.ExecutionContext"/> semantics that controlled
    /// operations inherit. The runtime executes each operation on a controlled thread, so anything that
    /// changes how those threads acquire their execution context, such as reusing them across operations,
    /// can silently change what the program under test observes. There is no other coverage of this.
    /// </remarks>
    public class AsyncLocalFlowTests : BaseBugFindingTest
    {
        public AsyncLocalFlowTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestAsyncLocalFlowsIntoTask()
        {
            this.Test(async () =>
            {
                var local = new AsyncLocal<int>();
                local.Value = 7;

                int observed = 0;
                await Task.Run(() =>
                {
                    observed = local.Value;
                });

                Specification.Assert(observed is 7,
                    "Expected the async local value '7' to flow into the task, but observed '{0}'.", observed);
            },
            this.GetConfiguration().WithTestingIterations(50));
        }

        [Fact(Timeout = 5000)]
        public void TestAsyncLocalDoesNotLeakAcrossSiblingTasks()
        {
            this.Test(async () =>
            {
                var local = new AsyncLocal<int>();

                int observed = -1;
                var t1 = Task.Run(() =>
                {
                    local.Value = 42;
                });

                var t2 = Task.Run(() =>
                {
                    observed = local.Value;
                });

                await Task.WhenAll(t1, t2);

                // A value written inside one task must not be visible in a sibling, regardless of the
                // order the two tasks are scheduled in, because each captured the context independently.
                Specification.Assert(observed is 0,
                    "Expected a sibling task to observe the default value '0', but observed '{0}'.", observed);

                // The write must not escape back into the parent either.
                Specification.Assert(local.Value is 0,
                    "Expected the parent to observe the default value '0', but observed '{0}'.", local.Value);
            },
            this.GetConfiguration().WithTestingIterations(50));
        }

        [Fact(Timeout = 5000)]
        public void TestAsyncLocalFlowsAcrossAwaitContinuation()
        {
            this.Test(async () =>
            {
                var local = new AsyncLocal<int>();
                local.Value = 3;

                await Task.Yield();

                // The continuation resumes on a different controlled operation, but it belongs to the
                // same asynchronous control flow, so the value must still be visible.
                Specification.Assert(local.Value is 3,
                    "Expected the async local value '3' to survive the await, but observed '{0}'.", local.Value);
            },
            this.GetConfiguration().WithTestingIterations(50));
        }

        /// <summary>
        /// Ambient state installed by the caller before the test runs; see
        /// <see cref="TestAsyncLocalFlowsFromCallerIntoTest"/>.
        /// </summary>
        private static readonly AsyncLocal<int> AmbientValue = new AsyncLocal<int>();

        [Fact(Timeout = 5000)]
        public void TestAsyncLocalFlowsFromCallerIntoTest()
        {
            // A test harness may install ambient state, such as a service override, on its own thread
            // before invoking the testing engine, and the program under test then reads it from inside
            // controlled operations. The runtime must flow the caller's execution context into the
            // operations it schedules, whether they run on dedicated or pooled threads.
            AmbientValue.Value = 99;
            try
            {
                this.Test(async () =>
                {
                    Specification.Assert(AmbientValue.Value is 99,
                        "Expected the caller's async local value '99' inside the test, but observed '{0}'.",
                        AmbientValue.Value);

                    int observed = 0;
                    await Task.Run(() =>
                    {
                        observed = AmbientValue.Value;
                    });

                    Specification.Assert(observed is 99,
                        "Expected the caller's async local value '99' inside a task, but observed '{0}'.",
                        observed);
                },
                this.GetConfiguration().WithTestingIterations(20));
            }
            finally
            {
                AmbientValue.Value = 0;
            }
        }

        [Fact(Timeout = 5000)]
        public void TestAsyncLocalFlowsIntoThread()
        {
            this.Test(() =>
            {
                var local = new AsyncLocal<int>();
                local.Value = 11;

                int observed = 0;
                var t = new Thread(() =>
                {
                    observed = local.Value;
                });

                t.Start();
                t.Join();

                Specification.Assert(observed is 11,
                    "Expected the async local value '11' to flow into the thread, but observed '{0}'.", observed);
            },
            this.GetConfiguration().WithTestingIterations(50));
        }
    }
}

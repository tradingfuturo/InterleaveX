// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// Pins how thread-static state behaves across controlled operations.
    /// </summary>
    /// <remarks>
    /// These record a consequence of reusing threads rather than a guarantee worth relying on: a value
    /// written by one operation can be read by a later one that runs on the same thread. It cannot be
    /// prevented, because nothing can enumerate or reset state that belongs to the program under test, so
    /// the rewriter reports thread-static fields instead and this behavior is documented rather than
    /// fixed. The tests exist so that a change to it is a deliberate decision.
    /// </remarks>
    public class ThreadStaticPoolingTests : BaseBugFindingTest
    {
        public ThreadStaticPoolingTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [ThreadStatic]
        private static int ThreadStaticValue;

        [Fact(Timeout = 5000)]
        public void TestThreadStaticStateIsSharedByReusedThreads()
        {
            this.Test(() =>
            {
                // Each operation records what it inherited and then leaves its own mark. Which thread
                // serves which operation is up to the pool, so this does not assert that any particular
                // operation inherits: it asserts that inheriting happens at all, which is the hazard.
                bool isValueInherited = false;
                for (int i = 1; i <= 8; i++)
                {
                    int mark = i;
                    var task = Rewriting.Types.Threading.Tasks.Task.Run(() =>
                    {
                        if (ThreadStaticValue != 0)
                        {
                            isValueInherited = true;
                        }

                        ThreadStaticValue = mark;
                    });

                    task.Wait();
                }

                Specification.Assert(isValueInherited,
                    "No operation observed thread-static state left by an earlier one, so threads are " +
                    "not being reused and the reported hazard would not exist.");
            },
            this.GetConfiguration().WithTestingIterations(10));
        }

        [Fact(Timeout = 5000)]
        public void TestThreadStaticStateIsIsolatedWhenPoolingIsDisabled()
        {
            this.Test(() =>
            {
                // Every operation gets its own thread, so none of them can observe another's state. This
                // is what the escape hatch is for, and unlike the sharing above it is a guarantee.
                for (int i = 1; i <= 8; i++)
                {
                    int mark = i;
                    var task = Rewriting.Types.Threading.Tasks.Task.Run(() =>
                    {
                        Specification.Assert(ThreadStaticValue is 0,
                            "Expected a fresh thread to observe the default value '0', but observed " +
                            "'{0}'.", ThreadStaticValue);
                        ThreadStaticValue = mark;
                    });

                    task.Wait();
                }
            },
            this.GetConfiguration().WithTestingIterations(10).WithControlledThreadPoolingEnabled(false));
        }
    }
}

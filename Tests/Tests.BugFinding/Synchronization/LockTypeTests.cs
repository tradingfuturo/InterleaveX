// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#if NET9_0_OR_GREATER
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    public class LockTypeTests : BaseBugFindingTest
    {
        public LockTypeTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestLockEnterScope()
        {
            this.Test(() =>
            {
                var lockObj = new Lock();
                lock (lockObj)
                {
                    // Basic test that lock can be entered and exited.
                }
            });
        }

        [Fact(Timeout = 5000)]
        public void TestLockReentrancy()
        {
            this.Test(() =>
            {
                var lockObj = new Lock();
                lock (lockObj)
                {
                    lock (lockObj)
                    {
                        // Reentrant lock should not deadlock.
                    }
                }
            });
        }

        [Fact(Timeout = 5000)]
        public void TestLockEnterExit()
        {
            this.Test(() =>
            {
                var lockObj = new Lock();
                lockObj.Enter();
                try
                {
                    // Critical section.
                }
                finally
                {
                    lockObj.Exit();
                }
            });
        }

        [Fact(Timeout = 5000)]
        public void TestLockTryEnter()
        {
            this.Test(() =>
            {
                var lockObj = new Lock();
                bool acquired = lockObj.TryEnter();
                Specification.Assert(acquired, "Expected to acquire lock.");
                lockObj.Exit();
            });
        }

        /// <summary>
        /// Answered from the controlled block the runtime holds for the lock, rather than from the
        /// real lock, which is what makes it agree with the rest of the controlled execution.
        /// </summary>
        [Fact(Timeout = 5000)]
        public void TestLockIsHeldByCurrentThread()
        {
            this.Test(() =>
            {
                var lockObj = new Lock();
                Specification.Assert(!lockObj.IsHeldByCurrentThread,
                    "Expected the lock to be unheld before it is entered.");

                lock (lockObj)
                {
                    Specification.Assert(lockObj.IsHeldByCurrentThread,
                        "Expected the lock to be held by the operation that entered it.");
                }

                Specification.Assert(!lockObj.IsHeldByCurrentThread,
                    "Expected the lock to be unheld once it is exited.");
            });
        }
    }
}
#endif

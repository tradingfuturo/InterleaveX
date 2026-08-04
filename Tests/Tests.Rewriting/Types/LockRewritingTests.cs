// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#if NET9_0_OR_GREATER
using System.Threading;
using Xunit;
using Xunit.Abstractions;

#pragma warning disable CS9216 // Lock objects are intentionally used in tests

namespace Microsoft.Coyote.Rewriting.Tests
{
    public class LockRewritingTests : BaseRewritingTest
    {
        public LockRewritingTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestRewritingLockEnterScope()
        {
            var lockObj = new Lock();
            lock (lockObj)
            {
                // Verify that Lock.EnterScope() is rewritten.
            }
        }

        [Fact(Timeout = 5000)]
        public void TestRewritingLockEnterExit()
        {
            var lockObj = new Lock();
            lockObj.Enter();
            lockObj.Exit();
        }

        [Fact(Timeout = 5000)]
        public void TestRewritingLockTryEnter()
        {
            var lockObj = new Lock();
            if (lockObj.TryEnter())
            {
                lockObj.Exit();
            }
        }

        [Fact(Timeout = 5000)]
        public void TestRewritingLockCreate()
        {
            var lockObj = new Lock();
            Assert.NotNull(lockObj);
        }

        [Fact(Timeout = 5000)]
        public void TestRewritingLockIsHeldByCurrentThread()
        {
            // Verify that the getter of Lock.IsHeldByCurrentThread is rewritten, and still answers
            // what the lock itself would when there is no controlled execution to answer from.
            var lockObj = new Lock();
            Assert.False(lockObj.IsHeldByCurrentThread);

            lockObj.Enter();
            Assert.True(lockObj.IsHeldByCurrentThread);

            lockObj.Exit();
            Assert.False(lockObj.IsHeldByCurrentThread);
        }
    }
}
#endif

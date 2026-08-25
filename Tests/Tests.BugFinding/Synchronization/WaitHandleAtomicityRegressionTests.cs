// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// Regression shields for atomic event-signal consumption and wait-handle registration.
    /// </summary>
    public class WaitHandleAtomicityRegressionTests : BaseBugFindingTest
    {
        public WaitHandleAtomicityRegressionTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 15000)]
        [Trait("Category", "WaitHandleAtomicityRemediation")]
        public void TestSingleAutoResetSignalReleasesAtMostOneWaitOneContender()
        {
            this.Test(() =>
            {
                using var signal = new AutoResetEvent(false);
                int completed = 0;
                var firstCompleted = new TaskCompletionSource<bool>();
                var firstStarted = new TaskCompletionSource<bool>();
                var secondStarted = new TaskCompletionSource<bool>();

                Task first = Task.Run(() =>
                {
                    firstStarted.SetResult(true);
                    signal.WaitOne();
                    Interlocked.Increment(ref completed);
                    firstCompleted.TrySetResult(true);
                });
                Task second = Task.Run(() =>
                {
                    secondStarted.SetResult(true);
                    signal.WaitOne();
                    Interlocked.Increment(ref completed);
                    firstCompleted.TrySetResult(true);
                });
                Task signaler = Task.Run(async () =>
                {
                    await Task.WhenAll(firstStarted.Task, secondStarted.Task);
                    signal.Set();
                    await firstCompleted.Task;
                    SchedulingPoint.Interleave();
                    Specification.Assert(completed is 1,
                        "One AutoResetEvent.Set released more than one WaitOne contender.");
                    signal.Set();
                });

                Task.WaitAll(first, second, signaler);
                Specification.Assert(completed is 2,
                    "The cleanup signal did not release the remaining WaitOne contender.");
            }, this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 15000)]
        [Trait("Category", "WaitHandleAtomicityRemediation")]
        public void TestSingleAutoResetSignalReleasesAtMostOneWaitAnyContender()
        {
            this.Test(() =>
            {
                using var signal = new AutoResetEvent(false);
                WaitHandle[] signals = { signal };
                int completed = 0;
                var firstCompleted = new TaskCompletionSource<bool>();
                var firstStarted = new TaskCompletionSource<bool>();
                var secondStarted = new TaskCompletionSource<bool>();

                Task first = Task.Run(() =>
                {
                    firstStarted.SetResult(true);
                    Specification.Assert(WaitHandle.WaitAny(signals) is 0,
                        "The first WaitAny contender returned an unexpected index.");
                    Interlocked.Increment(ref completed);
                    firstCompleted.TrySetResult(true);
                });
                Task second = Task.Run(() =>
                {
                    secondStarted.SetResult(true);
                    Specification.Assert(WaitHandle.WaitAny(signals) is 0,
                        "The second WaitAny contender returned an unexpected index.");
                    Interlocked.Increment(ref completed);
                    firstCompleted.TrySetResult(true);
                });
                Task signaler = Task.Run(async () =>
                {
                    await Task.WhenAll(firstStarted.Task, secondStarted.Task);
                    signal.Set();
                    await firstCompleted.Task;
                    SchedulingPoint.Interleave();
                    Specification.Assert(completed is 1,
                        "One AutoResetEvent.Set released more than one WaitAny contender.");
                    signal.Set();
                });

                Task.WaitAll(first, second, signaler);
                Specification.Assert(completed is 2,
                    "The cleanup signal did not release the remaining WaitAny contender.");
            }, this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 15000)]
        [Trait("Category", "WaitHandleAtomicityRemediation")]
        public void TestWaitAllRechecksAnInitiallySignaledAutoResetEventBeforeCompleting()
        {
            this.Test(() =>
            {
                using var first = new AutoResetEvent(true);
                using var second = new AutoResetEvent(false);
                WaitHandle[] signals = { first, second };
                bool allSucceeded = false;
                var allWaiterStarted = new TaskCompletionSource<bool>();
                var firstConsumed = new TaskCompletionSource<bool>();

                Task allWaiter = Task.Run(() =>
                {
                    allWaiterStarted.SetResult(true);
                    allSucceeded = WaitHandle.WaitAll(signals, 10);
                });
                Task firstConsumer = Task.Run(() =>
                {
                    allWaiterStarted.Task.Wait();
                    Specification.Assert(first.WaitOne(10),
                        "The initially-signaled AutoResetEvent was not available to its competing consumer.");
                    firstConsumed.SetResult(true);
                });
                Task secondSignaler = Task.Run(() =>
                {
                    firstConsumed.Task.Wait();
                    second.Set();
                });

                Task.WaitAll(allWaiter, firstConsumer, secondSignaler);
                Specification.Assert(!allSucceeded,
                    "WaitAll completed after a previously-signaled AutoResetEvent was consumed before all handles were signaled.");
            }, this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 15000)]
        [Trait("Category", "WaitHandleAtomicityRemediation")]
        public void TestSignalResetAndTimeoutRacesDoNotReleaseTwoWaitAnyContenders()
        {
            this.Test(() =>
            {
                using var signal = new AutoResetEvent(false);
                WaitHandle[] signals = { signal };
                bool firstSucceeded = false;
                bool secondSucceeded = false;

                Task first = Task.Run(() => firstSucceeded = WaitHandle.WaitAny(signals, 10) is 0);
                Task second = Task.Run(() => secondSucceeded = WaitHandle.WaitAny(signals, 10) is 0);
                Task signaler = Task.Run(signal.Set);
                Task resetter = Task.Run(signal.Reset);

                Task.WaitAll(first, second, signaler, resetter);
                Specification.Assert((firstSucceeded ? 1 : 0) + (secondSucceeded ? 1 : 0) <= 1,
                    "A signal/reset/timeout race released more than one WaitAny contender.");
            }, this.GetConfiguration().WithLockAccessRaceCheckingEnabled().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "WaitHandleAtomicityRemediation")]
        public void TestUntimedSpinUntilNullConditionThrowsArgumentNullException()
        {
            this.Test(() =>
            {
                ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => SpinWait.SpinUntil(null));
                Assert.Equal("condition", exception.ParamName);
            });
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestWaitAllRejectsNullAndEmptyHandleArrays()
        {
            this.Test(() =>
            {
                WaitHandle[] nullHandles = null;
                Assert.Throws<ArgumentNullException>(() => WaitHandle.WaitAll(nullHandles, 0));
                Assert.Throws<ArgumentException>(() => WaitHandle.WaitAll(Array.Empty<WaitHandle>(), 0));
            });
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestWaitAnyRejectsNullAndEmptyHandleArrays()
        {
            this.Test(() =>
            {
                WaitHandle[] nullHandles = null;
                Assert.Throws<ArgumentNullException>(() => WaitHandle.WaitAny(nullHandles, 0));
                Assert.Throws<ArgumentException>(() => WaitHandle.WaitAny(Array.Empty<WaitHandle>(), 0));
            });
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestWaitAllAndWaitAnyRejectNullElements()
        {
            this.Test(() =>
            {
                WaitHandle[] handles = { null };
                Assert.Throws<ArgumentNullException>(() => WaitHandle.WaitAll(handles, 0));
                Assert.Throws<ArgumentNullException>(() => WaitHandle.WaitAny(handles, 0));
            });
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestWaitAllAndWaitAnyRejectMoreThanSixtyFourHandles()
        {
            this.Test(() =>
            {
                var handles = new EventWaitHandle[65];
                try
                {
                    for (int idx = 0; idx < handles.Length; idx++)
                    {
                        handles[idx] = new AutoResetEvent(false);
                    }

                    Assert.Throws<NotSupportedException>(() => WaitHandle.WaitAll(handles, 0));
                    Assert.Throws<NotSupportedException>(() => WaitHandle.WaitAny(handles, 0));
                }
                finally
                {
                    foreach (EventWaitHandle handle in handles)
                    {
                        handle?.Dispose();
                    }
                }
            });
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestWaitAllRejectsDuplicateHandles()
        {
            this.Test(() =>
            {
                using var signal = new AutoResetEvent(false);
                Assert.Throws<DuplicateWaitObjectException>(() =>
                    WaitHandle.WaitAll(new WaitHandle[] { signal, signal }, 0));
            });
        }
    }
}

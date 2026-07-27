// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.IO;
using Microsoft.Coyote.SystematicTesting;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Tools.Tests
{
    /// <summary>
    /// Tests the policy that decides when a worker process of a parallel run stops, which is consulted
    /// at every testing iteration boundary and therefore has to keep its probes off the hot path.
    /// </summary>
    public class ParallelWorkerStopProbeTests : BaseToolsTest
    {
        public ParallelWorkerStopProbeTests(ITestOutputHelper output)
            : base(output)
        {
        }

        /// <summary>
        /// A clock a test advances by hand, so that the throttling is exercised without real time.
        /// </summary>
        private sealed class FakeClock
        {
            internal long Now { get; set; }
        }

        /// <summary>
        /// A probe that counts how many times it was consulted.
        /// </summary>
        private sealed class CountingProbe
        {
            internal int Count { get; private set; }

            internal bool Result { get; set; }

            internal bool Invoke()
            {
                this.Count++;
                return this.Result;
            }
        }

        [Fact(Timeout = 5000)]
        public void TestProbesOnTheFirstCall()
        {
            // A worker launched into a run that is already stopping, or whose coordinator is already
            // gone, has to notice at its first boundary rather than one interval into the run.
            var clock = new FakeClock();
            var stop = new CountingProbe { Result = true };
            var parent = new CountingProbe { Result = true };

            var probe = new ParallelWorkerStopProbe(stop.Invoke, parent.Invoke, () => clock.Now, 250);

            Assert.True(probe.ShouldStop());
            Assert.Equal(1, stop.Count);
        }

        [Fact(Timeout = 5000)]
        public void TestDoesNotProbeAgainWithinTheInterval()
        {
            // The point of the throttle: an iteration can take a fraction of a millisecond, and probing
            // the file system and the process table on each one costs more than the iteration itself.
            var clock = new FakeClock();
            var stop = new CountingProbe();
            var parent = new CountingProbe { Result = true };

            var probe = new ParallelWorkerStopProbe(stop.Invoke, parent.Invoke, () => clock.Now, 250);

            Assert.False(probe.ShouldStop());
            for (int idx = 0; idx < 1000; idx++)
            {
                clock.Now = 249;
                Assert.False(probe.ShouldStop());
            }

            Assert.Equal(1, stop.Count);
            Assert.Equal(1, parent.Count);
        }

        [Fact(Timeout = 5000)]
        public void TestProbesAgainAfterTheInterval()
        {
            var clock = new FakeClock();
            var stop = new CountingProbe();
            var parent = new CountingProbe { Result = true };

            var probe = new ParallelWorkerStopProbe(stop.Invoke, parent.Invoke, () => clock.Now, 250);

            Assert.False(probe.ShouldStop());
            Assert.Equal(1, stop.Count);

            clock.Now = 250;
            Assert.False(probe.ShouldStop());
            Assert.Equal(2, stop.Count);

            clock.Now = 499;
            Assert.False(probe.ShouldStop());
            Assert.Equal(2, stop.Count);

            clock.Now = 500;
            Assert.False(probe.ShouldStop());
            Assert.Equal(3, stop.Count);
        }

        [Fact(Timeout = 5000)]
        public void TestStopsOnTheFirstProbeAfterTheStopFileAppears()
        {
            var clock = new FakeClock();
            var stop = new CountingProbe();
            var parent = new CountingProbe { Result = true };

            var probe = new ParallelWorkerStopProbe(stop.Invoke, parent.Invoke, () => clock.Now, 250);

            Assert.False(probe.ShouldStop());

            stop.Result = true;
            clock.Now = 250;
            Assert.True(probe.ShouldStop());

            // The decision is sticky, so nothing is probed again once it has been made.
            int countAtDecision = stop.Count;
            clock.Now = 10000;
            Assert.True(probe.ShouldStop());
            Assert.Equal(countAtDecision, stop.Count);
        }

        [Fact(Timeout = 5000)]
        public void TestStopsWhenTheCoordinatorIsGone()
        {
            // A worker must not outlive the coordinator it reports to, whether or not a stop file
            // was ever written.
            var clock = new FakeClock();
            var stop = new CountingProbe();
            var parent = new CountingProbe { Result = true };

            var probe = new ParallelWorkerStopProbe(stop.Invoke, parent.Invoke, () => clock.Now, 250);

            Assert.False(probe.ShouldStop());

            parent.Result = false;
            clock.Now = 250;
            Assert.True(probe.ShouldStop());
        }

        [Fact(Timeout = 5000)]
        public void TestWorkerWithoutACoordinatorIsGovernedOnlyByTheStopFile()
        {
            // Create resolves a parent process id of zero to a probe that always reports alive, so a
            // worker that was not told which coordinator it belongs to is not stopped immediately.
            var probe = ParallelWorkerStopProbe.Create(
                Path.Combine(Path.GetTempPath(), $"coyote-no-such-stop-file-{Guid.NewGuid():N}"), 0);

            Assert.False(probe.ShouldStop());
        }
    }
}

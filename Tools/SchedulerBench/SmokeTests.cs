// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System.Threading.Tasks;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Specifications;
using Microsoft.Coyote.SystematicTesting;

namespace Microsoft.Coyote.Benchmarking.Scheduler
{
    /// <summary>
    /// Test entry points that can be driven through the command line tool.
    /// </summary>
    /// <remarks>
    /// These exist so that the tool itself, including sharding iterations across worker
    /// processes, can be exercised end to end against a small self-contained assembly.
    /// </remarks>
    public static class SmokeTests
    {
        /// <summary>
        /// Shared state accessed by <see cref="FindsRace"/>.
        /// </summary>
        private static int Counter;

        /// <summary>
        /// A test that never fails, for measuring throughput.
        /// </summary>
        [Test]
        public static async Task NoBug() => await Workloads.RunDeepAsync();

        /// <summary>
        /// A test with a genuine race, for checking that sharded exploration still finds
        /// bugs and reports a reproducible trace.
        /// </summary>
        [Test]
        public static async Task FindsRace()
        {
            Counter = 0;
            Task first = Task.Run(() => Increment());
            Task second = Task.Run(() => Increment());
            await Task.WhenAll(first, second);
            Specification.Assert(Counter is 2, "Counter is {0} instead of 2.", Counter);
        }

        /// <summary>
        /// Increments the shared counter without synchronization.
        /// </summary>
        private static void Increment()
        {
            int value = Counter;
            SchedulingPoint.Interleave();
            Counter = value + 1;
        }
    }
}

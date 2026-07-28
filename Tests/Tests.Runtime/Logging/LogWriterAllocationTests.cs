// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using Microsoft.Coyote.Logging;
using Microsoft.Coyote.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Runtime.Tests.Logging
{
    /// <summary>
    /// Verifies that a suppressed debug message costs no allocation.
    /// </summary>
    /// <remarks>
    /// The scheduling hot path logs several debug messages per step, with value-typed arguments such
    /// as thread ids, scheduling point kinds and runtime identifiers. Debug logging is off by
    /// default, so those messages are discarded; what must not happen is paying to construct their
    /// arguments first. The <see cref="object"/>-typed overloads do exactly that, because the
    /// arguments are boxed by the caller before the callee gets to check the verbosity level.
    /// <para>
    /// These tests exist because that cost is invisible: nothing about the logging output changes
    /// when it is reintroduced, and no functional test would notice. Only an allocation measurement
    /// distinguishes the two, so the property is pinned here rather than left to review.
    /// </para>
    /// </remarks>
    public class LogWriterAllocationTests : BaseRuntimeTest
    {
        public LogWriterAllocationTests(ITestOutputHelper output)
            : base(output)
        {
        }

        /// <summary>
        /// Enough iterations that any per-call allocation is unmistakable, and small enough to stay
        /// fast.
        /// </summary>
        private const int Iterations = 20000;

        /// <summary>
        /// How much the measurement may grow when the call count doubles.
        /// </summary>
        /// <remarks>
        /// Measured growth is zero, so this is slack rather than an expected cost. It is not tuned
        /// to anything: the smallest per-call allocation worth worrying about is a single boxed
        /// int, and that would show up as <see cref="Iterations"/> times 24 bytes, roughly a
        /// hundred times this. Anything between one byte and a hundred kilobytes would catch the
        /// same regressions, so the value is chosen to avoid a knife-edge rather than to draw a
        /// meaningful line.
        /// </remarks>
        private const int GrowthBudgetBytes = 4096;

        /// <summary>
        /// Returns the bytes allocated on the current thread by the specified action, having first
        /// run it once so that any one-off costs, such as JIT and the format string literals, are
        /// already paid.
        /// </summary>
        /// <remarks>
        /// Deliberately the per-thread counter and not <see cref="GC.GetTotalAllocatedBytes(bool)"/>.
        /// The process-wide counter attributes allocations made by any other thread in the process
        /// to whatever is being measured here, and a test host runs plenty of them: measured against
        /// it, the no-argument case picked up 62,728 bytes of unrelated traffic when it ran
        /// alongside the execution-trace tests, and passed on its own. That cuts both ways, so it
        /// could as easily have hidden a real per-call allocation as invented one. The action is
        /// synchronous and allocates only on the calling thread, so the per-thread counter measures
        /// exactly it.
        /// <para>
        /// The collection beforehand is not needed for correctness, since the counter is cumulative
        /// rather than a heap measurement, but it keeps a collection from landing in the middle of
        /// the measured region and perturbing its timing.
        /// </para>
        /// </remarks>
        private static long MeasureAllocation(Action action)
        {
            action();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetAllocatedBytesForCurrentThread();
            action();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        /// <summary>
        /// Asserts that the specified action allocates nothing per call, by measuring it at two
        /// call counts and comparing the growth.
        /// </summary>
        /// <remarks>
        /// Asserting on the growth between two call counts rather than on an absolute byte count is
        /// what makes this robust: the property under test is that cost does not scale with the
        /// number of calls, and any fixed cost cancels out of a difference. Boxing the arguments of
        /// the four-argument shape would put the growth in the megabytes.
        /// </remarks>
        private void AssertNoPerCallAllocation(Action<int> action, string what)
        {
            long single = MeasureAllocation(() => action(Iterations));
            long doubled = MeasureAllocation(() => action(Iterations * 2));
            long growth = doubled - single;

            // Recorded even on success. The expected reading is zero at both call counts, so a
            // later reader seeing anything else knows the measurement drifted before it knows the
            // budget was breached.
            this.TestOutput.WriteLine(
                $"{what}: {Iterations} calls -> {single} bytes, {Iterations * 2} calls -> {doubled} bytes, " +
                $"growth {growth} bytes (budget {GrowthBudgetBytes}).");

            Assert.True(growth < GrowthBudgetBytes,
                $"{what} allocated {growth} additional bytes on the calling thread when the call " +
                $"count doubled from {Iterations} to {Iterations * 2} ({single} then {doubled} " +
                $"bytes), against a budget of {GrowthBudgetBytes}. Something is allocating per " +
                $"call: the generic overloads should bind in preference to the object-typed ones, " +
                $"leaving the arguments unboxed unless the message is actually written.");
        }

        private static LogWriter CreateSuppressedWriter() =>
            // Error is the default verbosity, and it discards debug messages.
            new LogWriter(Configuration.Create().WithVerbosityEnabled(VerbosityLevel.Error));

        [Fact(Timeout = 60000)]
        public void TestSuppressedDebugMessagesDoNotAllocate()
        {
            using var logWriter = CreateSuppressedWriter();
            var guid = Guid.NewGuid();

            // Mirrors the shapes used on the scheduling hot path: one to four arguments, mixing
            // reference types with the value types that would otherwise be boxed. The four-argument
            // shape matters most, since it would otherwise bind to the params overload and allocate
            // the array as well.
            this.AssertNoPerCallAllocation(
                count =>
                {
                    for (int i = 0; i < count; ++i)
                    {
                        logWriter.LogDebug("one {0}", i);
                        logWriter.LogDebug("two {0} {1}", "op", i);
                        logWriter.LogDebug("three {0} {1} {2}", "op", SchedulingPointType.Default, i);
                        logWriter.LogDebug("four {0} {1} {2} {3}", "op", SchedulingPointType.Default, i, guid);
                    }
                },
                "Suppressed debug logging with one to four arguments");
        }

        [Fact(Timeout = 60000)]
        public void TestSuppressedDebugMessagesWithoutArgumentsDoNotAllocate()
        {
            using var logWriter = CreateSuppressedWriter();
            this.AssertNoPerCallAllocation(
                count =>
                {
                    for (int i = 0; i < count; ++i)
                    {
                        logWriter.LogDebug("no arguments");
                    }
                },
                "Suppressed debug logging without arguments");
        }

        /// <summary>
        /// The counterpart of the tests above: when debug logging is on, the arguments must still
        /// reach the logger. A gate that suppressed the message entirely would allocate nothing and
        /// pass those tests while silently dropping output.
        /// </summary>
        [Fact(Timeout = 30000)]
        public void TestEnabledDebugMessagesAreStillWritten()
        {
            var configuration = Configuration.Create().WithVerbosityEnabled(VerbosityLevel.Debug);
            using var logger = new MemoryLogger(configuration.VerbosityLevel);
            using var logWriter = new LogWriter(configuration);
            logWriter.SetLogger(logger);

            logWriter.LogDebug("one {0}", 1);
            logWriter.LogDebug("two {0} {1}", 1, 2);
            logWriter.LogDebug("three {0} {1} {2}", 1, 2, 3);
            logWriter.LogDebug("four {0} {1} {2} {3}", 1, 2, 3, 4);

            // Asserted by content rather than against an exact rendering, because what is at stake
            // is that every argument still reaches the logger through the generic overloads, not
            // how the logger chooses to lay the lines out.
            string actual = logger.ToString();
            Assert.Contains("one 1", actual);
            Assert.Contains("two 1 2", actual);
            Assert.Contains("three 1 2 3", actual);
            Assert.Contains("four 1 2 3 4", actual);
        }
    }
}

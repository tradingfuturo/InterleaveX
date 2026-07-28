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
        /// Enough iterations that any per-call allocation dwarfs the constant background, and small
        /// enough to stay fast.
        /// </summary>
        private const int Iterations = 20000;

        /// <summary>
        /// Returns the bytes allocated by the specified action, having first run it once so that
        /// any one-off costs, such as JIT and the format string literals, are already paid.
        /// </summary>
        private static long MeasureAllocation(Action action)
        {
            action();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetTotalAllocatedBytes(precise: true);
            action();
            return GC.GetTotalAllocatedBytes(precise: true) - before;
        }

        /// <summary>
        /// Asserts that the specified action allocates nothing per call, by measuring it at two
        /// call counts and comparing the growth.
        /// </summary>
        /// <remarks>
        /// Testing for zero bytes outright is the obvious thing to do and it does not work: a
        /// measurement carries a small constant background, on the order of a kilobyte, that has
        /// nothing to do with the calls being measured. What distinguishes "allocates nothing per
        /// call" from "allocates a little per call" is whether the total grows with the call count,
        /// so that is what is asserted. Boxing four arguments would put this in the megabytes.
        /// </remarks>
        private static void AssertNoPerCallAllocation(Action<int> action, string what)
        {
            long single = MeasureAllocation(() => action(Iterations));
            long doubled = MeasureAllocation(() => action(Iterations * 2));
            long growth = doubled - single;

            Assert.True(growth < Iterations,
                $"{what} allocated {growth} additional bytes when the call count doubled from " +
                $"{Iterations} to {Iterations * 2} ({single} then {doubled} bytes). That is more " +
                $"than one byte per additional call, so something is allocating per call: the " +
                $"generic overloads should bind in preference to the object-typed ones, leaving " +
                $"the arguments unboxed unless the message is actually written.");
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
            AssertNoPerCallAllocation(
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
            AssertNoPerCallAllocation(
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

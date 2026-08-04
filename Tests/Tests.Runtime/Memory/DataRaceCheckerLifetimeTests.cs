// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Threading;
using Microsoft.Coyote.Rewriting.Types.Collections;
using Xunit;
using Xunit.Abstractions;
using SystemThread = System.Threading.Thread;

namespace Microsoft.Coyote.Runtime.Tests
{
    /// <summary>
    /// Tests which callers of a modelled collection's data-race guard are allowed to take its state
    /// over, and what happens to the ones that are not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A modelled collection held in a static field outlives the iteration that created it, so the
    /// guard has to decide, on every access, whether the state it is holding still belongs to the
    /// caller. Getting that decision wrong does not throw or corrupt anything: it silently empties the
    /// frames a live iteration is holding, and every race those frames would have caught goes
    /// unreported for as long as the stale caller keeps arriving.
    /// </para>
    /// <para>
    /// These tests reach the guard through the overload that takes the caller's standing rather than
    /// reading it, because the situation that matters — a thread still running inside an iteration
    /// that has already been torn down — is reachable in a real run but not reliably reachable on
    /// demand. What is exercised here is the decision itself, on one thread pair, with no scheduling
    /// and no timing.
    /// </para>
    /// </remarks>
    public class DataRaceCheckerLifetimeTests : BaseRuntimeTest
    {
        public DataRaceCheckerLifetimeTests(ITestOutputHelper output)
            : base(output)
        {
        }

        /// <summary>
        /// The generation of a live iteration, and of the one before it that has already ended. Any
        /// two ordered values do: the guard compares them and never interprets them.
        /// </summary>
        private const long LiveGeneration = 2;
        private const long EndedGeneration = 1;

        [Fact(Timeout = 15000)]
        public void TestStaleCallerDoesNotClearLiveFrames()
        {
            var checker = new DataRaceChecker(typeof(object));
            CoyoteRuntime runtime = CoyoteRuntime.Current;

            WithFrameHeldOnAnotherThread(checker, runtime, isWriteAccess: true, () =>
            {
                // The thread left behind by the ended iteration arrives.
                using var stale = checker.Enter(runtime, EndedGeneration, isAccountable: true,
                    isWriteAccess: true);

                // The live frame must still be counted, so this must be reported as a race.
                Assert.Throws<AssertionFailureException>(() =>
                    checker.Enter(runtime, LiveGeneration, isAccountable: true, isWriteAccess: true));
            });
        }

        [Fact(Timeout = 15000)]
        public void TestStaleCallerReleasesNothingItNeverTook()
        {
            var checker = new DataRaceChecker(typeof(object));
            CoyoteRuntime runtime = CoyoteRuntime.Current;

            WithFrameHeldOnAnotherThread(checker, runtime, isWriteAccess: true, () =>
            {
                // Taken and released while the live frame is held. Disposing it must not decrement a
                // count that belongs to the live frame, which is what the scope would do if the stale
                // caller had been given a real one.
                using (checker.Enter(runtime, EndedGeneration, isAccountable: true, isWriteAccess: true))
                {
                }

                Assert.Throws<AssertionFailureException>(() =>
                    checker.Enter(runtime, LiveGeneration, isAccountable: true, isWriteAccess: true));
            });
        }

        [Fact(Timeout = 15000)]
        public void TestUnaccountableCallerDoesNotClearLiveFrames()
        {
            var checker = new DataRaceChecker(typeof(object));
            CoyoteRuntime runtime = CoyoteRuntime.Current;

            WithFrameHeldOnAnotherThread(checker, runtime, isWriteAccess: true, () =>
            {
                // A caller with no controlled execution behind it reports the process-wide default
                // runtime, whose generation is unrelated to the live one and could be either side of
                // it. It owns nothing here whichever way that falls.
                using var uncontrolled = checker.Enter(runtime, LiveGeneration + 1,
                    isAccountable: false, isWriteAccess: true);

                Assert.Throws<AssertionFailureException>(() =>
                    checker.Enter(runtime, LiveGeneration, isAccountable: true, isWriteAccess: true));
            });
        }

        [Fact(Timeout = 15000)]
        public void TestYoungerCallerTakesTheStateOver()
        {
            var checker = new DataRaceChecker(typeof(object));
            CoyoteRuntime runtime = CoyoteRuntime.Current;

            WithFrameHeldOnAnotherThread(checker, runtime, isWriteAccess: true, () =>
            {
                // The frame held by the older iteration is abandoned rather than disposed, which is
                // what happens when an iteration is torn down inside an operation. The next iteration
                // has to be able to start from a clean count, so this must NOT be reported.
                using var younger = checker.Enter(runtime, LiveGeneration + 1, isAccountable: true,
                    isWriteAccess: true);
            });
        }

        [Fact(Timeout = 15000)]
        public void TestLiveFrameIsStillReportedAfterAStaleCaller()
        {
            var checker = new DataRaceChecker(typeof(object));
            CoyoteRuntime runtime = CoyoteRuntime.Current;

            WithFrameHeldOnAnotherThread(checker, runtime, isWriteAccess: false, () =>
            {
                using var stale = checker.Enter(runtime, EndedGeneration, isAccountable: true,
                    isWriteAccess: false);

                // A reader is held, so a writer is a race and another reader is not.
                Assert.Throws<AssertionFailureException>(() =>
                    checker.Enter(runtime, LiveGeneration, isAccountable: true, isWriteAccess: true));

                using var reader = checker.Enter(runtime, LiveGeneration, isAccountable: true,
                    isWriteAccess: false);
            });
        }

        /// <summary>
        /// Holds one frame of the live generation open on another thread for as long as the specified
        /// action runs, and rethrows whatever that thread saw.
        /// </summary>
        /// <remarks>
        /// Another thread rather than this one, because the guard identifies a frame by who took it and
        /// exempts anyone re-entering their own. Everything a race needs is here: two owners, and one
        /// frame that is still open while the other arrives.
        /// </remarks>
        private static void WithFrameHeldOnAnotherThread(DataRaceChecker checker, CoyoteRuntime runtime,
            bool isWriteAccess, Action action)
        {
            using var frameIsHeld = new ManualResetEventSlim(false);
            using var frameCanBeReleased = new ManualResetEventSlim(false);
            Exception failure = null;

            var holder = new SystemThread(() =>
            {
                try
                {
                    using var frame = checker.Enter(runtime, LiveGeneration, isAccountable: true,
                        isWriteAccess: isWriteAccess);
                    frameIsHeld.Set();
                    frameCanBeReleased.Wait();
                }
                catch (Exception exception)
                {
                    failure = exception;
                    frameIsHeld.Set();
                }
            });

            holder.Start();
            frameIsHeld.Wait();

            try
            {
                Assert.Null(failure);
                action();
            }
            finally
            {
                frameCanBeReleased.Set();
                holder.Join();
            }

            Assert.Null(failure);
        }
    }
}

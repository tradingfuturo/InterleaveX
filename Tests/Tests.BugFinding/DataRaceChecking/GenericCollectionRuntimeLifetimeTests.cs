// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Rewriting;
using Microsoft.Coyote.Runtime;
using Xunit;
using Xunit.Abstractions;
using SystemThread = System.Threading.Thread;

namespace Microsoft.Coyote.BugFinding.Tests.DataRaceChecking
{
    /// <summary>
    /// Tests that a caller the runtime knows nothing about cannot take a modelled collection's race
    /// state away from the iteration that is using it.
    /// </summary>
    /// <remarks>
    /// A modelled collection can be reached from a thread that no runtime controls: one the test never
    /// started, or one left over from an iteration that has already been torn down. Such a caller
    /// resolves a different runtime from the live one, and the guard used to read that as the arrival
    /// of a new iteration and clear the frames the live one was holding. Nothing fails when that
    /// happens — the races those frames would have caught are simply not reported, which is the worst
    /// way for a bug finder to be wrong.
    /// </remarks>
    public class GenericCollectionRuntimeLifetimeTests : BaseBugFindingTest
    {
        public GenericCollectionRuntimeLifetimeTests(ITestOutputHelper output)
            : base(output)
        {
        }

        /// <summary>
        /// The uncontrolled caller arrives while the writer is still inside the dictionary, which is
        /// the only moment at which it could hide anything: it has to clear a frame that is being
        /// held, not one that has already been released.
        /// </summary>
        /// <remarks>
        /// The writer signals from its key's <see cref="object.GetHashCode"/>, which the dictionary
        /// calls from inside <c>Remove</c>, after the guard is already open — the same technique the
        /// guarded-window tests use, for the same reason. The reader refuses to move until then, so
        /// the read/write race it goes on to commit is reported if and only if the writer's frame
        /// survived the intruder.
        /// </remarks>
        [Fact(Timeout = 30000)]
        public void TestUncontrolledCallerDoesNotHideADataRace()
        {
            this.TestWithError(async () =>
            {
                var signal = new Signal();

                // Seeded so that Remove reaches the key: on an empty dictionary it returns on the
                // empty check, before hashing anything.
                var dictionary = new Dictionary<IntrudingKey, bool>
                {
                    { new IntrudingKey(null, null), true }
                };

                Task writer = Task.Run(() =>
                {
                    dictionary.Remove(new IntrudingKey(dictionary, signal));
                });

                Task reader = Task.Run(() =>
                {
                    while (!signal.IsSet)
                    {
                        SchedulingPoint.Interleave();
                    }

                    dictionary.TryGetValue(new IntrudingKey(null, null), out bool _);
                });

                await Task.WhenAll(writer, reader);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100),
            expectedError: $"Found read/write data race on '{typeof(Dictionary<IntrudingKey, bool>)}'.",
            replay: true);
        }

        /// <summary>
        /// Per-iteration flag published by the writer from inside the underlying operation.
        /// </summary>
        private class Signal
        {
            /// <summary>
            /// True once the writer is inside the dictionary operation and the intruder has been.
            /// </summary>
            internal volatile bool IsSet;
        }

        /// <summary>
        /// A key that, while being hashed, lets an uncontrolled thread touch the very dictionary that
        /// is hashing it, and only then reports that the operation has begun.
        /// </summary>
        private class IntrudingKey
        {
            private readonly Dictionary<IntrudingKey, bool> Owner;
            private readonly Signal Signal;

            internal IntrudingKey(Dictionary<IntrudingKey, bool> owner, Signal signal)
            {
                this.Owner = owner;
                this.Signal = signal;
            }

            public override int GetHashCode()
            {
                if (this.Owner != null)
                {
                    // Reads the count rather than the contents: it is guarded, which is all this needs,
                    // and it leaves the storage alone while another thread is inside it.
                    UncontrolledThreadRunner.RunAndWait(() => _ = this.Owner.Count);
                    this.Signal.IsSet = true;
                    SchedulingPoint.Interleave();
                }

                return 1;
            }

            public override bool Equals(object obj) => ReferenceEquals(this, obj);
        }

        /// <summary>
        /// Runs an action on a thread that the runtime does not control and waits for it.
        /// </summary>
        /// <remarks>
        /// Not rewritten, so that starting the thread stays a real thread start instead of becoming a
        /// controlled operation. The execution context is not flowed either: it carries the runtime
        /// installed on the thread that starts this one, and flowing it would make the action report
        /// the live runtime, which is the one situation this is written to avoid. The action itself
        /// comes from rewritten code, so what it does to the collection is still guarded.
        /// </remarks>
        [SkipRewriting("Starts the one thread in these tests that must stay outside the runtime's control.")]
        private static class UncontrolledThreadRunner
        {
            internal static void RunAndWait(Action action)
            {
                using (ExecutionContext.SuppressFlow())
                {
                    var thread = new SystemThread(() => action())
                    {
                        IsBackground = true
                    };

                    thread.Start();
                    thread.Join();
                }
            }
        }
    }
}

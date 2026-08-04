// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Coyote.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests.DataRaceChecking
{
    /// <summary>
    /// Tests that the data-race guard on a modelled collection spans the REAL operation, and that
    /// holding it that long does not turn re-entrant user callbacks into false reports.
    /// </summary>
    public class GenericCollectionGuardedWindowTests : BaseBugFindingTest
    {
        public GenericCollectionGuardedWindowTests(ITestOutputHelper output)
            : base(output)
        {
        }

        /// <summary>
        /// The overlap here is reachable ONLY while the first operation is inside the underlying
        /// dictionary, which is what makes this a regression test rather than another race test.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The reader refuses to touch the dictionary until the writer signals, and the writer only
        /// signals from its key's <see cref="object.GetHashCode"/> — which the dictionary calls from
        /// inside <c>Remove</c>, after the shim has already entered the guard. A guard that closes
        /// before delegating is therefore provably released by the time the reader is allowed to move,
        /// and reports nothing; a guard that spans the operation reports the read/write race.
        /// </para>
        /// <para>
        /// The signal is per-iteration state, NOT a static: a static would still be set from the
        /// previous iteration, the reader would never wait, and the race would degenerate into the
        /// ordinary entry-window overlap that the old guard could already catch.
        /// </para>
        /// </remarks>
        [Fact(Timeout = 15000)]
        public void TestGenericDictionaryDataRaceInsideOperation()
        {
            this.TestWithError(async () =>
            {
                var signal = new Signal();

                // Seeded so the dictionary has buckets: Remove and TryGetValue both return on the
                // empty check BEFORE hashing, so on an empty dictionary the key is never consulted
                // and the writer could never signal.
                var dictionary = new Dictionary<ProbeKey, bool>
                {
                    { new ProbeKey(null), true }
                };

                Task writer = Task.Run(() =>
                {
                    dictionary.Remove(new ProbeKey(signal));
                });

                Task reader = Task.Run(() =>
                {
                    while (!signal.IsSet)
                    {
                        SchedulingPoint.Interleave();
                    }

                    dictionary.TryGetValue(new ProbeKey(null), out bool _);
                });

                await Task.WhenAll(writer, reader);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100),
            expectedError: $"Found read/write data race on '{typeof(Dictionary<ProbeKey, bool>)}'.",
            replay: true);
        }

        /// <summary>
        /// A key callback that touches the same dictionary the operation is already inside must not
        /// report a race against itself.
        /// </summary>
        /// <remarks>
        /// This is the cost of holding the guard across the delegation: <c>GetHashCode</c> now runs
        /// inside it. One logical operation re-entering itself is not concurrency, so the nested frame
        /// is exempt. Single threaded on purpose — there is no second operation anywhere.
        /// </remarks>
        [Fact(Timeout = 15000)]
        public void TestGenericDictionaryReentrantKeyCallbackIsNotARace()
        {
            this.Test(() =>
            {
                var dictionary = new Dictionary<ReentrantKey, bool>();
                var key = new ReentrantKey();
                key.Owner = dictionary;

                // The write frame is open across Add, whose hashing re-enters for a read.
                dictionary.Add(key, true);

                Assert.Single(dictionary);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }

        /// <summary>
        /// A comparison delegate that reads the list it is sorting must not report a race either.
        /// </summary>
        /// <remarks>
        /// The list equivalent of the reentrant key, and the sharper case: <c>Sort</c> holds a WRITE
        /// frame for its whole duration and calls the comparison many times, so every one of those
        /// callbacks re-enters a list that is already being written.
        /// </remarks>
        [Fact(Timeout = 15000)]
        public void TestGenericListReentrantComparisonIsNotARace()
        {
            this.Test(() =>
            {
                var list = new List<int> { 3, 1, 2 };

                list.Sort((left, right) =>
                {
                    _ = list.Count;
                    return left.CompareTo(right);
                });

                Assert.Equal(1, list[0]);
                Assert.Equal(3, list[2]);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }

        /// <summary>
        /// Per-iteration flag published by the writer from inside the underlying operation.
        /// </summary>
        private class Signal
        {
            /// <summary>
            /// True once the writer is inside the dictionary operation.
            /// </summary>
            internal volatile bool IsSet;
        }

        /// <summary>
        /// A key that signals, from inside the operation that is hashing it, that the operation has
        /// begun. Every instance hashes alike so lookups reach the comparison step.
        /// </summary>
        private class ProbeKey
        {
            private readonly Signal Signal;

            internal ProbeKey(Signal signal)
            {
                this.Signal = signal;
            }

            public override int GetHashCode()
            {
                if (this.Signal != null)
                {
                    this.Signal.IsSet = true;
                    SchedulingPoint.Interleave();
                }

                return 1;
            }

            public override bool Equals(object obj) => ReferenceEquals(this, obj);
        }

        /// <summary>
        /// A key whose hashing reads the very dictionary that is hashing it.
        /// </summary>
        private class ReentrantKey
        {
            private bool IsReentering;

            /// <summary>
            /// Gets or sets the dictionary this key re-enters while being hashed.
            /// </summary>
            internal Dictionary<ReentrantKey, bool> Owner { get; set; }

            public override int GetHashCode()
            {
                // Count rather than a lookup: re-entering through a keyed operation would hash this
                // key again and recurse forever, which is a different bug than the one under test.
                if (this.Owner != null && !this.IsReentering)
                {
                    this.IsReentering = true;
                    _ = this.Owner.Count;
                    this.IsReentering = false;
                }

                return 1;
            }

            public override bool Equals(object obj) => ReferenceEquals(this, obj);
        }
    }
}

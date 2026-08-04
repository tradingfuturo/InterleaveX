// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests.DataRaceChecking
{
    public class GenericHashSetTests : BaseBugFindingTest
    {
        public GenericHashSetTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestGenericHashSetProperties()
        {
            this.Test(() =>
            {
                var hashSet = new HashSet<int>();
                Assert.Empty(hashSet);

                hashSet.Add(1);
                var count = hashSet.Count;
                Assert.Equal(1, count);
                Assert.Single(hashSet);

                hashSet.Clear();
                Assert.Empty(hashSet);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        public void TestGenericHashSetWriteWriteDataRace()
        {
            this.TestWithError(async () =>
            {
                var hashSet = new HashSet<int>();

                Task t1 = Task.Run(() =>
                {
                    hashSet.Add(1);
                });

                Task t2 = Task.Run(() =>
                {
                    hashSet.Add(2);
                });

                await Task.WhenAll(t1, t2);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100),
            expectedError: $"Found write/write data race on '{typeof(HashSet<int>)}'.",
            replay: true);
        }

        [Fact(Timeout = 5000)]
        public void TestGenericHashSetReadWriteDataRace()
        {
            this.TestWithError(async () =>
            {
                var hashSet = new HashSet<int>();

                Task t1 = Task.Run(() =>
                {
                    hashSet.Add(1);
                });

                Task t2 = Task.Run(() =>
                {
                    hashSet.Contains(2);
                });

                await Task.WhenAll(t1, t2);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100),
            expectedError: $"Found read/write data race on '{typeof(HashSet<int>)}'.",
            replay: true);
        }

        /// <summary>
        /// Checks that intersecting is guarded. The replacement for it was misspelled, so nothing was
        /// ever redirected to it and the set was left unguarded for the whole of an operation that
        /// rewrites its contents.
        /// </summary>
        /// <remarks>
        /// The other set is an array so that only one modelled collection is in play, and the race
        /// that is reported can only be the one this test is about.
        /// </remarks>
        [Fact(Timeout = 5000)]
        public void TestGenericHashSetIntersectWithDataRace()
        {
            this.TestWithError(async () =>
            {
                var hashSet = new HashSet<int>
                {
                    1, 2, 3
                };

                int[] other = new int[] { 1, 2 };

                Task t1 = Task.Run(() =>
                {
                    hashSet.IntersectWith(other);
                });

                Task t2 = Task.Run(() =>
                {
                    hashSet.IntersectWith(other);
                });

                await Task.WhenAll(t1, t2);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100),
            expectedError: $"Found write/write data race on '{typeof(HashSet<int>)}'.",
            replay: true);
        }

        /// <summary>
        /// Checks that trimming counts as a write. It rebuilds the set's storage, but was declared as
        /// a read, so two threads trimming at once were treated as compatible readers.
        /// </summary>
        [Fact(Timeout = 5000)]
        public void TestGenericHashSetTrimExcessDataRace()
        {
            this.TestWithError(async () =>
            {
                var hashSet = new HashSet<int>
                {
                    1, 2, 3
                };

                Task t1 = Task.Run(() =>
                {
                    hashSet.TrimExcess();
                });

                Task t2 = Task.Run(() =>
                {
                    hashSet.TrimExcess();
                });

                await Task.WhenAll(t1, t2);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100),
            expectedError: $"Found write/write data race on '{typeof(HashSet<int>)}'.",
            replay: true);
        }

        /// <summary>
        /// Checks that reserving capacity counts as a write, for the same reason trimming does.
        /// </summary>
        [Fact(Timeout = 5000)]
        public void TestGenericHashSetEnsureCapacityDataRace()
        {
            this.TestWithError(async () =>
            {
                var hashSet = new HashSet<int>();

                Task t1 = Task.Run(() =>
                {
                    hashSet.EnsureCapacity(16);
                });

                Task t2 = Task.Run(() =>
                {
                    hashSet.EnsureCapacity(32);
                });

                await Task.WhenAll(t1, t2);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100),
            expectedError: $"Found write/write data race on '{typeof(HashSet<int>)}'.",
            replay: true);
        }

        /// <summary>
        /// Checks that the deserialization callback counts as a write, because it is what fills the
        /// set in once deserialization has produced its contents.
        /// </summary>
        /// <remarks>
        /// Called on a set that was never deserialized, where it returns without touching anything, and
        /// called on a variable of the set's own type rather than through the interface that declares
        /// it: rewriting matches on the type a call names, so a call through the interface would reach
        /// the real method and this test would pass without ever exercising the replacement.
        /// </remarks>
        [Fact(Timeout = 5000)]
        public void TestGenericHashSetOnDeserializationDataRace()
        {
            this.TestWithError(async () =>
            {
                var hashSet = new HashSet<int>
                {
                    1
                };

                Task t1 = Task.Run(() =>
                {
                    hashSet.OnDeserialization(null);
                });

                Task t2 = Task.Run(() =>
                {
                    hashSet.OnDeserialization(null);
                });

                await Task.WhenAll(t1, t2);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100),
            expectedError: $"Found write/write data race on '{typeof(HashSet<int>)}'.",
            replay: true);
        }
    }
}

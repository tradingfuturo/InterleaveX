// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests.DataRaceChecking
{
    public class GenericListTests : BaseBugFindingTest
    {
        public GenericListTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestGenericListAddDataRace()
        {
            this.TestWithError(async () =>
            {
                var list = new List<int>();

                Task t1 = Task.Run(() =>
                {
                    list.Add(1);
                });

                Task t2 = Task.Run(() =>
                {
                    list.Add(2);
                });

                await Task.WhenAll(t1, t2);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100),
            expectedError: $"Found write/write data race on '{typeof(List<int>)}'.",
            replay: true);
        }

        [Fact(Timeout = 5000)]
        public void TestGenericListIndex()
        {
            this.Test(async () =>
            {
                var list = new List<int>
                {
                    1
                };

                Task t1 = Task.Run(() =>
                {
                    _ = list[0];
                });

                Task t2 = Task.Run(() =>
                {
                    _ = list[0];
                });

                await Task.WhenAll(t1, t2);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        public void TestGenericListIndexDataRace()
        {
            this.TestWithError(async () =>
            {
                var list = new List<int>
                {
                    1
                };

                Task t1 = Task.Run(() =>
                {
                    _ = list[0];
                });

                Task t2 = Task.Run(() =>
                {
                    list[0] = 2;
                });

                await Task.WhenAll(t1, t2);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100),
            expectedError: $"Found read/write data race on '{typeof(List<int>)}'.",
            replay: true);
        }

        [Fact(Timeout = 5000)]
        public void TestGenericListCapacityDataRace()
        {
            this.TestWithError(async () =>
            {
                var list = new List<int>();

                Task t1 = Task.Run(() =>
                {
                    list.Capacity = 2;
                });

                Task t2 = Task.Run(() =>
                {
                    list.Capacity = 5;
                });

                await Task.WhenAll(t1, t2);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100),
            expectedError: $"Found write/write data race on '{typeof(List<int>)}'.",
            replay: true);
        }

        /// <summary>
        /// Checks that a search still answers what the list says, and not what the replacement for it
        /// happens to leave behind.
        /// </summary>
        /// <remarks>
        /// The replacements for these three searches used to return nothing at all, which rewriting
        /// accepted: it redirected the call and left the caller reading an index that the replacement
        /// never pushed. Every overload is searched here because each one was declared separately and
        /// so could drift separately.
        /// </remarks>
        [Fact(Timeout = 5000)]
        public void TestGenericListBinarySearch()
        {
            this.Test(() =>
            {
                var list = new List<int>
                {
                    1, 3, 5, 7
                };

                Assert.Equal(2, list.BinarySearch(5));
                Assert.Equal(-1, list.BinarySearch(0));
                Assert.Equal(2, list.BinarySearch(5, Comparer<int>.Default));
                Assert.Equal(3, list.BinarySearch(0, 4, 7, Comparer<int>.Default));
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }

        /// <summary>
        /// Checks that a search is guarded, which is what says the call reaches the replacement at all:
        /// the search above answers correctly whether or not it was ever redirected.
        /// </summary>
        [Fact(Timeout = 5000)]
        public void TestGenericListBinarySearchDataRace()
        {
            this.TestWithError(async () =>
            {
                var list = new List<int>
                {
                    1, 3, 5, 7
                };

                Task t1 = Task.Run(() =>
                {
                    list.Add(9);
                });

                Task t2 = Task.Run(() =>
                {
                    list.BinarySearch(5);
                });

                await Task.WhenAll(t1, t2);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100),
            expectedError: $"Found read/write data race on '{typeof(List<int>)}'.",
            replay: true);
        }
    }
}

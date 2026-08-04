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

        /// <summary>
        /// Reserving capacity reallocates the backing storage, so it is a write. It had no replacement
        /// at all, so it was not merely misclassified but entirely unguarded.
        /// </summary>
        [Fact(Timeout = 5000)]
        public void TestGenericListEnsureCapacityDataRace()
        {
            this.TestWithError(async () =>
            {
                var list = new List<int>();

                Task t1 = Task.Run(() =>
                {
                    list.EnsureCapacity(16);
                });

                Task t2 = Task.Run(() =>
                {
                    list.EnsureCapacity(32);
                });

                await Task.WhenAll(t1, t2);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100),
            expectedError: $"Found write/write data race on '{typeof(List<int>)}'.",
            replay: true);
        }

        /// <summary>
        /// The same race as above, with race checking on collection accesses turned off, which must
        /// silence it.
        /// </summary>
        /// <remarks>
        /// The concurrent collections honoured this setting from the start and the generic ones never
        /// read it, so turning it off used to quieten half of what it names.
        /// </remarks>
        [Fact(Timeout = 5000)]
        public void TestGenericListDataRaceCheckingCanBeDisabled()
        {
            this.Test(async () =>
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
            configuration: this.GetConfiguration().WithTestingIterations(100)
                .WithCollectionAccessRaceCheckingEnabled(false));
        }

        /// <summary>
        /// Copying a list reads the list it copies from, so a writer to the source races the copy. Only
        /// the list being constructed used to be guarded, and it is not the one at risk.
        /// </summary>
        [Fact(Timeout = 5000)]
        public void TestGenericListCopyingConstructorDataRace()
        {
            this.TestWithError(async () =>
            {
                var source = new List<int>
                {
                    1, 2, 3
                };

                Task t1 = Task.Run(() =>
                {
                    _ = new List<int>(source);
                });

                Task t2 = Task.Run(() =>
                {
                    source.Add(4);
                });

                await Task.WhenAll(t1, t2);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100),
            expectedError: $"Found read/write data race on '{typeof(List<int>)}'.",
            replay: true);
        }

        /// <summary>
        /// Appending one list to another reads the one appended, so a writer to it races the append.
        /// </summary>
        [Fact(Timeout = 5000)]
        public void TestGenericListAddRangeSourceDataRace()
        {
            this.TestWithError(async () =>
            {
                var source = new List<int>
                {
                    1, 2, 3
                };

                var target = new List<int>();

                Task t1 = Task.Run(() =>
                {
                    target.AddRange(source);
                });

                Task t2 = Task.Run(() =>
                {
                    source.Add(4);
                });

                await Task.WhenAll(t1, t2);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100),
            expectedError: $"Found read/write data race on '{typeof(List<int>)}'.",
            replay: true);
        }

        /// <summary>
        /// Appending a list to itself takes two frames on one list, which is one operation re-entering
        /// itself rather than two operations meeting, and must not be reported.
        /// </summary>
        /// <remarks>
        /// This works only because a frame whose owner already holds one is exempt. That exemption was
        /// introduced for reentrant comparers; guarding the source made it load-bearing here too, so a
        /// change to it would break this in a way that reads as a false report rather than as a
        /// regression in exemption.
        /// </remarks>
        [Fact(Timeout = 5000)]
        public void TestGenericListAddRangeOfItselfIsNotARace()
        {
            this.Test(() =>
            {
                var list = new List<int>
                {
                    1, 2, 3
                };

                list.AddRange(list);

                Assert.Equal(6, list.Count);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }
    }
}

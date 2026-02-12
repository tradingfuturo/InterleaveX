// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#if NET9_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    public class WhenEachTests : BaseBugFindingTest
    {
        public WhenEachTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestWhenEachWithArray()
        {
            this.Test(async () =>
            {
                var t1 = Task.Run(async () =>
                {
                    await Task.CompletedTask;
                    return 1;
                });
                var t2 = Task.Run(async () =>
                {
                    await Task.CompletedTask;
                    return 2;
                });

                var results = new List<int>();
                await foreach (var task in Task.WhenEach(t1, t2))
                {
                    results.Add(task.Result);
                }

                Specification.Assert(results.Count == 2, "Expected 2 results, got {0}.", results.Count);
            },
            this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        public void TestWhenEachNonGeneric()
        {
            this.Test(async () =>
            {
                int value = 0;
                var t1 = Task.Run(() => { value++; });
                var t2 = Task.Run(() => { value++; });

                int count = 0;
                await foreach (var task in Task.WhenEach(t1, t2))
                {
                    count++;
                }

                Specification.Assert(count == 2, "Expected 2 completions, got {0}.", count);
            },
            this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        public void TestWhenEachWithEnumerable()
        {
            this.Test(async () =>
            {
                var tasks = new List<Task<int>>
                {
                    Task.Run(async () =>
                    {
                        await Task.CompletedTask;
                        return 10;
                    }),
                    Task.Run(async () =>
                    {
                        await Task.CompletedTask;
                        return 20;
                    }),
                    Task.Run(async () =>
                    {
                        await Task.CompletedTask;
                        return 30;
                    })
                };

                var results = new List<int>();
                await foreach (var task in Task.WhenEach((IEnumerable<Task<int>>)tasks))
                {
                    results.Add(task.Result);
                }

                Specification.Assert(results.Count == 3, "Expected 3 results, got {0}.", results.Count);
            },
            this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        public void TestWhenEachSingleTask()
        {
            this.Test(async () =>
            {
                var t1 = Task.Run(async () =>
                {
                    await Task.CompletedTask;
                    return 42;
                });

                int count = 0;
                await foreach (var task in Task.WhenEach(t1))
                {
                    Specification.Assert(task.Result == 42, "Expected 42, got {0}.", task.Result);
                    count++;
                }

                Specification.Assert(count == 1, "Expected 1 result, got {0}.", count);
            },
            this.GetConfiguration().WithTestingIterations(10));
        }

        [Fact(Timeout = 5000)]
        public void TestWhenEachWithDuplicateTask()
        {
            this.Test(async () =>
            {
                var tcs = new TaskCompletionSource<int>();
                var setter = Task.Run(() => tcs.SetResult(1));

                int count = 0;
                await foreach (var task in Task.WhenEach(tcs.Task, tcs.Task))
                {
                    Specification.Assert(object.ReferenceEquals(task, tcs.Task),
                        "Expected duplicate entries to preserve task identity.");
                    count++;
                }

                await setter;
                Specification.Assert(count == 2, "Expected 2 completions, got {0}.", count);
            },
            this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        public void TestWhenEachYieldsFaultedAndCanceledTasks()
        {
            this.Test(async () =>
            {
                var source = new System.Threading.CancellationTokenSource();
                source.Cancel();

                Task success = Task.CompletedTask;
                Task fault = Task.FromException(new InvalidOperationException("boom"));
                Task cancel = Task.FromCanceled(source.Token);

                int count = 0;
                int successful = 0;
                int faulted = 0;
                int canceled = 0;
                await foreach (var task in Task.WhenEach(success, fault, cancel))
                {
                    count++;
                    if (task.Status is TaskStatus.RanToCompletion)
                    {
                        successful++;
                    }
                    else if (task.IsFaulted)
                    {
                        faulted++;
                    }
                    else if (task.IsCanceled)
                    {
                        canceled++;
                    }
                }

                Specification.Assert(count == 3, "Expected 3 completions, got {0}.", count);
                Specification.Assert(successful == 1, "Expected 1 successful task, got {0}.", successful);
                Specification.Assert(faulted == 1, "Expected 1 faulted task, got {0}.", faulted);
                Specification.Assert(canceled == 1, "Expected 1 canceled task, got {0}.", canceled);
            },
            this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        public void TestWhenEachThrowsOnNullArray()
        {
            this.Test(() =>
            {
                bool isThrown = false;
                try
                {
                    _ = Task.WhenEach((Task[])null);
                }
                catch (ArgumentNullException)
                {
                    isThrown = true;
                }

                Specification.Assert(isThrown, "Expected ArgumentNullException to be thrown.");
            },
            this.GetConfiguration().WithTestingIterations(10));
        }

        [Fact(Timeout = 5000)]
        public void TestWhenEachThrowsOnNullTaskInArray()
        {
            this.Test(() =>
            {
                bool isThrown = false;
                try
                {
                    _ = Task.WhenEach(Task.CompletedTask, null);
                }
                catch (ArgumentException)
                {
                    isThrown = true;
                }

                Specification.Assert(isThrown, "Expected ArgumentException to be thrown.");
            },
            this.GetConfiguration().WithTestingIterations(10));
        }
    }
}
#endif

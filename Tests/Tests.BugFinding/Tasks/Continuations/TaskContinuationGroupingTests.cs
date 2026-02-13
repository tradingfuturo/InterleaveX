// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Specifications;
using Microsoft.Coyote.SystematicTesting;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    public class TaskContinuationGroupingTests : BaseBugFindingTest
    {
        public TaskContinuationGroupingTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestTaskContinuationGroupingWithCompletedTask()
        {
            this.Test(async () =>
            {
                OperationGroup originalGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                await Task.CompletedTask;
                OperationGroup newGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                Specification.Assert(newGroup == originalGroup,
                    $"The new '{newGroup}' and original '{originalGroup}' groups differ.");
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        public void TestTaskContinuationGroupingWithYield()
        {
            this.Test(async () =>
            {
                OperationGroup originalGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                await Task.Yield();
                OperationGroup newGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                Specification.Assert(newGroup == originalGroup,
                    $"The new '{newGroup}' and original '{originalGroup}' groups differ.");
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        public void TestTaskContinuationGroupingWithDelay()
        {
            this.Test(async () =>
            {
                OperationGroup originalGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                await Task.Delay(10);
                OperationGroup newGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                Specification.Assert(newGroup == originalGroup,
                    $"The new '{newGroup}' and original '{originalGroup}' groups differ.");
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        public void TestTaskContinuationGroupingWithTaskRun()
        {
            this.Test(async () =>
            {
                OperationGroup originalGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                OperationGroup taskGroup = null;
                Task task = Task.Run(() =>
                {
                    taskGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                });

                await task;

                OperationGroup newGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                Specification.Assert(newGroup == originalGroup,
                    $"The new '{newGroup}' and original '{originalGroup}' groups differ.");
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        public void TestTaskContinuationGroupingWithAsyncTaskRun()
        {
            this.Test(async () =>
            {
                OperationGroup originalGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                OperationGroup taskGroup = null;
                Task task = Task.Run(async () =>
                {
                    OperationGroup originalTaskGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                    await Task.CompletedTask;
                    taskGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                    Specification.Assert(taskGroup == originalTaskGroup,
                        $"The task '{taskGroup}' and original task '{originalTaskGroup}' groups differ.");
                });

                await task;

                OperationGroup newGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                Specification.Assert(newGroup == originalGroup,
                    $"The new '{newGroup}' and original '{originalGroup}' groups differ.");
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        public void TestTaskContinuationGroupingWithYieldedTaskRun()
        {
            this.Test(async () =>
            {
                OperationGroup originalGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                OperationGroup taskGroup = null;
                Task task = Task.Run(async () =>
                {
                    OperationGroup originalTaskGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                    await Task.Yield();
                    taskGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                    Specification.Assert(taskGroup == originalTaskGroup,
                        $"The task '{taskGroup}' and original task '{originalTaskGroup}' groups differ.");
                });

                await task;

                OperationGroup newGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                Specification.Assert(newGroup == originalGroup,
                    $"The new '{newGroup}' and original '{originalGroup}' groups differ.");
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        public void TestTaskContinuationGroupingWithDelayedTaskRun()
        {
            this.Test(async () =>
            {
                OperationGroup originalGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                OperationGroup taskGroup = null;
                Task task = Task.Run(async () =>
                {
                    OperationGroup originalTaskGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                    await Task.Delay(10);
                    taskGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                    Specification.Assert(taskGroup == originalTaskGroup,
                        $"The task '{taskGroup}' and original task '{originalTaskGroup}' groups differ.");
                });

                await task;

                OperationGroup newGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                Specification.Assert(newGroup == originalGroup,
                    $"The new '{newGroup}' and original '{originalGroup}' groups differ.");
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        public void TestTaskContinuationGroupingWithNestedAsyncTaskRun()
        {
            this.Test(async () =>
            {
                OperationGroup originalGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                OperationGroup taskGroup = null;
                Task task = Task.Run(async () =>
                {
                    OperationGroup originalTaskGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                    OperationGroup innerTaskGroup = null;
                    Task innerTask = Task.Run(async () =>
                    {
                        OperationGroup originalInnerTaskGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                        await Task.CompletedTask;
                        innerTaskGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                        Specification.Assert(innerTaskGroup == originalInnerTaskGroup,
                            $"The inner task '{innerTaskGroup}' and original inner task '{originalInnerTaskGroup}' groups differ.");
                    });

                    await innerTask;

                    taskGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                    Specification.Assert(taskGroup == originalTaskGroup,
                        $"The task '{taskGroup}' and original task '{originalTaskGroup}' groups differ.");
                });

                await task;

                OperationGroup newGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                Specification.Assert(newGroup == originalGroup,
                    $"The new '{newGroup}' and original '{originalGroup}' groups differ.");
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        public void TestTaskContinuationGroupingWithNestedYieldedTaskRun()
        {
            this.Test(async () =>
            {
                OperationGroup originalGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                OperationGroup taskGroup = null;
                Task task = Task.Run(async () =>
                {
                    OperationGroup originalTaskGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                    OperationGroup innerTaskGroup = null;
                    Task innerTask = Task.Run(async () =>
                    {
                        OperationGroup originalInnerTaskGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                        await Task.Yield();
                        innerTaskGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                        Specification.Assert(innerTaskGroup == originalInnerTaskGroup,
                            $"The inner task '{innerTaskGroup}' and original inner task '{originalInnerTaskGroup}' groups differ.");
                    });

                    await innerTask;

                    taskGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                    Specification.Assert(taskGroup == originalTaskGroup,
                        $"The task '{taskGroup}' and original task '{originalTaskGroup}' groups differ.");
                });

                await task;

                OperationGroup newGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                Specification.Assert(newGroup == originalGroup,
                    $"The new '{newGroup}' and original '{originalGroup}' groups differ.");
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        public void TestTaskContinuationGroupingWithNestedDelayedTaskRun()
        {
            this.Test(async () =>
            {
                OperationGroup originalGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                OperationGroup taskGroup = null;
                Task task = Task.Run(async () =>
                {
                    OperationGroup originalTaskGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                    OperationGroup innerTaskGroup = null;
                    Task innerTask = Task.Run(async () =>
                    {
                        OperationGroup originalInnerTaskGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                        await Task.Delay(10);
                        innerTaskGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                        Specification.Assert(innerTaskGroup == originalInnerTaskGroup,
                            $"The inner task '{innerTaskGroup}' and original inner task '{originalInnerTaskGroup}' groups differ.");
                    });

                    await innerTask;

                    taskGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                    Specification.Assert(taskGroup == originalTaskGroup,
                        $"The task '{taskGroup}' and original task '{originalTaskGroup}' groups differ.");
                });

                await task;

                OperationGroup newGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                Specification.Assert(newGroup == originalGroup,
                    $"The new '{newGroup}' and original '{originalGroup}' groups differ.");
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        public void TestTaskContinuationGroupingWithSequentialAwaits()
        {
            this.Test(async () =>
            {
                OperationGroup originalGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;

                await Task.Run(() => { });
                OperationGroup groupAfterFirst = CoyoteRuntime.Current.GetExecutingOperation().Group;
                Specification.Assert(groupAfterFirst == originalGroup,
                    $"After first await: group '{groupAfterFirst}' differs from original '{originalGroup}'.");

                await Task.Run(() => { });
                OperationGroup groupAfterSecond = CoyoteRuntime.Current.GetExecutingOperation().Group;
                Specification.Assert(groupAfterSecond == originalGroup,
                    $"After second await: group '{groupAfterSecond}' differs from original '{originalGroup}'.");

                await Task.Run(() => { });
                OperationGroup groupAfterThird = CoyoteRuntime.Current.GetExecutingOperation().Group;
                Specification.Assert(groupAfterThird == originalGroup,
                    $"After third await: group '{groupAfterThird}' differs from original '{originalGroup}'.");
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        public void TestTaskContinuationGroupingWithSequentialAwaitsAndPCT()
        {
            this.Test(async () =>
            {
                OperationGroup originalGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;

                await Task.Run(() => { });
                OperationGroup groupAfterFirst = CoyoteRuntime.Current.GetExecutingOperation().Group;
                Specification.Assert(groupAfterFirst == originalGroup,
                    $"After first await: group '{groupAfterFirst}' differs from original '{originalGroup}'.");

                await Task.Run(() => { });
                OperationGroup groupAfterSecond = CoyoteRuntime.Current.GetExecutingOperation().Group;
                Specification.Assert(groupAfterSecond == originalGroup,
                    $"After second await: group '{groupAfterSecond}' differs from original '{originalGroup}'.");

                await Task.Run(() => { });
                OperationGroup groupAfterThird = CoyoteRuntime.Current.GetExecutingOperation().Group;
                Specification.Assert(groupAfterThird == originalGroup,
                    $"After third await: group '{groupAfterThird}' differs from original '{originalGroup}'.");
            },
            configuration: this.GetConfiguration()
                .WithTestingIterations(100)
                .WithPrioritizationStrategy());
        }

        [Fact(Timeout = 5000)]
        public void TestTaskContinuationGroupingWithFaultedTask()
        {
            this.Test(async () =>
            {
                OperationGroup originalGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                try
                {
                    await Task.Run(() => throw new InvalidOperationException("test"));
                }
                catch (InvalidOperationException)
                {
                    // Expected.
                }

                OperationGroup newGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                Specification.Assert(newGroup == originalGroup,
                    $"After faulted await: group '{newGroup}' differs from original '{originalGroup}'.");
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        public void TestTaskContinuationGroupingWithCancelledTask()
        {
            this.Test(async () =>
            {
                OperationGroup originalGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                try
                {
                    await Task.Run(() => cts.Token.ThrowIfCancellationRequested(), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Expected.
                }

                OperationGroup newGroup = CoyoteRuntime.Current.GetExecutingOperation().Group;
                Specification.Assert(newGroup == originalGroup,
                    $"After cancelled await: group '{newGroup}' differs from original '{originalGroup}'.");
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }
    }
}

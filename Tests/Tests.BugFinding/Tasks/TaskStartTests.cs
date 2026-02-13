// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Threading.Tasks;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    public class TaskStartTests : BaseBugFindingTest
    {
        public TaskStartTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestStartParallelTask()
        {
            this.Test(async () =>
            {
                SharedEntry entry = new SharedEntry();
                var task = new Task(() =>
                {
                    entry.Value = 5;
                });

                task.Start();
                await task;

                AssertSharedEntryValue(entry, 5);
            },
            configuration: this.GetConfiguration().WithTestingIterations(200));
        }

        [Fact(Timeout = 5000)]
        public void TestStartParallelTaskFailure()
        {
            this.TestWithError(async () =>
            {
                SharedEntry entry = new SharedEntry();
                var task = new Task(() =>
                {
                    entry.Value = 3;
                });

                task.Start();
                await task;

                AssertSharedEntryValue(entry, 5);
            },
            configuration: this.GetConfiguration().WithTestingIterations(200),
            expectedError: "Value is 3 instead of 5.",
            replay: true);
        }

        [Fact(Timeout = 5000)]
        public void TestStartParallelTaskWithResult()
        {
            this.Test(async () =>
            {
                SharedEntry entry = new SharedEntry();
                var task = new Task<int>(() =>
                {
                    entry.Value = 5;
                    return entry.Value;
                });

                task.Start();
                int value = await task;

                Specification.Assert(value == 5, "Value is {0} instead of 5.", value);
            },
            configuration: this.GetConfiguration().WithTestingIterations(200));
        }

        [Fact(Timeout = 5000)]
        public void TestStartParallelTaskWithResultFailure()
        {
            this.TestWithError(async () =>
            {
                SharedEntry entry = new SharedEntry();
                var task = new Task<int>(() =>
                {
                    entry.Value = 3;
                    return entry.Value;
                });

                task.Start();
                int value = await task;

                Specification.Assert(value == 5, "Value is {0} instead of 5.", value);
            },
            configuration: this.GetConfiguration().WithTestingIterations(200),
            expectedError: "Value is 3 instead of 5.",
            replay: true);
        }

        [Fact(Timeout = 5000)]
        public void TestRunSynchronouslyParallelTask()
        {
            this.Test(async () =>
            {
                SharedEntry entry = new SharedEntry();
                var task = new Task(() =>
                {
                    entry.Value = 5;
                });

                task.RunSynchronously();
                await Task.CompletedTask;

                AssertSharedEntryValue(entry, 5);
            },
            configuration: this.GetConfiguration().WithTestingIterations(200));
        }

        [Fact(Timeout = 5000)]
        public void TestStartTaskWithState()
        {
            this.Test(async () =>
            {
                SharedEntry entry = new SharedEntry();
                var task = new Task(state =>
                {
                    entry.Value = (int)state;
                }, 5);

                task.Start();
                await task;

                AssertSharedEntryValue(entry, 5);
            },
            configuration: this.GetConfiguration().WithTestingIterations(200));
        }

        [Fact(Timeout = 5000)]
        public void TestStartMultipleParallelTasks()
        {
            this.TestWithError(async () =>
            {
                SharedEntry entry = new SharedEntry();
                var task1 = new Task(() =>
                {
                    entry.Value = 3;
                });

                var task2 = new Task(() =>
                {
                    entry.Value = 5;
                });

                task1.Start();
                task2.Start();
                await Task.WhenAll(task1, task2);

                AssertSharedEntryValue(entry, 5);
            },
            configuration: this.GetConfiguration().WithTestingIterations(200),
            expectedError: "Value is 3 instead of 5.",
            replay: true);
        }
    }
}

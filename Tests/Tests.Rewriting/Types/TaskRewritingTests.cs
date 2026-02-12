// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Rewriting.Tests
{
    public class TaskRewritingTests : BaseRewritingTest
    {
        public TaskRewritingTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestRewritingTaskWhenAll()
        {
            Task.WhenAll(Task.CompletedTask);
        }

        [Fact(Timeout = 5000)]
        public void TestRewritingGenericTaskWhenAll()
        {
            Task.WhenAll(Task.FromResult(1));
        }

        [Fact(Timeout = 5000)]
        public void TestRewritingTaskWhenAny()
        {
            Task.WhenAny(Task.CompletedTask);
        }

        [Fact(Timeout = 5000)]
        public void TestRewritingGenericTaskWhenAny()
        {
            Task.WhenAny(Task.FromResult(1));
        }

#if NET9_0_OR_GREATER
        [Fact(Timeout = 5000)]
        public void TestRewritingTaskWhenAllWithSpan()
        {
            Task[] tasks = new[] { Task.CompletedTask };
            ReadOnlySpan<Task> taskSpan = tasks;
            Task.WhenAll(taskSpan);
        }

        [Fact(Timeout = 5000)]
        public void TestRewritingGenericTaskWhenAllWithSpan()
        {
            Task<int>[] tasks = new[] { Task.FromResult(1) };
            ReadOnlySpan<Task<int>> taskSpan = tasks;
            Task.WhenAll(taskSpan);
        }

        [Fact(Timeout = 5000)]
        public void TestRewritingTaskWhenAnyWithSpan()
        {
            Task[] tasks = new[] { Task.CompletedTask };
            ReadOnlySpan<Task> taskSpan = tasks;
            Task.WhenAny(taskSpan);
        }

        [Fact(Timeout = 5000)]
        public void TestRewritingGenericTaskWhenAnyWithSpan()
        {
            Task<int>[] tasks = new[] { Task.FromResult(1) };
            ReadOnlySpan<Task<int>> taskSpan = tasks;
            Task.WhenAny(taskSpan);
        }

        [Fact(Timeout = 5000)]
        public void TestRewritingTaskWaitAllWithSpan()
        {
            Task[] tasks = new[] { Task.CompletedTask };
            ReadOnlySpan<Task> taskSpan = tasks;
            Task.WaitAll(taskSpan);
        }
#endif
    }
}

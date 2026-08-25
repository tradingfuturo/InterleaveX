// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Tests.Common;
using Xunit;
using Xunit.Abstractions;
using CoyotePeriodicTimer = Microsoft.Coyote.Rewriting.Types.Threading.PeriodicTimer;
using CoyoteTask = Microsoft.Coyote.Rewriting.Types.Threading.Tasks.Task;
using SystemPeriodicTimer = System.Threading.PeriodicTimer;

namespace Microsoft.Coyote.Runtime.Tests
{
    /// <summary>
    /// Contract tests for the modeled APIs that delegate timer ownership to a custom time provider.
    /// </summary>
    public class TimeProviderTimerContractTests : BaseRuntimeTest
    {
        public TimeProviderTimerContractTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestProviderDelayTimerExpiryCanInlineExecuteSynchronouslyContinuation()
        {
            this.RunSystematicTest(() =>
            {
                var provider = new RecordingTimeProvider();
                Task delay = CoyoteTask.Delay(TimeSpan.FromSeconds(1), provider);
                var scheduler = new InlineRecordingTaskScheduler();
                Task continuation = delay.ContinueWith(_ => { }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, scheduler);

                provider.LastTimer.Fire();

                Assert.True(delay.IsCompletedSuccessfully);
                Assert.Equal(1, scheduler.InlineCount);
                Assert.Equal(0, scheduler.QueuedCount);
                Assert.True(continuation.IsCompletedSuccessfully);
            }, this.GetConfiguration().WithTestingIterations(1));
        }

        [Fact(Timeout = 5000)]
        public void TestProviderDelayCancellationQueuesExecuteSynchronouslyContinuation()
        {
            this.RunSystematicTest(() =>
            {
                var provider = new RecordingTimeProvider();
                using var source = new CancellationTokenSource();
                Task delay = CoyoteTask.Delay(TimeSpan.FromSeconds(1), provider, source.Token);
                var scheduler = new InlineRecordingTaskScheduler();
                Task continuation = delay.ContinueWith(_ => { }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, scheduler);

                source.Cancel();

                Assert.True(delay.IsCanceled);
                Assert.Equal(0, scheduler.InlineCount);
                Assert.Equal(1, scheduler.QueuedCount);
                scheduler.ExecuteQueuedTasks();
                Assert.True(continuation.IsCompletedSuccessfully);
            }, this.GetConfiguration().WithTestingIterations(1));
        }

        [Fact(Timeout = 5000)]
        public void TestInfiniteDelayCancellationQueuesExecuteSynchronouslyContinuation()
        {
            this.RunSystematicTest(() =>
            {
                using var source = new CancellationTokenSource();
                Task delay = CoyoteTask.Delay(Timeout.InfiniteTimeSpan, source.Token);
                var scheduler = new InlineRecordingTaskScheduler();
                Task continuation = delay.ContinueWith(_ => { }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, scheduler);

                source.Cancel();

                Assert.True(delay.IsCanceled);
                Assert.Equal(0, scheduler.InlineCount);
                Assert.Equal(1, scheduler.QueuedCount);
                scheduler.ExecuteQueuedTasks();
                Assert.True(continuation.IsCompletedSuccessfully);
            }, this.GetConfiguration().WithTestingIterations(1));
        }

        [Fact(Timeout = 5000)]
        public void TestLateProviderCallbackAfterRuntimeTeardownDoesNotThrow()
        {
            var provider = new RecordingTimeProvider();
            CoyoteRuntime runtime = null;
            SystemPeriodicTimer timer = null;
            try
            {
                this.RunSystematicTest(() =>
                {
                    runtime = CoyoteRuntime.Current;
                    timer = CoyotePeriodicTimer.Create(TimeSpan.FromSeconds(1), provider);
                }, this.GetConfiguration()
                    .WithTestingIterations(1)
                    .WithPartiallyControlledConcurrencyAllowed());

                Assert.NotNull(runtime);
                Assert.NotNull(timer);
                Assert.NotNull(provider.LastTimer);
                Assert.True(runtime.HasExecutionEnded);
                Assert.Null(RunOnUncontrolledThreadAndCapture(provider.LastTimer.Fire));
            }
            finally
            {
                if (timer != null)
                {
                    CoyotePeriodicTimer.Dispose(timer);
                }
            }
        }

        private static Exception RunOnUncontrolledThreadAndCapture(Action action)
        {
            Exception failure = null;
            using (ExecutionContext.SuppressFlow())
            {
                var thread = new Thread(() =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                })
                {
                    IsBackground = true
                };
                thread.Start();
                if (!thread.Join(millisecondsTimeout: 2000))
                {
                    throw new TimeoutException("The uncontrolled provider callback did not complete.");
                }
            }

            return failure;
        }

        private sealed class InlineRecordingTaskScheduler : TaskScheduler
        {
            private readonly Queue<Task> QueuedTasks = new Queue<Task>();

            internal int InlineCount { get; private set; }

            internal int QueuedCount => this.QueuedTasks.Count;

            protected override IEnumerable<Task> GetScheduledTasks() => this.QueuedTasks.ToArray();

            protected override void QueueTask(Task task) => this.QueuedTasks.Enqueue(task);

            protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
            {
                this.InlineCount++;
                return this.TryExecuteTask(task);
            }

            internal void ExecuteQueuedTasks()
            {
                while (this.QueuedTasks.Count > 0)
                {
                    _ = this.TryExecuteTask(this.QueuedTasks.Dequeue());
                }
            }
        }
    }
}
#endif

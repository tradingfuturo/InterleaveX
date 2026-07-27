// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Threading;
using Xunit;
using Xunit.Abstractions;
using CoyoteTypes = Microsoft.Coyote.Rewriting.Types;
using SystemTasks = System.Threading.Tasks;

namespace Microsoft.Coyote.Runtime.Tests
{
    /// <summary>
    /// Tests that controlled threads are reused across operations and testing iterations, and that they
    /// are retired rather than reused whenever reuse would be unsafe.
    /// </summary>
    public class ControlledThreadPoolTests : BaseRuntimeTest
    {
        public ControlledThreadPoolTests(ITestOutputHelper output)
            : base(output)
        {
        }

        /// <summary>
        /// Returns a configuration with controlled thread pooling enabled, which is off by default.
        /// </summary>
        private static Configuration CreateConfiguration() =>
            Configuration.Create().WithControlledThreadPoolingEnabled();

        [Fact(Timeout = 30000)]
        public void TestThreadsAreReusedAcrossIterations()
        {
            const uint iterations = 100;
            const int tasksPerIteration = 4;

            long createdBefore = ControlledThreadPool.ThreadsCreated;

            Configuration configuration = CreateConfiguration()
                .WithTestingIterations(iterations)
                .WithTestIterationsRunToCompletion();

            this.RunSystematicTest(() =>
            {
                var tasks = new SystemTasks.Task[tasksPerIteration];
                for (int i = 0; i < tasksPerIteration; i++)
                {
                    tasks[i] = CoyoteTypes.Threading.Tasks.Task.Run(() => { });
                }

                CoyoteTypes.Threading.Tasks.Task.WaitAll(tasks);
            },
            configuration);

            long created = ControlledThreadPool.ThreadsCreated - createdBefore;

            // Without reuse this test creates a thread for the test method and one per task in every
            // iteration, plus one per continuation. The bound below is deliberately loose, because the
            // exact number depends on how many operations happen to be live at once, but it is far below
            // the unpooled count and so fails loudly if reuse stops happening.
            long unpooledLowerBound = iterations * (tasksPerIteration + 1);
            Assert.True(created < unpooledLowerBound / 4,
                $"Created {created} threads for {iterations} iterations of {tasksPerIteration} tasks, " +
                $"which is not meaningfully below the {unpooledLowerBound} that no reuse would need.");
        }

        [Fact(Timeout = 30000)]
        public void TestThreadsAreReusedAcrossOperationsInOneIteration()
        {
            const int tasks = 200;

            long createdBefore = ControlledThreadPool.ThreadsCreated;

            Configuration configuration = CreateConfiguration()
                .WithTestingIterations(1)
                .WithTestIterationsRunToCompletion();

            // Each task completes before the next is created, so a single thread can serve all of them.
            this.RunSystematicTest(() =>
            {
                for (int i = 0; i < tasks; i++)
                {
                    CoyoteTypes.Threading.Tasks.Task.Run(() => { }).Wait();
                }
            },
            configuration);

            long created = ControlledThreadPool.ThreadsCreated - createdBefore;
            Assert.True(created < tasks / 4,
                $"Created {created} threads for {tasks} sequential tasks, which indicates that threads " +
                "are not being reused within an iteration.");
        }

        [Fact(Timeout = 30000)]
        public void TestIterationsSucceedAfterMaxStepsBoundIsReached()
        {
            // Reaching the max steps bound detaches the runtime, which interrupts every live operation.
            // The threads those operations were running must retire instead of returning to the pool,
            // because an interrupt latches until the thread next waits and would otherwise be raised
            // inside an unrelated operation later on.
            Configuration configuration = CreateConfiguration()
                .WithTestingIterations(20)
                .WithTestIterationsRunToCompletion()
                .WithMaxSchedulingSteps(100);

            this.RunSystematicTest(() =>
            {
                var t = CoyoteTypes.Threading.Tasks.Task.Run(() =>
                {
                    // Creates more operations than the step bound allows, so the iteration is always cut
                    // short with several of them still live and about to be interrupted.
                    for (int i = 0; i < 200; i++)
                    {
                        CoyoteTypes.Threading.Tasks.Task.Run(() => { });
                    }
                });

                t.Wait();
            },
            configuration);

            // Reaching here without a hang or a spurious failure is the assertion: every iteration after
            // the first ran on threads drawn from a pool that had just been through a detach.
        }

        [Fact(Timeout = 30000)]
        public void TestIterationsSucceedAfterDeadlockDetection()
        {
            // A deadlock detaches the runtime from the background monitor thread rather than from the
            // operation itself, which is a different interrupt path to the one above.
            Configuration configuration = CreateConfiguration()
                .WithTestingIterations(20)
                .WithTestIterationsRunToCompletion()
                .WithDeadlockTimeout(10)
                .WithPotentialDeadlocksReportedAsBugs(false);

            this.RunSystematicTest(() =>
            {
                var semaphore = new SemaphoreSlim(0, 1);
                CoyoteTypes.Threading.SemaphoreSlim.Wait(semaphore);
            },
            configuration);
        }

        [Fact(Timeout = 30000)]
        public void TestPoolIsDrainedAfterTestCompletes()
        {
            Configuration configuration = CreateConfiguration()
                .WithTestingIterations(20)
                .WithTestIterationsRunToCompletion();

            this.RunSystematicTest(() =>
            {
                var t = CoyoteTypes.Threading.Tasks.Task.Run(() => { });
                t.Wait();
            },
            configuration);

            // The engine drains the pool when it finishes, so nothing is retained for the host process.
            Assert.True(ControlledThreadPool.Instance.IdleThreadCount is 0,
                $"Expected the pool to be empty after the test completed, but it retains " +
                $"{ControlledThreadPool.Instance.IdleThreadCount} threads.");
        }
    }
}

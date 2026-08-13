// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

#if NET
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Specifications;
using Microsoft.Extensions.Hosting;
using Xunit;
using Xunit.Abstractions;
using SystemBackgroundService = Microsoft.Extensions.Hosting.BackgroundService;

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// Tests for the controlled <see cref="BackgroundService"/> model. The real <c>StopAsync</c> waits on
    /// <c>WhenAny(executeTask, Delay(Infinite, token))</c> inside an assembly the rewriter does not visit,
    /// so shutdown hangs off a task the scheduler has no record of: every test that waits for a hosted
    /// service to stop reports a deadlock, and the <c>ExecuteAsync</c> half of one is never explored at all.
    /// </summary>
    public class BackgroundServiceTests : BaseBugFindingTest
    {
        public BackgroundServiceTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestStartThenStopCompletes()
        {
            // The case that cannot run unmodelled: shutdown genuinely waits for the loop.
            this.Test(async () =>
            {
                var service = new CooperativeService();
                await service.StartAsync(CancellationToken.None);
                await service.StopAsync(CancellationToken.None);

#if !NET10_0_OR_GREATER
                // Through .NET 9 StartAsync invokes ExecuteAsync synchronously until its first await, so the
                // loop must have entered before StartAsync returns. .NET 10 queues ExecuteAsync instead, and
                // an immediate StopAsync is allowed to cancel that queued work before it starts.
                Specification.Assert(service.Ticks > 0, "The service loop never ran.");
#endif
                Specification.Assert(service.ExecuteTask != null, "The service execution task was not published.");
                Specification.Assert(service.ExecuteTask.IsCompleted, "The service loop was still running.");
            },
            this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        public void TestExecuteTaskIsVisibleWhileRunning()
        {
            // The model owns the execute task, so the property has to answer from the model. A null here
            // would tell a caller the loop had never started when it is running.
            this.Test(async () =>
            {
                var service = new CooperativeService();
                await service.StartAsync(CancellationToken.None);

                Specification.Assert(service.ExecuteTask != null, "ExecuteTask was null while the service was running.");

                await service.StopAsync(CancellationToken.None);
            });
        }

        [Fact(Timeout = 5000)]
        public void TestUnsynchronizedStateRacesWithShutdown()
        {
            // The loop writes a field that shutdown reads, with nothing ordering them — the shape the model
            // exists to make reachable at all.
            this.TestWithError(async () =>
            {
                var service = new RacingService();
                await service.StartAsync(CancellationToken.None);
                Task stop = Task.Run(() => service.StopAsync(CancellationToken.None));
                service.Observed = service.Counter;
                await stop;

                Specification.Assert(service.Observed == service.Counter, "The loop advanced under shutdown.");
            },
            configuration: this.GetConfiguration().WithTestingIterations(200),
            expectedError: "The loop advanced under shutdown.",
            replay: true);
        }

        [Fact(Timeout = 5000)]
        public void TestServiceThatIgnoresItsTokenDeadlocks()
        {
            // A loop that never observes its stopping token never stops, and shutdown waits forever. That is
            // a hang, and the model has to report it as one rather than let shutdown walk past a live loop —
            // which is exactly what an infinite Task.Delay would do, since ScheduleDelay resolves on its own.
            this.TestWithError(async () =>
            {
                var service = new UnstoppableService();
                await service.StartAsync(CancellationToken.None);
                await service.StopAsync(CancellationToken.None);
            },
            errorChecker: (e) => Assert.StartsWith("Deadlock detected.", e));
        }

        [Fact(Timeout = 5000)]
        public void TestOverriddenStopAsyncStillRunsWhenCalledThroughTheBaseType()
        {
            // The rewriter re-emits a rewritten callvirt as a static call, so a call through a variable typed
            // as the base class must not skip the override.
            this.Test(async () =>
            {
                var service = new OverridingService();
                SystemBackgroundService asBase = service;
                await asBase.StartAsync(CancellationToken.None);
                await asBase.StopAsync(CancellationToken.None);

                Specification.Assert(service.OverrideRan, "The StopAsync override was skipped.");
                Specification.Assert(service.ExecuteTask.IsCompleted, "base.StopAsync did not wait for the loop.");
            },
            this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        public void TestOverrideThatNeverCallsBaseIsNotReentered()
        {
            // An override is under no obligation to call base, and the re-entrancy flag that makes the case
            // above work must not leak into the next stop.
            this.Test(async () =>
            {
                var service = new SwallowingService();
                SystemBackgroundService asBase = service;
                await asBase.StartAsync(CancellationToken.None);
                await asBase.StopAsync(CancellationToken.None);
                await asBase.StopAsync(CancellationToken.None);

                Specification.Assert(service.OverrideCount == 2, "The StopAsync override ran {0} times, not twice.",
                    service.OverrideCount);
            });
        }

        /// <summary>A loop that stops when it is asked to.</summary>
        private class CooperativeService : SystemBackgroundService
        {
            internal int Ticks;

            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    this.Ticks++;
                    await Task.Yield();
                }
            }
        }

        /// <summary>A loop whose state is read by whoever is shutting it down.</summary>
        private class RacingService : SystemBackgroundService
        {
            internal int Counter;
            internal int Observed;

            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Yield();
                    this.Counter++;
                }
            }
        }

        /// <summary>
        /// A loop that never observes its stopping token. It parks on a signal nobody sends rather than
        /// spinning: a spin would be truncated by the step bound and prove nothing, and a service wedged on
        /// a dependency that never answers is the shape this actually takes in production.
        /// </summary>
        private class UnstoppableService : SystemBackgroundService
        {
            private readonly TaskCompletionSource Wedged = new TaskCompletionSource();

            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                await this.Wedged.Task;
            }
        }

        /// <summary>Overrides StopAsync and calls base, the ordinary shape.</summary>
        private class OverridingService : CooperativeService
        {
            internal bool OverrideRan;

            public override Task StopAsync(CancellationToken cancellationToken)
            {
                this.OverrideRan = true;
                return base.StopAsync(cancellationToken);
            }
        }

        /// <summary>Overrides StopAsync and never calls base.</summary>
        private class SwallowingService : CooperativeService
        {
            internal int OverrideCount;

            public override Task StopAsync(CancellationToken cancellationToken)
            {
                this.OverrideCount++;
                return Task.CompletedTask;
            }
        }
    }
}
#endif

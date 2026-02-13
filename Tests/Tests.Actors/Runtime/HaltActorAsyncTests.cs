// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Actors.Tests
{
    public class HaltActorAsyncTests : BaseActorTest
    {
        public HaltActorAsyncTests(ITestOutputHelper output)
            : base(output)
        {
        }

        private class SimpleActor : Actor
        {
        }

        private class SetFlagOnHaltActor : Actor
        {
            internal static volatile bool HaltCalled;

            protected override Task OnHaltAsync(Event e)
            {
                HaltCalled = true;
                return Task.CompletedTask;
            }
        }

        private class SlowHaltActor : Actor
        {
            internal static volatile bool HaltCompleted;

            protected override async Task OnHaltAsync(Event e)
            {
                await Task.Delay(200);
                HaltCompleted = true;
            }
        }

        private class SimpleStateMachine : StateMachine
        {
            [Start]
            private class Init : State
            {
            }
        }

        private class MultiActorHaltTracker : Actor
        {
            internal static volatile int HaltCount;

            protected override Task OnHaltAsync(Event e)
            {
                System.Threading.Interlocked.Increment(ref HaltCount);
                return Task.CompletedTask;
            }
        }

        [Fact(Timeout = 5000)]
        public async Task TestHaltActorAsync()
        {
            await this.RunAsync(async r =>
            {
                var actorId = r.CreateActor(typeof(SimpleActor));
                await r.HaltActorAsync(actorId);
                Assert.Equal(ActorExecutionStatus.None, r.GetActorExecutionStatus(actorId));
            });
        }

        [Fact(Timeout = 5000)]
        public async Task TestHaltActorAsyncAwaitsOnHalt()
        {
            await this.RunAsync(async r =>
            {
                SetFlagOnHaltActor.HaltCalled = false;
                var actorId = r.CreateActor(typeof(SetFlagOnHaltActor));
                await r.HaltActorAsync(actorId);
                Assert.True(SetFlagOnHaltActor.HaltCalled);
            });
        }

        [Fact(Timeout = 5000)]
        public async Task TestHaltActorAsyncAwaitsSlowOnHalt()
        {
            await this.RunAsync(async r =>
            {
                SlowHaltActor.HaltCompleted = false;
                var actorId = r.CreateActor(typeof(SlowHaltActor));
                await r.HaltActorAsync(actorId);
                Assert.True(SlowHaltActor.HaltCompleted);
            });
        }

        [Fact(Timeout = 5000)]
        public async Task TestHaltActorAsyncAlreadyHalted()
        {
            await this.RunAsync(async r =>
            {
                var tcs = new TaskCompletionSource<bool>();
                var actorId = r.CreateActor(typeof(SimpleActor));
                r.OnActorHalted += (id) =>
                {
                    if (id.Equals(actorId))
                    {
                        tcs.TrySetResult(true);
                    }
                };

                r.SendEvent(actorId, HaltEvent.Instance);
                await this.WaitAsync(tcs.Task);

                // Actor is already halted; this should complete immediately.
                await r.HaltActorAsync(actorId);
            });
        }

        [Fact(Timeout = 5000)]
        public async Task TestHaltActorAsyncNonExistent()
        {
            await this.RunAsync(async r =>
            {
                var id = r.CreateActorId(typeof(SimpleActor));
                // Actor was never created; should complete immediately.
                await r.HaltActorAsync(id);
            });
        }

        [Fact(Timeout = 5000)]
        public async Task TestHaltActorAsyncNullId()
        {
            await this.RunAsync(async r =>
            {
                await Assert.ThrowsAsync<ArgumentNullException>(() => r.HaltActorAsync(null));
            });
        }

        [Fact(Timeout = 5000)]
        public async Task TestHaltActorAsyncStateMachine()
        {
            await this.RunAsync(async r =>
            {
                var actorId = r.CreateActor(typeof(SimpleStateMachine));
                await r.HaltActorAsync(actorId);
                Assert.Equal(ActorExecutionStatus.None, r.GetActorExecutionStatus(actorId));
            });
        }

        [Fact(Timeout = 5000)]
        public async Task TestHaltAllActorsAsync()
        {
            await this.RunAsync(async r =>
            {
                MultiActorHaltTracker.HaltCount = 0;
                int numActors = 5;
                for (int i = 0; i < numActors; i++)
                {
                    r.CreateActor(typeof(MultiActorHaltTracker));
                }

                await r.HaltAllActorsAsync();
                Assert.Equal(numActors, MultiActorHaltTracker.HaltCount);
                Assert.Equal(0, r.GetCurrentActorCount());
            });
        }

        [Fact(Timeout = 5000)]
        public async Task TestHaltAllActorsAsyncEmpty()
        {
            await this.RunAsync(async r =>
            {
                await r.HaltAllActorsAsync();
            });
        }

        [Fact(Timeout = 5000)]
        public async Task TestMultipleHaltActorAsyncSameActor()
        {
            await this.RunAsync(async r =>
            {
                var actorId = r.CreateActor(typeof(SlowHaltActor));
                SlowHaltActor.HaltCompleted = false;

                var task1 = r.HaltActorAsync(actorId);
                var task2 = r.HaltActorAsync(actorId);

                await Task.WhenAll(task1, task2);
                Assert.True(SlowHaltActor.HaltCompleted);
            });
        }
    }
}

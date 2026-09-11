// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// Expected-red public API contract tests for the current-use Mutex model.
    /// These tests deliberately use only Mutex operations for synchronization so
    /// they cannot pass through an uncontrolled fallback implementation.
    /// </summary>
    public class CurrentUseMutexTests : BaseBugFindingTest
    {
        public CurrentUseMutexTests(ITestOutputHelper output)
            : base(output)
        {
        }

        private Configuration StrictConfiguration() => this.GetConfiguration()
            .WithPartiallyControlledConcurrencyAllowed(false)
            .WithPartiallyControlledDataNondeterminismAllowed(false)
            .WithSystematicFuzzingFallbackEnabled(false)
            .WithTestingIterations(100);

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseMutex")]
        public void TestNamedHandlesShareOneMutexIdentityUnderContention()
        {
            this.Test(() =>
            {
                using var first = new Mutex(false, "Coyote.CurrentUseMutex.SharedIdentity");
                using var second = new Mutex(false, "Coyote.CurrentUseMutex.SharedIdentity");
                int inside = 0;
                int overlap = 0;

                Task firstUser = Task.Run(() => EnterAndLeave(first, ref inside, ref overlap));
                Task secondUser = Task.Run(() => EnterAndLeave(second, ref inside, ref overlap));

                Task.WaitAll(firstUser, secondUser);
                Specification.Assert(Volatile.Read(ref overlap) is 0,
                    "Named Mutex handles did not serialize access to their shared identity.");
                Specification.Assert(Volatile.Read(ref inside) is 0,
                    "A Mutex user did not leave the shared critical section.");
            }, this.StrictConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseMutex")]
        public void TestMutexSupportsRecursiveWaitAndRequiresMatchingReleases()
        {
            this.Test(() =>
            {
                using var owner = new Mutex(false, "Coyote.CurrentUseMutex.Recursion");
                using var contender = new Mutex(false, "Coyote.CurrentUseMutex.Recursion");
                int contenderEntered = 0;

                owner.WaitOne();
                owner.WaitOne();
                Task waitingContender = Task.Run(() =>
                {
                    contender.WaitOne();
                    try
                    {
                        Interlocked.Exchange(ref contenderEntered, 1);
                    }
                    finally
                    {
                        contender.ReleaseMutex();
                    }
                });

                SchedulingPoint.Interleave();
                Specification.Assert(Volatile.Read(ref contenderEntered) is 0,
                    "A contender entered before both recursive acquisitions were released.");
                owner.ReleaseMutex();
                SchedulingPoint.Interleave();
                Specification.Assert(Volatile.Read(ref contenderEntered) is 0,
                    "One ReleaseMutex released a recursively acquired Mutex.");
                owner.ReleaseMutex();

                waitingContender.Wait();
                Specification.Assert(Volatile.Read(ref contenderEntered) is 1,
                    "The contender did not acquire after the matching recursive releases.");
            }, this.StrictConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseMutex")]
        public void TestReleaseMutexRejectsANonOwner()
        {
            this.Test(() =>
            {
                using var owner = new Mutex(false, "Coyote.CurrentUseMutex.WrongOwner");
                using var nonOwner = new Mutex(false, "Coyote.CurrentUseMutex.WrongOwner");
                int rejected = 0;

                owner.WaitOne();
                try
                {
                    Task attempt = Task.Run(() =>
                    {
                        try
                        {
                            nonOwner.ReleaseMutex();
                        }
                        catch (ApplicationException)
                        {
                            Interlocked.Exchange(ref rejected, 1);
                        }
                    });

                    attempt.Wait();
                    Specification.Assert(Volatile.Read(ref rejected) is 1,
                        "ReleaseMutex did not reject an actor that does not own the Mutex.");
                }
                finally
                {
                    owner.ReleaseMutex();
                }
            }, this.StrictConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseMutex")]
        public void TestWaitOneReportsAbandonmentAndTransfersOwnership()
        {
            this.Test(() =>
            {
                using var abandoned = new Mutex(false, "Coyote.CurrentUseMutex.Abandonment");
                using var acquirer = new Mutex(false, "Coyote.CurrentUseMutex.Abandonment");
                int observedAbandonment = 0;

                Task owner = Task.Run(() => abandoned.WaitOne());
                owner.Wait();

                Task nextOwner = Task.Run(() =>
                {
                    bool acquired = false;
                    try
                    {
                        acquirer.WaitOne();
                        acquired = true;
                    }
                    catch (AbandonedMutexException)
                    {
                        acquired = true;
                        Interlocked.Exchange(ref observedAbandonment, 1);
                    }
                    finally
                    {
                        if (acquired)
                        {
                            acquirer.ReleaseMutex();
                        }
                    }
                });

                nextOwner.Wait();
                Specification.Assert(Volatile.Read(ref observedAbandonment) is 1,
                    "WaitOne did not report abandonment while granting the next owner the Mutex.");
            }, this.StrictConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseMutex")]
        public void TestDisposingOneNamedHandleDoesNotDisposeItsPeer()
        {
            this.Test(() =>
            {
                var disposed = new Mutex(false, "Coyote.CurrentUseMutex.Dispose");
                using var survivor = new Mutex(false, "Coyote.CurrentUseMutex.Dispose");
                disposed.Dispose();

                Assert.Throws<ObjectDisposedException>(() => disposed.WaitOne());
                bool acquired = survivor.WaitOne();
                try
                {
                    Specification.Assert(acquired,
                        "The surviving named Mutex handle could not acquire its resource.");
                }
                finally
                {
                    survivor.ReleaseMutex();
                }
            }, this.StrictConfiguration());
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "CurrentUseMutex")]
        public void TestDisposingAnAliasIsIdempotentAndLastAliasAllowsRecreation()
        {
            this.Test(() =>
            {
                var disposed = new Mutex(false, "Coyote.CurrentUseMutex.Recreate");
                disposed.Dispose();
                disposed.Dispose();

                using var recreated = new Mutex(false, "Coyote.CurrentUseMutex.Recreate");
                bool acquired = recreated.WaitOne();
                try
                {
                    Specification.Assert(acquired,
                        "A named Mutex could not be recreated after its final alias was disposed.");
                }
                finally
                {
                    recreated.ReleaseMutex();
                }
            }, this.StrictConfiguration());
        }

        private static void EnterAndLeave(Mutex mutex, ref int inside, ref int overlap)
        {
            mutex.WaitOne();
            try
            {
                if (Interlocked.Increment(ref inside) is not 1)
                {
                    Interlocked.Exchange(ref overlap, 1);
                }

                SchedulingPoint.Interleave();
                Interlocked.Decrement(ref inside);
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
    }
}

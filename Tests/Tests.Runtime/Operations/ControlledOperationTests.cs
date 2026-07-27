// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

using System;
using System.Threading;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Runtime.Tests
{
    public class ControlledOperationTests : BaseRuntimeTest
    {
        public ControlledOperationTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestThreadOperationInstrumentation()
        {
            this.RunSystematicTest(() =>
            {
                var operationId = Operation.CreateNext();
                Specification.Assert(operationId.HasValue, $"Unable to create next operation.");

                int value = 0;
                Thread thread = new Thread(state =>
                {
                    Operation.OnStarted((ulong)state);
                    value = 1;
                    Operation.OnCompleted();
                    Operation.ScheduleNext();
                });

                Operation.Start(operationId.Value);
                thread.Start(operationId.Value);
                Operation.ScheduleNext();

                Operation.PauseUntilCompleted(operationId.Value);
                thread.Join();

                int expected = 1;
                Specification.Assert(value == expected, "Value is {0} instead of {1}.", value, expected);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }

        /// <summary>
        /// Verifies that a completed operation can be reset and reused.
        /// </summary>
        /// <remarks>
        /// The runtime relies on this to reuse an operation across successive activations of the
        /// same logical entity; every actor event-handler dispatch resets the actor's completed
        /// operation. In particular, a reset operation must remain resolvable by its id, which is
        /// why completed operations cannot simply be dropped from the runtime's operation map.
        /// </remarks>
        [Fact(Timeout = 10000)]
        public void TestOperationReuseAfterReset()
        {
            this.RunSystematicTest(() =>
            {
                var operationId = Operation.CreateNext();
                Specification.Assert(operationId.HasValue, "Unable to create next operation.");
                ulong id = operationId.Value;

                int value = 0;
                RunOperationToCompletion(id, () => value++);

                // The operation has completed, so it can be reset and run a second time.
                bool didReset = Operation.TryReset(id);
                Specification.Assert(didReset, "Expected the completed operation to be reset.");

                RunOperationToCompletion(id, () => value++);

                int expected = 2;
                Specification.Assert(value == expected, "Value is {0} instead of {1}.", value, expected);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }

        /// <summary>
        /// Verifies that resetting an operation that has not completed is refused.
        /// </summary>
        [Fact(Timeout = 10000)]
        public void TestOperationResetIsRefusedWhileRunning()
        {
            this.RunSystematicTest(() =>
            {
                var operationId = Operation.CreateNext();
                Specification.Assert(operationId.HasValue, "Unable to create next operation.");
                ulong id = operationId.Value;

                // The operation has been created but never started, so it is not completed.
                bool didReset = Operation.TryReset(id);
                Specification.Assert(!didReset, "Expected the reset of a non-completed operation to be refused.");

                RunOperationToCompletion(id, () => { });

                // Now that it has completed the reset must be accepted, and a second reset of the
                // freshly reset (and therefore no longer completed) operation must be refused.
                Specification.Assert(Operation.TryReset(id), "Expected the completed operation to be reset.");
                Specification.Assert(!Operation.TryReset(id), "Expected the second reset to be refused.");
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }

        /// <summary>
        /// Verifies that resetting an operation that was never created is an error.
        /// </summary>
        [Fact(Timeout = 10000)]
        public void TestOperationResetWithUnknownIdFails()
        {
            this.RunSystematicTest(() =>
            {
                // Reserve an id without ever creating the corresponding operation.
                var operationId = Operation.GetNextId();
                Specification.Assert(operationId.HasValue, "Unable to reserve the next operation id.");

                Exception exception = null;
                try
                {
                    Operation.TryReset(operationId.Value);
                }
                catch (InvalidOperationException ex)
                {
                    exception = ex;
                }

                Specification.Assert(exception != null,
                    "Expected resetting an unknown operation id to throw an invalid operation exception.");
            },
            configuration: this.GetConfiguration().WithTestingIterations(10));
        }

        /// <summary>
        /// Verifies that a pause condition may itself register or reset an operation.
        /// </summary>
        /// <remarks>
        /// The runtime invokes the pause condition of every paused operation while it walks its
        /// collection of schedulable operations, and the condition is arbitrary user code that
        /// <see cref="Operation.PauseUntil"/> accepts from the program under test. Registering or
        /// resetting an operation from inside it inserts into the very collection being walked, so
        /// the walk must tolerate that rather than assume the collection is stable.
        /// <para>
        /// The pause condition deliberately runs on an operation registered <em>after</em> the one it
        /// resets. The collection is kept sorted by registration order, so resetting an older
        /// operation inserts <em>behind</em> the position the walk has already passed, which is the
        /// case a compaction performed during the walk gets wrong. Pausing on the root operation
        /// instead only ever inserts ahead of the walk, and exercises nothing.
        /// </para>
        /// </remarks>
        [Fact(Timeout = 10000)]
        public void TestPauseConditionThatMutatesSchedulableOperations()
        {
            this.RunSystematicTest(() =>
            {
                // An operation that has run to completion, so that it can be reset later. Being
                // completed, it is also dropped from the schedulable operations by a later step,
                // which is what makes resetting it insert rather than no-op.
                var resettableId = Operation.CreateNext();
                Specification.Assert(resettableId.HasValue, "Unable to create next operation.");
                RunOperationToCompletion(resettableId.Value, () => { });

                // Registered after the resettable one, so its pause condition is walked from a
                // later position than where the reset inserts.
                var pausingId = Operation.CreateNext();
                Specification.Assert(pausingId.HasValue, "Unable to create next operation.");

                int evaluations = 0;
                bool didReset = false;
                bool didRegister = false;
                RunOperationToCompletion(pausingId.Value, () =>
                {
                    Operation.PauseUntil(() =>
                    {
                        // The first evaluation is made inline by the caller, before the operation is
                        // paused. Returning false forces the pause, so every later evaluation is made
                        // by the runtime from its walk of the schedulable operations.
                        if (++evaluations is 1)
                        {
                            return false;
                        }

                        // Accumulated rather than assigned: the condition is evaluated more than once
                        // more, and only the first of those resets can be accepted.
                        didReset |= Operation.TryReset(resettableId.Value);
                        didRegister |= Operation.CreateNext().HasValue;
                        return true;
                    });
                });

                Specification.Assert(evaluations > 1,
                    "The pause condition was never evaluated by the runtime, so nothing was exercised.");
                Specification.Assert(didReset, "The completed operation was not reset from the pause condition.");
                Specification.Assert(didRegister, "No operation was registered from the pause condition.");
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }

        /// <summary>
        /// Verifies that an operation reset from inside a pause condition can run again afterwards.
        /// </summary>
        /// <remarks>
        /// The reset inserts the operation at its original position in the runtime's collection of
        /// schedulable operations, which can be behind the position the walk invoking the condition has
        /// already passed. Being missed by that walk is harmless — a reset operation has no status for
        /// it to act on yet — but only if the scheduler still picks the operation up afterwards, which
        /// is the whole point of resetting it. That is what this checks, rather than merely that the
        /// reset was accepted.
        /// </remarks>
        [Fact(Timeout = 10000)]
        public void TestResetOperationIsScheduledAfterwards()
        {
            this.RunSystematicTest(() =>
            {
                var resettableId = Operation.CreateNext();
                Specification.Assert(resettableId.HasValue, "Unable to create next operation.");
                RunOperationToCompletion(resettableId.Value, () => { });

                // Registered after the operation it resets, so the reset inserts behind the walk.
                var pausingId = Operation.CreateNext();
                Specification.Assert(pausingId.HasValue, "Unable to create next operation.");

                int evaluations = 0;
                bool didReset = false;
                RunOperationToCompletion(pausingId.Value, () =>
                {
                    Operation.PauseUntil(() =>
                    {
                        if (++evaluations is 1)
                        {
                            return false;
                        }

                        didReset |= Operation.TryReset(resettableId.Value);
                        return true;
                    });
                });

                Specification.Assert(evaluations > 1,
                    "The pause condition was never evaluated by the runtime, so nothing was exercised.");
                Specification.Assert(didReset, "The completed operation was not reset from the pause condition.");

                // The reset made it schedulable again, so it must actually run.
                int ran = 0;
                RunOperationToCompletion(resettableId.Value, () => ran++);
                Specification.Assert(ran is 1, "The reset operation did not run again.");
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }

        /// <summary>
        /// Verifies that a pause condition which mutates the schedulable operations is evaluated at
        /// most once per walk of them.
        /// </summary>
        /// <remarks>
        /// A deadlock is the expected outcome, and is what makes this an oracle. The condition below
        /// resets an older operation and then keeps the operation paused; nothing else is left to run,
        /// so the program really is deadlocked and the runtime must say so. The reset inserts the older
        /// operation behind the position the walk has already passed, and every later entry shifts
        /// right — so a walk that does not track which operations it has already visited lands back on
        /// this one and evaluates its condition a second time in the same pass. That extra evaluation
        /// returns true, enables the operation, and hides the deadlock. Reporting it is therefore the
        /// evidence that each operation is visited exactly once.
        /// </remarks>
        [Fact(Timeout = 10000)]
        public void TestPauseConditionIsNotReevaluatedWithinAWalk()
        {
            this.TestWithError(() =>
            {
                var resettableId = Operation.CreateNext();
                Specification.Assert(resettableId.HasValue, "Unable to create next operation.");
                RunOperationToCompletion(resettableId.Value, () => { });

                // Registered after the operation it resets, so the reset inserts behind the walk.
                var pausingId = Operation.CreateNext();
                Specification.Assert(pausingId.HasValue, "Unable to create next operation.");

                int evaluations = 0;
                RunOperationToCompletion(pausingId.Value, () =>
                {
                    Operation.PauseUntil(() =>
                    {
                        // The first evaluation is made inline by the caller and pauses the operation.
                        // The second is the runtime's, and mutates the collection it is walking. A
                        // third can only come from being visited twice in that same walk.
                        if (++evaluations is 2)
                        {
                            Operation.TryReset(resettableId.Value);
                        }

                        return evaluations > 2;
                    });
                });
            },
            configuration: this.GetConfiguration().WithTestingIterations(100),
            errorChecker: (e) => Assert.Contains("Deadlock detected", e),
            replay: false);
        }

        /// <summary>
        /// Starts the operation with the specified id on a dedicated thread, runs the specified
        /// logic on it, and waits until it completes.
        /// </summary>
        private static void RunOperationToCompletion(ulong operationId, Action logic)
        {
            Thread thread = new Thread(state =>
            {
                try
                {
                    Operation.OnStarted((ulong)state);
                    logic();
                    Operation.OnCompleted();
                    Operation.ScheduleNext();
                }
                catch (ThreadInterruptedException)
                {
                    // The runtime interrupts the threads it controls as soon as it detaches, which is
                    // what an iteration that reports an error does. This is a raw thread rather than
                    // one the runtime created, so letting the interrupt escape would surface as an
                    // unhandled exception and take the test host down instead of failing the test.
                }
            });

            Operation.Start(operationId);
            thread.Start(operationId);
            Operation.ScheduleNext();

            Operation.PauseUntilCompleted(operationId);
            thread.Join();
        }
    }
}

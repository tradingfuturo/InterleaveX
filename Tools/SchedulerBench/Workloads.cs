// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System.Threading.Tasks;

namespace Microsoft.Coyote.Benchmarking.Scheduler
{
    /// <summary>
    /// Workloads exercised by the systematic testing runtime during benchmarking.
    /// </summary>
    /// <remarks>
    /// These are deliberately written as ordinary static methods rather than lambdas. The
    /// call-site extraction rewriting pass skips compiler-generated types, so logic placed
    /// directly in a lambda would not receive the injected prologue that some of the
    /// measured optimizations target.
    /// </remarks>
    internal static class Workloads
    {
        /// <summary>
        /// Number of long-lived operations in the 'deep' workload.
        /// </summary>
        private const int DeepOperationCount = 2;

        /// <summary>
        /// Number of scheduling points performed by each 'deep' operation.
        /// </summary>
        private const int DeepStepsPerOperation = 250;

        /// <summary>
        /// Number of short-lived operations in the 'wide' workload.
        /// </summary>
        private const int WideOperationCount = 200;

        /// <summary>
        /// Number of scheduling points performed by each 'wide' operation.
        /// </summary>
        private const int WideStepsPerOperation = 3;

        /// <summary>
        /// Shared state accessed by the workloads.
        /// </summary>
        private static int SharedCounter;

        /// <summary>
        /// Synchronizes access to <see cref="SharedCounter"/>.
        /// </summary>
        private static readonly object SyncObject = new object();

        /// <summary>
        /// Runs the 'deep' workload: few long-lived operations performing many scheduling
        /// points each. Isolates per-scheduling-step cost.
        /// </summary>
        internal static async Task RunDeepAsync()
        {
            SharedCounter = 0;
            var tasks = new Task[DeepOperationCount];
            for (int idx = 0; idx < DeepOperationCount; idx++)
            {
                tasks[idx] = Task.Run(() => ExecuteSteps(DeepStepsPerOperation));
            }

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Runs the 'wide' workload: many short-lived operations. Isolates cost that scales
        /// with the number of operations ever created, as the operation map is not pruned
        /// during an iteration.
        /// </summary>
        internal static async Task RunWideAsync()
        {
            SharedCounter = 0;
            var tasks = new Task[WideOperationCount];
            for (int idx = 0; idx < WideOperationCount; idx++)
            {
                tasks[idx] = Task.Run(() => ExecuteSteps(WideStepsPerOperation));
            }

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Performs the specified number of synchronized increments.
        /// </summary>
        private static void ExecuteSteps(int steps)
        {
            for (int idx = 0; idx < steps; idx++)
            {
                Increment();
            }
        }

        /// <summary>
        /// Increments the shared counter under a lock. The nested calls give a method-call
        /// to scheduling-point ratio closer to that of a real program under test.
        /// </summary>
        private static void Increment()
        {
            lock (SyncObject)
            {
                SharedCounter = Advance(SharedCounter);
            }
        }

        /// <summary>
        /// Returns the next value of the counter.
        /// </summary>
        private static int Advance(int value) => Normalize(value) + 1;

        /// <summary>
        /// Returns the specified value.
        /// </summary>
        private static int Normalize(int value) => value;
    }
}

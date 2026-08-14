// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Runtime;

namespace Microsoft.Coyote.Testing.Fuzzing
{
    /// <summary>
    /// Abstract exploration strategy used during systematic fuzzing.
    /// </summary>
    internal abstract class FuzzingStrategy : Strategy
    {
        /// <summary>
        /// Provides access to the operation id associated with each asynchronous control flow.
        /// </summary>
        private static readonly AsyncLocal<Guid> OperationId = new AsyncLocal<Guid>();

        /// <summary>
        /// Map from task ids to operation ids.
        /// </summary>
        private readonly ConcurrentDictionary<int, Guid> OperationIdMap;

        /// <summary>
        /// Initializes a new instance of the <see cref="FuzzingStrategy"/> class.
        /// </summary>
        internal FuzzingStrategy(Configuration configuration, bool isFair)
            : base(configuration, isFair)
        {
            this.OperationIdMap = new ConcurrentDictionary<int, Guid>();
        }

        /// <summary>
        /// Returns the next delay.
        /// </summary>
        /// <param name="current">The operation requesting the delay.</param>
        /// <param name="maxValue">The max value.</param>
        /// <param name="next">The next delay.</param>
        /// <returns>True if there is a next delay, else false.</returns>
        internal bool GetNextDelay(ControlledOperation current, int maxValue, out int next) =>
            this.NextDelay(current, maxValue, out next);

        /// <summary>
        /// Returns the next delay.
        /// </summary>
        /// <param name="current">The operation requesting the delay.</param>
        /// <param name="maxValue">The max value.</param>
        /// <param name="next">The next delay.</param>
        /// <returns>True if there is a next delay, else false.</returns>
        internal abstract bool NextDelay(ControlledOperation current, int maxValue, out int next);

        /// <summary>
        /// Returns a positive quantized delay that never exceeds the inclusive maximum.
        /// </summary>
        protected int GetNextQuantizedDelay(int maxValue, int quantum)
        {
            if (maxValue <= 0)
            {
                return 0;
            }

            int bucketCount = maxValue / quantum;
            return bucketCount is 0 ? maxValue :
                (this.RandomValueGenerator.Next(bucketCount) + 1) * quantum;
        }

        /// <summary>
        /// Returns the current operation id.
        /// </summary>
        protected Guid GetOperationId()
        {
            Guid id;
            if (Task.CurrentId is null)
            {
                id = OperationId.Value;
                if (id == Guid.Empty)
                {
                    id = Guid.NewGuid();
                    OperationId.Value = id;
                }
            }
            else
            {
                id = this.OperationIdMap.GetOrAdd(Task.CurrentId.Value, Guid.NewGuid());
                OperationId.Value = id;
            }

            return id;
        }

        /// <summary>
        /// Clears the operation id associated with the current asynchronous control flow.
        /// </summary>
        /// <remarks>
        /// This is invoked when a controlled thread finishes executing an operation. The id is stored in
        /// an <see cref="AsyncLocal{T}"/> that is never otherwise reset, so leaving it set would allow a
        /// thread that goes on to execute another operation to be treated as the same logical operation,
        /// which would conflate their delay distributions.
        ///
        /// Guarded because writing an <see cref="AsyncLocal{T}"/> of a value type always boxes, and so
        /// never matches the value already stored: the write would allocate a new value map and a new
        /// execution context on every completed operation, including under
        /// <see cref="SchedulingPolicy.Interleaving"/>, which never assigns an id in the first place.
        /// Reading one is a lookup that allocates nothing.
        /// </remarks>
        internal static void ClearOperationId()
        {
            if (OperationId.Value != Guid.Empty)
            {
                OperationId.Value = Guid.Empty;
            }
        }
    }
}

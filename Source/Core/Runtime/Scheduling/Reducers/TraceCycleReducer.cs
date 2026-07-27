// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

using System.Collections.Generic;

namespace Microsoft.Coyote.Runtime
{
    /// <summary>
    /// A reducer that analyzes cycles in the execution trace to reduce the set of operations
    /// to be scheduled at each scheduling step.
    /// </summary>
    internal sealed class TraceCycleReducer : IScheduleReducer
    {
        /// <summary>
        /// Set of states that have been 'READ' accessed. States are removed from the set
        /// when they are 'WRITE' accessed.
        /// </summary>
        private readonly HashSet<string> RepeatedReadAccesses;

        /// <summary>
        /// Reusable buffer holding the operations that perform 'WRITE' accesses in the current
        /// scheduling step.
        /// </summary>
        private readonly List<ControlledOperation> WriteAccessOps;

        /// <summary>
        /// Initializes a new instance of the <see cref="TraceCycleReducer"/> class.
        /// </summary>
        internal TraceCycleReducer()
        {
            this.RepeatedReadAccesses = new HashSet<string>();
            this.WriteAccessOps = new List<ControlledOperation>();
        }

        /// <inheritdoc/>
        public void InitializeNextIteration(uint iteration)
        {
            // Release the operations of the previous iteration, which this buffer would otherwise
            // keep alive until the next scheduling step that reduces.
            this.WriteAccessOps.Clear();
        }

        /// <inheritdoc/>
        public void ReduceOperations(IReadOnlyList<ControlledOperation> ops, ControlledOperation current,
            List<ControlledOperation> result)
        {
            // Find all operations that perform 'WRITE' accesses.
            var writeAccessOps = this.WriteAccessOps;
            writeAccessOps.Clear();
            for (int idx = 0; idx < ops.Count; ++idx)
            {
                if (ops[idx].LastSchedulingPoint is SchedulingPointType.Write)
                {
                    writeAccessOps.Add(ops[idx]);
                }
            }

            // Filter out all 'READ' operations that are repeatedly 'READ' accessing shared state when there is a 'WRITE' access.
            // This must be evaluated before the set of repeated read accesses is updated below, so that the decision is based
            // on the accesses known at the start of this scheduling step.
            for (int idx = 0; idx < ops.Count; ++idx)
            {
                var op = ops[idx];
                if (op.LastSchedulingPoint is SchedulingPointType.Read &&
                    this.RepeatedReadAccesses.Contains(op.LastAccessedSharedState) &&
                    IsSharedStateWriteAccessed(writeAccessOps, op.LastAccessedSharedState))
                {
                    continue;
                }

                result.Add(op);
            }

            if (current.LastSchedulingPoint is SchedulingPointType.Read)
            {
                // The current operation is a 'READ' access, so add it to the set of repeated read accesses.
                this.RepeatedReadAccesses.Add(current.LastAccessedSharedState);
            }
            else if (current.LastSchedulingPoint is SchedulingPointType.Write)
            {
                // The current operation is a 'WRITE' access, so remove it from the set of repeated read accesses.
                this.RepeatedReadAccesses.RemoveWhere(state =>
                    current.LastAccessedSharedStateComparer?.Equals(current.LastAccessedSharedState, state) ??
                    current.LastAccessedSharedState == state);
            }

            if (result.Count == ops.Count)
            {
                // Nothing was reduced, so report that no reduction applies rather than handing back
                // a copy of the input, which lets the caller keep using its current buffer.
                result.Clear();
            }
        }

        /// <summary>
        /// Returns true if any of the specified 'WRITE' accessing operations accessed the specified shared state.
        /// </summary>
        private static bool IsSharedStateWriteAccessed(List<ControlledOperation> writeAccessOps, string state)
        {
            for (int idx = 0; idx < writeAccessOps.Count; ++idx)
            {
                var wop = writeAccessOps[idx];
                if (wop.LastAccessedSharedStateComparer?.Equals(wop.LastAccessedSharedState, state) ??
                    wop.LastAccessedSharedState == state)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

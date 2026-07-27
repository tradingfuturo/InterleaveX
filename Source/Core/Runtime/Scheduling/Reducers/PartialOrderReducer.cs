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
    /// A reducer that prioritizes non-racy access scheduling decisions to try force
    /// racy operations interleave.
    /// </summary>
    internal sealed class PartialOrderReducer : IScheduleReducer
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PartialOrderReducer"/> class.
        /// </summary>
        internal PartialOrderReducer()
        {
        }

        /// <inheritdoc/>
        public void InitializeNextIteration(uint iteration)
        {
        }

        /// <inheritdoc/>
        public void ReduceOperations(IReadOnlyList<ControlledOperation> ops, ControlledOperation current,
            List<ControlledOperation> result)
        {
            // Find all operations that are not invoking a 'READ' or 'WRITE' scheduling decision,
            // and if there are any, then return them. This effectively helps racy scheduling
            // decisions to happen as close to each other as possible, which helps to find bugs
            // that are caused by the interleaving of these operations. If there are none, then
            // the result is left empty, which the caller treats as "no reduction applies".
            for (int idx = 0; idx < ops.Count; ++idx)
            {
                var op = ops[idx];
                if (!SchedulingPoint.IsReadOrWrite(op.LastSchedulingPoint))
                {
                    result.Add(op);
                }
            }
        }
    }
}

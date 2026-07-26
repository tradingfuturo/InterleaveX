// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;

namespace Microsoft.Coyote.Runtime
{
    /// <summary>
    /// Interface of a reducer that can choose a subset of all available operations
    /// to be scheduled at each scheduling step.
    /// </summary>
    internal interface IScheduleReducer
    {
        /// <summary>
        /// Initializes the next iteration.
        /// </summary>
        /// <param name="iteration">The id of the next iteration.</param>
        void InitializeNextIteration(uint iteration);

        /// <summary>
        /// Appends to <paramref name="result"/> the subset of <paramref name="ops"/> that should be
        /// scheduled at the next scheduling step, preserving the relative order of <paramref name="ops"/>.
        /// </summary>
        /// <param name="ops">All available operations to schedule.</param>
        /// <param name="current">The currently scheduled operation.</param>
        /// <param name="result">The buffer to populate with the subset of operations to schedule.</param>
        /// <remarks>
        /// The <paramref name="result"/> buffer is empty on entry and is never the same instance as
        /// <paramref name="ops"/>. Leaving it empty means that no reduction applies, in which case
        /// the caller keeps <paramref name="ops"/> unchanged.
        /// </remarks>
        void ReduceOperations(IReadOnlyList<ControlledOperation> ops, ControlledOperation current,
            List<ControlledOperation> result);
    }
}

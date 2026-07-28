// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System.Globalization;

namespace Microsoft.Coyote.SystematicTesting
{
    /// <summary>
    /// Decides whether a finished worker process of a parallel run failed in a way that its report
    /// cannot express, and so has to be reported as an internal error of the merged run.
    /// </summary>
    internal static class ParallelWorkerOutcome
    {
        /// <summary>
        /// Returns the internal error to record for a worker with the specified outcome, or null if
        /// it completed acceptably.
        /// </summary>
        /// <remarks>
        /// Only workers that exited on their own reach this. The coordinator waits for every worker
        /// before merging, and kills whatever is left over in the cleanup that follows the merge, so
        /// a worker it had to kill is never evaluated here. A worker asked to stop early, because a
        /// bug was found elsewhere or because its parent went away, stops at its next iteration
        /// boundary and exits normally, which is why an unexpected code means an unexpected failure
        /// rather than a run that was cut short.
        /// </remarks>
        internal static string Evaluate(uint shardIndex, int exitCode, bool reportExists)
        {
            if (!reportExists)
            {
                // The more informative of the two, so it is the one reported.
                return string.Format(CultureInfo.InvariantCulture,
                    "Worker {0} produced no test report.", shardIndex);
            }

            if (exitCode != (int)ExitCode.Success && exitCode != (int)ExitCode.BugFound)
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "Worker {0} exited with {1}.", shardIndex, exitCode);
            }

            return null;
        }
    }
}

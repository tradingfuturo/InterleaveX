// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Microsoft.Coyote.SystematicTesting
{
    /// <summary>
    /// Resolves which of the report artifacts in a worker's output directory belong to the run that
    /// just finished.
    /// </summary>
    /// <remarks>
    /// Split out from the orchestration that consumes it, and kept free of any file system access, so
    /// that it can be unit tested directly.
    /// </remarks>
    internal static class ParallelTestArtifacts
    {
        /// <summary>
        /// Returns the highest '&lt;assembly&gt;_&lt;index&gt;' artifact index among the specified file
        /// names, or -1 if none of them is such an artifact.
        /// </summary>
        /// <param name="assemblyName">The assembly name the artifacts are prefixed with.</param>
        /// <param name="fileNames">The file names present, without their directory.</param>
        internal static int GetHighestArtifactIndex(string assemblyName, IEnumerable<string> fileNames)
        {
            var match = new Regex("^" + Regex.Escape(assemblyName) + @"_([0-9]+)");
            int highest = -1;
            foreach (string fileName in fileNames)
            {
                Match result = match.Match(fileName);
                if (result.Success && int.TryParse(result.Groups[1].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int index) && index > highest)
                {
                    highest = index;
                }
            }

            return highest;
        }

        /// <summary>
        /// Returns the '&lt;assembly&gt;_&lt;index&gt;' stem that the artifacts of the most recent run
        /// carry, given the file names present, or null if that run produced none.
        /// </summary>
        /// <remarks>
        /// A worker writes into a directory of its own that the coordinator empties first, so its files
        /// normally carry the '_0' suffix that the output file manager assigns first. The suffix is
        /// resolved from the files actually present rather than assumed: if the directory could not be
        /// emptied, the worker's artifacts are the ones with the highest index, and promoting a stale
        /// '_0' would advertise a previous run's trace as the repro for this run's bug.
        /// <para>
        /// The highest index alone is not enough, because emptying the directory is best effort and a
        /// worker can report a bug without leaving a trace behind. <paramref name="minimumIndex"/> is
        /// the highest index that was already present before the worker started, so anything at or below
        /// it belongs to an earlier run and is not this run's repro. Pass -1 when the directory was known
        /// to be empty.
        /// </para>
        /// </remarks>
        /// <param name="assemblyName">The assembly name the artifacts are prefixed with.</param>
        /// <param name="fileNames">The file names present, without their directory.</param>
        /// <param name="minimumIndex">The highest index present before the run, or -1 for none.</param>
        internal static string GetArtifactStem(string assemblyName, IEnumerable<string> fileNames,
            int minimumIndex = -1)
        {
            int highest = GetHighestArtifactIndex(assemblyName, fileNames);
            return highest <= minimumIndex ? null :
                assemblyName + "_" + highest.ToString(CultureInfo.InvariantCulture);
        }
    }
}

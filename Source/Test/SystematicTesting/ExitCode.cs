// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

namespace Microsoft.Coyote.SystematicTesting
{
    /// <summary>
    /// The exit code returned by the tool.
    /// </summary>
    /// <remarks>
    /// Also the contract between the coordinator of a parallel run and its worker processes, which is
    /// why this lives beside the rest of that machinery rather than with the command line: the
    /// coordinator reads these values back off a process it started. The numeric order is load
    /// bearing, because a run over several test methods reports the worst code it saw.
    /// </remarks>
    internal enum ExitCode
    {
        /// <summary>
        /// Indicates that the tool terminated successfully.
        /// </summary>
        /// <remarks>
        /// If the tool run in test mode, it also indicates that no bugs were found.
        /// </remarks>
        Success = 0,

        /// <summary>
        /// Indicates that the tool terminated with an error.
        /// </summary>
        Error = 1,

        /// <summary>
        /// Indicates that a bug was found during testing.
        /// </summary>
        BugFound = 2,

        /// <summary>
        /// Indicates that the tool terminated with an internal error.
        /// </summary>
        InternalError = 3
    }
}

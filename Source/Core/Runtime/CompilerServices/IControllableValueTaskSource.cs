// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

using System.Threading.Tasks;

namespace Microsoft.Coyote.Runtime.CompilerServices
{
    /// <summary>
    /// Exposes the scheduler-visible task that backs a modeled <see cref="ValueTask"/> source.
    /// </summary>
    /// <remarks>This interface is intended for compiler use rather than use directly in code.</remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public interface IControllableValueTaskSource
    {
        /// <summary>Returns the controlled task associated with the specified source token.</summary>
        Task GetTask(short token);
    }
}

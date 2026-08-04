// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

namespace Microsoft.Coyote.Rewriting.Types.Collections
{
    /// <summary>
    /// Implemented by every modelled collection instance, so that a collection can be guarded by code
    /// that does not know what kind of collection it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Needed because an operation reads collections other than the one it was called on: a list is
    /// constructed from any enumerable, a set is intersected with any enumerable, and the shim serving
    /// those calls cannot name the wrapper type of whatever it was handed. Without this, only the
    /// receiver was ever guarded, and the collection being read from was free to be written at the same
    /// time by somebody else with nothing reported.
    /// </para>
    /// <para>
    /// A property rather than the field each wrapper already has, because an interface cannot declare a
    /// field. The wrappers keep the field and implement this explicitly, so guarding the receiver stays
    /// a direct load and only the cross-type lookup pays for the indirection.
    /// </para>
    /// </remarks>
    internal interface IModelledCollection
    {
        /// <summary>
        /// Detects unsynchronized concurrent access to this collection.
        /// </summary>
        DataRaceChecker Checker { get; }
    }
}

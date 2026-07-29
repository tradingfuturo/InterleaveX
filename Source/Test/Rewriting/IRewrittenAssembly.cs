// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System.Collections.Generic;
using Microsoft.Coyote.IO;

namespace Microsoft.Coyote.Rewriting
{
    /// <summary>
    /// What the rewriting cache needs to know about an assembly it is recording.
    /// </summary>
    /// <remarks>
    /// Everything here is plain data that <see cref="AssemblyInfo"/> already holds. It is named as an
    /// interface so that <see cref="RewritingCache"/> does not depend on the Mono.Cecil-backed type:
    /// recording is decided entirely by paths and stamps, but taking an <see cref="AssemblyInfo"/>
    /// meant a test had to stage a real assembly and read it through Cecil before it could ask what
    /// the cache would record, which is why the cache had no test of its own.
    /// </remarks>
    internal interface IRewrittenAssembly
    {
        /// <summary>
        /// The name of the assembly.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// The path the assembly was read from.
        /// </summary>
        string FilePath { get; }

        /// <summary>
        /// The names of the assemblies this one directly references.
        /// </summary>
        IReadOnlyList<string> ReferenceNames { get; }

        /// <summary>
        /// The directories that were searched while resolving the modules of this assembly.
        /// </summary>
        IReadOnlyList<string> SearchDirectories { get; }

        /// <summary>
        /// The paths of every module that was resolved while visiting this assembly.
        /// </summary>
        IEnumerable<string> ResolvedModulePaths { get; }

        /// <summary>
        /// The directories holding every installed version of each shared framework that was asked
        /// for, which describe the candidates resolution chose between.
        /// </summary>
        IReadOnlyList<string> FrameworkInventoryRoots { get; }

        /// <summary>
        /// Returns what the specified file looked like when this assembly read it.
        /// </summary>
        /// <remarks>
        /// The cache fingerprints what a run consumed only once that run is over, so between the read
        /// and the fingerprint there is an interval in which the file can be replaced. Recording the
        /// new bytes against output built from the old ones is the one mistake this cache cannot
        /// recover from: the manifest is self-consistent, and every later run skips. So the state at
        /// the moment of the read is kept, and the recording is refused if it no longer matches.
        ///
        /// Metadata rather than content, because this is taken on every resolution -- hashing each
        /// one would read every framework assembly a pass touches, twice.
        /// </remarks>
        /// <returns>True if the file was read by this assembly, else false.</returns>
        bool TryGetResolutionStamp(string path, out IFileEntry stamp);
    }
}

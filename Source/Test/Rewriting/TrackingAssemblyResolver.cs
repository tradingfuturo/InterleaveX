// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using Mono.Cecil;

namespace Microsoft.Coyote.Rewriting
{
    /// <summary>
    /// An assembly resolver that remembers which files it resolved.
    /// </summary>
    /// <remarks>
    /// The rewriting passes decide what to emit by resolving types and methods, so the content of
    /// every assembly they reach is an input to the rewritten output. Recording those files is what
    /// lets <see cref="RewritingCache"/> notice that a reference changed underneath an otherwise
    /// untouched assembly. The set is deliberately gathered from resolution itself rather than from a
    /// list of expected dependencies, because the passes reach assemblies -- framework, test
    /// framework, packages -- that no such list would name.
    /// </remarks>
    internal sealed class TrackingAssemblyResolver : DefaultAssemblyResolver
    {
        /// <summary>
        /// The paths of the modules that were resolved.
        /// </summary>
        private readonly HashSet<string> ResolvedPaths;

        /// <summary>
        /// Initializes a new instance of the <see cref="TrackingAssemblyResolver"/> class.
        /// </summary>
        internal TrackingAssemblyResolver()
        {
            // Ordinal, because this set only keeps one resolver from recording one file twice, and the
            // rewriting cache does the comparison that decides identity, against the file system the
            // files actually sit on. Two spellings surviving to there cost a hash, not an answer.
            this.ResolvedPaths = new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>
        /// The paths of the modules that were resolved.
        /// </summary>
        /// <remarks>
        /// Remains readable after the resolver is disposed, because the rewriting cache records an
        /// assembly only once its output has reached its final location.
        /// </remarks>
        internal IEnumerable<string> ResolvedModulePaths => this.ResolvedPaths;

        /// <inheritdoc/>
        public override AssemblyDefinition Resolve(AssemblyNameReference name) =>
            this.Track(base.Resolve(name));

        /// <inheritdoc/>
        public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters) =>
            this.Track(base.Resolve(name, parameters));

        /// <summary>
        /// Records the file that backs the specified assembly, and returns the assembly.
        /// </summary>
        private AssemblyDefinition Track(AssemblyDefinition assembly)
        {
            foreach (var module in assembly?.Modules ?? (IEnumerable<ModuleDefinition>)Array.Empty<ModuleDefinition>())
            {
                if (!string.IsNullOrEmpty(module.FileName))
                {
                    this.ResolvedPaths.Add(module.FileName);
                }
            }

            return assembly;
        }
    }
}

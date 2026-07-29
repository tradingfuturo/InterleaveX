// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using Microsoft.Coyote.IO;
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
        /// What each file looked like when it was read.
        /// </summary>
        private readonly Dictionary<string, IFileEntry> Stamps;

        /// <summary>
        /// The file system the stamps are taken from.
        /// </summary>
        private readonly IFileSystem FileSystem;

        /// <summary>
        /// Initializes a new instance of the <see cref="TrackingAssemblyResolver"/> class.
        /// </summary>
        internal TrackingAssemblyResolver(IFileSystem fileSystem)
        {
            this.FileSystem = fileSystem;

            // Ordinal, because this set only keeps one resolver from recording one file twice, and the
            // rewriting cache does the comparison that decides identity, against the file system the
            // files actually sit on. Two spellings surviving to there cost a hash, not an answer.
            this.ResolvedPaths = new HashSet<string>(StringComparer.Ordinal);
            this.Stamps = new Dictionary<string, IFileEntry>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Records what the specified file looks like now, if it has not been recorded already.
        /// </summary>
        /// <remarks>
        /// The first stamp wins, because what this answers is "as it was when this run first read it",
        /// and a later read of the same file is a read of whatever the first one already described.
        ///
        /// A file system that refuses to describe the file leaves it unstamped rather than failing the
        /// resolution, which would turn a transient error into a rewriting failure. Nothing is lost by
        /// it: the same file is opened again when the cache fingerprints it, and a refusal there is
        /// already what stops a manifest from being written.
        /// </remarks>
        internal void Stamp(string path)
        {
            if (string.IsNullOrEmpty(path) || this.Stamps.ContainsKey(path))
            {
                return;
            }

            try
            {
                this.Stamps.Add(path, this.FileSystem.GetFile(path));
            }
            catch (Exception)
            {
                // Deliberately unstamped. See the remarks above.
            }
        }

        /// <summary>
        /// Returns what the specified file looked like when it was read.
        /// </summary>
        internal bool TryGetResolutionStamp(string path, out IFileEntry stamp) =>
            this.Stamps.TryGetValue(path, out stamp);

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

                    // Stamped here rather than where the cache records it, because here is where the
                    // file was read. What the cache fingerprints later describes whatever is on disk
                    // by then, which is the same file only if nothing replaced it in between.
                    this.Stamp(module.FileName);
                }
            }

            return assembly;
        }
    }
}

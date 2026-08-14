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

        private readonly HashSet<string> CandidatePaths;

        /// <summary>
        /// Paths whose point-in-time content could not be captured.
        /// </summary>
        private readonly HashSet<string> UnreliableStampPaths;

        /// <summary>
        /// What each file looked like when it was read.
        /// </summary>
        private readonly Dictionary<string, ResolutionStamp> Stamps;

        /// <summary>
        /// The file system the stamps are taken from.
        /// </summary>
        private readonly IFileSystem FileSystem;

        private readonly Func<string, string> ToLogicalPath;

        /// <summary>
        /// Initializes a new instance of the <see cref="TrackingAssemblyResolver"/> class.
        /// </summary>
        internal TrackingAssemblyResolver(IFileSystem fileSystem, Func<string, string> toLogicalPath = null)
        {
            this.FileSystem = fileSystem;
            this.ToLogicalPath = toLogicalPath ?? (path => path);

            // Ordinal, because this set only keeps one resolver from recording one file twice, and the
            // rewriting cache does the comparison that decides identity, against the file system the
            // files actually sit on. Two spellings surviving to there cost a hash, not an answer.
            this.ResolvedPaths = new HashSet<string>(StringComparer.Ordinal);
            this.CandidatePaths = new HashSet<string>(StringComparer.Ordinal);
            this.UnreliableStampPaths = new HashSet<string>(StringComparer.Ordinal);
            this.Stamps = new Dictionary<string, ResolutionStamp>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Records what the specified file looks like now, if it has not been recorded already.
        /// </summary>
        /// <remarks>
        /// The first stamp wins, because what this answers is "as it was when this run first read it",
        /// and a later read of the same file is a read of whatever the first one already described.
        ///
        /// A file system that refuses to describe the file leaves it unstamped rather than failing the
        /// resolution, but the path is exposed as unreliable so the cache cannot publish a manifest
        /// that describes bytes different from the ones the rewrite consumed.
        /// </remarks>
        internal void Stamp(string path, bool replace = false)
        {
            string logicalPath = string.IsNullOrEmpty(path) ? path : this.ToLogicalPath(path);
            if (string.IsNullOrEmpty(path) || (!replace && this.Stamps.ContainsKey(logicalPath)) ||
                this.UnreliableStampPaths.Contains(logicalPath))
            {
                return;
            }

            try
            {
                IFileEntry entry = this.FileSystem.GetFile(path);
                string fingerprint = entry.Exists ?
                    RewritingCacheValidator.ComputeFileFingerprint(this.FileSystem, path) : null;
                this.Stamps[logicalPath] = new ResolutionStamp(entry, fingerprint);
            }
            catch (Exception)
            {
                this.UnreliableStampPaths.Add(logicalPath);
            }
        }

        /// <summary>
        /// Returns what the specified file looked like when it was read.
        /// </summary>
        internal bool TryGetResolutionStamp(string path, out ResolutionStamp stamp) =>
            this.Stamps.TryGetValue(path, out stamp);

        /// <summary>
        /// The paths of the modules that were resolved.
        /// </summary>
        /// <remarks>
        /// Remains readable after the resolver is disposed, because the rewriting cache records an
        /// assembly only once its output has reached its final location.
        /// </remarks>
        internal IEnumerable<string> ResolvedModulePaths => this.ResolvedPaths;

        internal IEnumerable<string> ResolutionCandidatePaths => this.CandidatePaths;

        internal IEnumerable<string> UnreliableResolutionStampPaths => this.UnreliableStampPaths;

        /// <inheritdoc/>
        public override AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            this.StampCandidates(name);
            return this.Track(base.Resolve(name));
        }

        /// <inheritdoc/>
        public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            this.StampCandidates(name);
            return this.Track(base.Resolve(name, parameters));
        }

        private void StampCandidates(AssemblyNameReference name)
        {
            string[] directories = this.GetSearchDirectories();
            foreach (string directory in directories)
            {
                foreach (string extension in new[] { ".dll", ".exe", ".winmd" })
                {
                    string path = System.IO.Path.Combine(directory, name.Name + extension);
                    if (this.CandidatePaths.Add(this.ToLogicalPath(path)))
                    {
                        this.Stamp(path);
                    }
                }
            }
        }

        /// <summary>
        /// Records the file that backs the specified assembly, and returns the assembly.
        /// </summary>
        private AssemblyDefinition Track(AssemblyDefinition assembly)
        {
            foreach (var module in assembly?.Modules ?? (IEnumerable<ModuleDefinition>)Array.Empty<ModuleDefinition>())
            {
                if (!string.IsNullOrEmpty(module.FileName))
                {
                    if (this.ResolvedPaths.Add(this.ToLogicalPath(module.FileName)))
                    {
                        // Replace the earlier candidate probe exactly once, at the first successful
                        // resolution. Later metadata lookups of the same module consume the already
                        // loaded Cecil definition and must not repeatedly hash a large framework file.
                        this.Stamp(module.FileName, replace: true);
                    }
                }
            }

            return assembly;
        }
    }
}

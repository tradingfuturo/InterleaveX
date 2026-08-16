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
        /// <remarks>
        /// Every resolution funnels through here, including the parameterless overload above: Cecil's
        /// <see cref="BaseAssemblyResolver"/> implements that one by calling this one virtually with a
        /// fresh <see cref="ReaderParameters"/>, so setting InMemory here covers both without
        /// bypassing the <see cref="DefaultAssemblyResolver"/> cache that the other overload consults.
        /// </remarks>
        public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            this.StampCandidates(name);
            return this.Track(base.Resolve(name, WithoutHoldingTheFileOpen(parameters)));
        }

        /// <summary>
        /// Returns reader parameters that do not leave the resolved file open.
        /// </summary>
        /// <remarks>
        /// Rewriting overwrites the files it resolves. A batch that replaces its assemblies in place
        /// copies each rewritten assembly back over the snapshot copy it was read from, so that batch
        /// members rewritten later resolve signatures from the transformed dependency rather than the
        /// original. The whole batch is loaded before the first assembly is rewritten and nothing is
        /// disposed until the last one finishes, so a resolution taken while loading is still cached
        /// when that overwrite runs.
        ///
        /// The base implementation reads with a <see cref="ReaderParameters"/> of its own making, where
        /// InMemory is false, so the module keeps a FileStream on the file and
        /// <see cref="DefaultAssemblyResolver"/> caches that module for this resolver's lifetime. The
        /// overwrite then fails with "The process cannot access the file ... because it is being used
        /// by another process", and takes the run with it. Reading the bytes into memory instead costs
        /// the file's size and keeps the file writable, which is the state rewriting needs it in.
        ///
        /// Note that this is not the same read as the one <see cref="AssemblyInfo"/> performs for the
        /// assembly it owns: that one already sets InMemory, but it covers only that assembly, not
        /// anything reached through resolution.
        /// </remarks>
        private static ReaderParameters WithoutHoldingTheFileOpen(ReaderParameters parameters)
        {
            parameters ??= new ReaderParameters();
            parameters.InMemory = true;
            return parameters;
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

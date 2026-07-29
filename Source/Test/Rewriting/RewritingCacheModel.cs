// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System.Collections.Generic;

namespace Microsoft.Coyote.Rewriting
{
    /// <summary>
    /// The recorded state of a rewriting run.
    /// </summary>
    /// <remarks>
    /// Written as JSON into the output directory and read back by the next run. Lifted out of
    /// <see cref="RewritingCache"/> so that <see cref="RewritingCacheValidator"/> can be handed one
    /// directly: what these describe is the whole input to the decision that skips a rewrite, and
    /// checking that decision used to mean staging a real assembly and running the engine over it.
    ///
    /// The names here are the wire format. Renaming a property changes what a manifest written by an
    /// older build deserializes into, which is what <see cref="CacheManifest.SchemaVersion"/> is for.
    /// Moving these types between namespaces or nesting is safe: the serializer keys on property
    /// names, not on the declaring type.
    /// </remarks>
    internal sealed class CacheManifest
    {
        public int SchemaVersion { get; set; }

        public string FingerprintAlgorithm { get; set; }

        public string RewriterVersion { get; set; }

        public string RewriterModuleId { get; set; }

        public string AssembliesDirectory { get; set; }

        public string OutputDirectory { get; set; }

        public string ConfigurationHash { get; set; }

        public List<string> RequestedInputs { get; set; }

        public List<string> RewriteInputs { get; set; }

        public List<CacheFile> ResolvedModules { get; set; }

        public List<CacheDirectory> DependencySearchDirectories { get; set; }

        public List<CacheDirectoryListing> FrameworkInventories { get; set; }

        public List<CacheEntry> Entries { get; set; }
    }

    /// <summary>
    /// The versions a shared framework directory offered, by name.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="CacheDirectory"/> rather than folded into it as a third form,
    /// because the two answer different questions. A <see cref="CacheDirectory"/> says what a
    /// directory offered a resolver that searched it; this says which directories a roll-forward
    /// chose between before any of them was searched, and its content is deliberately no more than
    /// the names -- the winner's files are recorded by the other type.
    /// </remarks>
    internal sealed class CacheDirectoryListing
    {
        public string Path { get; set; }

        public bool Exists { get; set; }

        public string NamesHash { get; set; }
    }

    /// <summary>
    /// The recorded state of one directory that resolution searches.
    /// </summary>
    internal sealed class CacheDirectory
    {
        public string Path { get; set; }

        public bool Exists { get; set; }

        public string ContentHash { get; set; }

        /// <summary>
        /// True if <see cref="ContentHash"/> covers the bytes of each assembly offered here, false if
        /// it covers only their names, lengths and write times.
        /// </summary>
        /// <remarks>
        /// Recorded rather than decided again when the manifest is read, because the two forms are
        /// not comparable and only the run that wrote one knows which it chose. A manifest from
        /// before this was recorded reads as false and is recaptured that way, which does not match
        /// the older form either -- so it is rejected, and the run rewrites. That is the safe
        /// direction and costs one rewrite.
        /// </remarks>
        public bool IsContentHashed { get; set; }
    }

    /// <summary>
    /// The recorded state of one rewritten assembly.
    /// </summary>
    internal sealed class CacheEntry
    {
        public string Name { get; set; }

        public CacheFile Input { get; set; }

        public CacheFile Output { get; set; }

        public CacheFile Symbols { get; set; }

        public CacheFile OutputSymbols { get; set; }

        public CacheFile RuntimeConfig { get; set; }

        public List<string> ReferenceNames { get; set; }

        public List<string> PresentReferences { get; set; }

        public List<CacheFile> Artifacts { get; set; }

        public List<string> ThreadStaticFields { get; set; }
    }

    /// <summary>
    /// The recorded state of one file.
    /// </summary>
    internal sealed class CacheFile
    {
        public string Path { get; set; }

        public bool Exists { get; set; }

        public long Length { get; set; }

        public string Fingerprint { get; set; }
    }
}

// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Coyote.IO;

namespace Microsoft.Coyote.Rewriting
{
    /// <summary>
    /// What a run expects to find recorded, for a manifest to describe that same run.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than read back off <see cref="RewritingOptions"/> so that the decision can
    /// be checked against values a test chooses. The rewriter's own version and build identity are
    /// part of it for the same reason: they are properties of whatever built this assembly, and a
    /// test that had to match them could only ever assert that they matched themselves.
    /// </remarks>
    internal sealed class RewritingCacheExpectation
    {
        internal int SchemaVersion { get; }

        internal string RewriterVersion { get; }

        internal string RewriterModuleId { get; }

        internal string ConfigurationHash { get; }

        internal string AssembliesDirectory { get; }

        internal string OutputDirectory { get; }

        internal IReadOnlyCollection<string> InputPaths { get; }

        internal bool IsReplacingAssemblies { get; }

        internal bool IsLoggingAssemblyContents { get; }

        internal bool IsDiffingAssemblyContents { get; }

        internal RewritingCacheExpectation(int schemaVersion, string rewriterVersion, string rewriterModuleId,
            string configurationHash, string assembliesDirectory, string outputDirectory,
            IEnumerable<string> inputPaths, bool isReplacingAssemblies,
            bool isLoggingAssemblyContents = false, bool isDiffingAssemblyContents = false)
        {
            this.SchemaVersion = schemaVersion;
            this.RewriterVersion = rewriterVersion;
            this.RewriterModuleId = rewriterModuleId;
            this.ConfigurationHash = configurationHash;
            this.AssembliesDirectory = RewritingCacheValidator.NormalizeDirectory(assembliesDirectory);
            this.OutputDirectory = RewritingCacheValidator.NormalizeDirectory(outputDirectory);
            this.InputPaths = (inputPaths ?? Enumerable.Empty<string>())
                .Select(RewritingCacheValidator.NormalizeFile).ToList();
            this.IsReplacingAssemblies = isReplacingAssemblies;
            this.IsLoggingAssemblyContents = isLoggingAssemblyContents;
            this.IsDiffingAssemblyContents = isDiffingAssemblyContents;
        }

        /// <summary>
        /// Returns the path the specified input assembly is written to.
        /// </summary>
        internal string GetOutputPath(string inputPath) => this.IsReplacingAssemblies ? inputPath :
            Path.Combine(this.OutputDirectory, Path.GetFileName(inputPath));
    }

    /// <summary>
    /// Decides whether a recorded rewriting run still describes what is on disk.
    /// </summary>
    /// <remarks>
    /// This is the part of <see cref="RewritingCache"/> whose answer matters most and which was
    /// hardest to reach. A wrong "yes" here is not a slow build but a silent one: the tests then run
    /// against an assembly that was never instrumented, and nothing downstream detects it. Every
    /// uncertain case therefore resolves to "not up to date" rather than to a skip.
    ///
    /// Separated from the cache, and given its file system rather than reaching for the real one, so
    /// that each of those cases can be set up in memory. Reaching them before meant staging a copy
    /// of a real assembly in a temporary directory and running the whole engine over it, which is
    /// why most of them had no test of their own.
    /// </remarks>
    internal sealed class RewritingCacheValidator
    {
        /// <summary>
        /// The algorithm and wire representation used for cache content fingerprints.
        /// </summary>
        internal const string FingerprintAlgorithm = "xxh128-v1";

        /// <summary>
        /// How much of a file is appended to the fingerprint at a time.
        /// </summary>
        private const int FingerprintBufferSize = 1 << 16;

        private readonly IFileSystem FileSystem;
        private readonly RewritingCacheExpectation Expectation;

        /// <summary>
        /// Compares paths as the file system holding each of them does.
        /// </summary>
        /// <remarks>
        /// This one is for the files a run reads: its inputs, the modules it resolved, and the
        /// directories it searched. Those can sit anywhere, and deciding how to compare them from the
        /// output directory is how two distinct dependency trees came to be folded into one -- after
        /// which only one of them is fingerprinted and a change in the other is invisible.
        ///
        /// Use <see cref="OutputPathComparer"/> instead for anything named relative to the output.
        /// The two agree on the ordinary run, where everything is on one volume, and the difference
        /// only shows up on the run that would otherwise be misjudged.
        /// </remarks>
        internal IEqualityComparer<string> PathComparer { get; }

        /// <summary>
        /// Compares paths as the file system holding this run's output does.
        /// </summary>
        /// <remarks>
        /// For the files this run writes, which are all under one directory and so all answer to one
        /// comparer. <see cref="PathComparison"/> matches it.
        /// </remarks>
        internal StringComparer OutputPathComparer { get; }

        /// <summary>
        /// The <see cref="StringComparison"/> matching <see cref="OutputPathComparer"/>.
        /// </summary>
        internal StringComparison PathComparison { get; }

        internal RewritingCacheValidator(IFileSystem fileSystem, RewritingCacheExpectation expectation)
        {
            this.FileSystem = fileSystem;
            this.Expectation = expectation;

            bool isCaseInsensitive = fileSystem.IsCaseInsensitive(expectation.OutputDirectory);
            this.OutputPathComparer = isCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            this.PathComparison = isCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            this.PathComparer = new FileSystemPathComparer(fileSystem);
        }

        /// <summary>
        /// Checks whether the specified manifest describes the run that is about to happen, and
        /// whether every file it recorded is unchanged.
        /// </summary>
        internal bool IsManifestCurrent(CacheManifest manifest, out string reason)
        {
            reason = null;
            if (manifest is null)
            {
                reason = "there is none";
                return false;
            }

            if (manifest.SchemaVersion != this.Expectation.SchemaVersion)
            {
                reason = "it was written in an older format";
                return false;
            }

            if (manifest.FingerprintAlgorithm != FingerprintAlgorithm)
            {
                reason = "it uses an unsupported content fingerprint";
                return false;
            }

            if (manifest.RewriterVersion != this.Expectation.RewriterVersion ||
                manifest.RewriterModuleId != this.Expectation.RewriterModuleId)
            {
                // The version alone is not enough: it changes rarely, so a locally rebuilt rewriter
                // carries the same one while emitting different IL.
                reason = "it was written by a different build of the rewriter";
                return false;
            }

            if (manifest.ConfigurationHash != this.Expectation.ConfigurationHash)
            {
                reason = "the rewriting configuration changed";
                return false;
            }

            if (!this.PathComparer.Equals(manifest.AssembliesDirectory, this.Expectation.AssembliesDirectory) ||
                !this.OutputPathComparer.Equals(manifest.OutputDirectory, this.Expectation.OutputDirectory))
            {
                // Guards against a manifest that was copied into this directory from elsewhere, which
                // the input tree copy can do when an earlier in-place run left one behind.
                reason = "it was written for a different directory";
                return false;
            }

            if (manifest.RequestedInputs is null || manifest.RewriteInputs is null ||
                manifest.Entries is null || manifest.ResolvedModules is null ||
                manifest.DependencySearchDirectories is null || manifest.FrameworkInventories is null)
            {
                reason = "it is incomplete";
                return false;
            }

            var expectedInputs = new HashSet<string>(this.Expectation.InputPaths, this.PathComparer);
            if (!this.TryCreatePathSet(manifest.RequestedInputs, out HashSet<string> requestedInputs) ||
                !requestedInputs.SetEquals(expectedInputs))
            {
                reason = "it was written for a different set of requested assemblies";
                return false;
            }

            if (!this.TryCreatePathSet(manifest.RewriteInputs, out HashSet<string> rewriteInputs) ||
                !expectedInputs.IsSubsetOf(rewriteInputs))
            {
                reason = "its rewrite closure is incomplete or contains duplicate paths";
                return false;
            }

            // Every assembly in the closure must be described exactly once, by an entry whose
            // recorded paths are the ones this run would use.
            var seenInputs = new HashSet<string>(this.PathComparer);
            var entriesByInput = new Dictionary<string, CacheEntry>(this.PathComparer);
            foreach (var entry in manifest.Entries)
            {
                if (string.IsNullOrEmpty(entry?.Name) || entry.Input is null || entry.Output is null ||
                    entry.Symbols is null || entry.OutputSymbols is null || entry.RuntimeConfig is null ||
                    entry.ReferenceNames is null || entry.PresentReferences is null ||
                    entry.Artifacts is null)
                {
                    reason = "an entry is incomplete";
                    return false;
                }

                var referenceNames = new HashSet<string>(StringComparer.Ordinal);
                var presentReferences = new HashSet<string>(StringComparer.Ordinal);
                if (entry.ReferenceNames.Any(name => string.IsNullOrEmpty(name) ||
                    !referenceNames.Add(name)) ||
                    entry.PresentReferences.Any(name => string.IsNullOrEmpty(name) ||
                    !presentReferences.Add(name)) ||
                    !presentReferences.IsSubsetOf(referenceNames))
                {
                    reason = $"the dependency graph of '{entry.Name}' is invalid";
                    return false;
                }

                string inputPath;
                try
                {
                    inputPath = NormalizeFile(entry.Input.Path);
                }
                catch (Exception)
                {
                    reason = "an entry contains an invalid input path";
                    return false;
                }

                if (!seenInputs.Add(inputPath))
                {
                    reason = $"'{entry.Name}' is recorded more than once";
                    return false;
                }

                entriesByInput.Add(inputPath, entry);
                string outputPath;
                string expectedOutputPath;
                try
                {
                    outputPath = NormalizeFile(entry.Output.Path);
                    expectedOutputPath = NormalizeFile(this.Expectation.GetOutputPath(entry.Input.Path));
                }
                catch (Exception)
                {
                    reason = $"the output path of '{entry.Name}' is invalid";
                    return false;
                }

                if (!this.OutputPathComparer.Equals(outputPath, expectedOutputPath))
                {
                    reason = $"the output path of '{entry.Name}' changed";
                    return false;
                }

                var expectedArtifacts = new HashSet<string>(this.OutputPathComparer);
                if (this.Expectation.IsLoggingAssemblyContents)
                {
                    expectedArtifacts.Add(NormalizeFile(Path.ChangeExtension(outputPath, ".il.json")));
                    expectedArtifacts.Add(NormalizeFile(Path.ChangeExtension(outputPath, ".rw.json")));
                }

                if (this.Expectation.IsDiffingAssemblyContents)
                {
                    expectedArtifacts.Add(NormalizeFile(Path.ChangeExtension(outputPath, ".diff.json")));
                }

                if (!this.TryCreatePathSet(entry.Artifacts.Select(artifact => artifact?.Path),
                    out HashSet<string> artifactPaths, this.OutputPathComparer) ||
                    !artifactPaths.SetEquals(expectedArtifacts))
                {
                    reason = $"the debug artifacts of '{entry.Name}' are incomplete or unexpected";
                    return false;
                }
            }

            if (!rewriteInputs.SetEquals(seenInputs))
            {
                reason = "its entries do not exactly cover the recorded rewrite closure";
                return false;
            }

            if (!this.IsClosureReachable(expectedInputs, entriesByInput))
            {
                reason = "its rewrite closure contains an assembly unreachable from the requested inputs";
                return false;
            }

            foreach (var entry in manifest.Entries)
            {
                if (!this.IsEntryCurrent(entry, out string entryReason))
                {
                    reason = entryReason;
                    return false;
                }
            }

            var resolvedModulePaths = new HashSet<string>(this.PathComparer);
            foreach (var module in manifest.ResolvedModules)
            {
                string modulePath;
                try
                {
                    modulePath = NormalizeFile(module?.Path);
                }
                catch (Exception)
                {
                    modulePath = null;
                }

                if (string.IsNullOrEmpty(modulePath) || !resolvedModulePaths.Add(modulePath) ||
                    !HasContentFingerprintWhenPresent(module) || !this.IsFileCurrent(module))
                {
                    reason = $"the resolved assembly '{module?.Path}' changed or is invalid";
                    return false;
                }
            }

            var searchDirectoryPaths = new HashSet<string>(this.PathComparer);
            foreach (var directory in manifest.DependencySearchDirectories)
            {
                string directoryPath;
                try
                {
                    directoryPath = NormalizeDirectory(directory?.Path);
                }
                catch (Exception)
                {
                    directoryPath = null;
                }

                if (string.IsNullOrEmpty(directoryPath) || !searchDirectoryPaths.Add(directoryPath) ||
                    directory is null || !directory.IsContentHashed ||
                    (directory.Exists && !IsFingerprint(directory.ContentHash)) ||
                    (!directory.Exists && directory.ContentHash != null) ||
                    !this.IsDirectoryCurrent(directory))
                {
                    reason = $"the assemblies offered by the '{directory?.Path}' search directory changed or are invalid";
                    return false;
                }
            }

            var inventoryPaths = new HashSet<string>(this.PathComparer);
            foreach (var inventory in manifest.FrameworkInventories)
            {
                string inventoryPath;
                try
                {
                    inventoryPath = NormalizeDirectory(inventory?.Path);
                }
                catch (Exception)
                {
                    inventoryPath = null;
                }

                if (string.IsNullOrEmpty(inventoryPath) || !inventoryPaths.Add(inventoryPath) ||
                    (inventory.Exists && !IsFingerprint(inventory.NamesHash)) ||
                    (!inventory.Exists && inventory.NamesHash != null) ||
                    !this.IsInventoryCurrent(inventory))
                {
                    reason = $"the framework versions installed in '{inventory?.Path}' changed or are invalid";
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns true if the specified paths are valid, normalized and unique.
        /// </summary>
        private bool TryCreatePathSet(IEnumerable<string> paths, out HashSet<string> normalized,
            IEqualityComparer<string> comparer = null)
        {
            normalized = new HashSet<string>(comparer ?? this.PathComparer);
            try
            {
                foreach (string path in paths)
                {
                    if (string.IsNullOrEmpty(path) || !normalized.Add(NormalizeFile(path)))
                    {
                        return false;
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Returns true if every recorded dependency can be reached from an explicitly requested
        /// assembly through sibling references that were present when the run was recorded.
        /// </summary>
        private bool IsClosureReachable(HashSet<string> roots, Dictionary<string, CacheEntry> entries)
        {
            var reachable = new HashSet<string>(roots, this.PathComparer);
            var pending = new Queue<string>(roots);
            while (pending.Count > 0)
            {
                string inputPath = pending.Dequeue();
                if (!entries.TryGetValue(inputPath, out CacheEntry entry))
                {
                    return false;
                }

                string directory = Path.GetDirectoryName(inputPath);
                foreach (string referenceName in entry.PresentReferences)
                {
                    string dependency = NormalizeFile(Path.Combine(directory, referenceName + ".dll"));
                    if (entries.ContainsKey(dependency) && reachable.Add(dependency))
                    {
                        pending.Enqueue(dependency);
                    }
                }
            }

            return reachable.Count == entries.Count;
        }

        /// <summary>
        /// Checks whether everything the specified entry recorded is unchanged.
        /// </summary>
        internal bool IsEntryCurrent(CacheEntry entry, out string reason)
        {
            reason = null;
            if (!HasContentFingerprint(entry.Input) || !this.IsFileCurrent(entry.Input))
            {
                reason = $"'{entry.Name}' changed";
                return false;
            }

            if (!HasContentFingerprint(entry.Output) || !this.IsFileCurrent(entry.Output))
            {
                reason = $"the rewritten '{entry.Name}' changed";
                return false;
            }

            // Symbols are read from beside the input, because that is what decides whether they are
            // read at all, and so whether they are written. A symbol file appearing or disappearing
            // there changes what a rewrite would produce, which is one of the cases 'IsFileCurrent'
            // already answers, alongside the file having changed.
            if (!HasContentFingerprintWhenPresent(entry.Symbols) ||
                !this.IsFileCurrent(entry.Symbols))
            {
                reason = $"the symbols of '{entry.Name}' appeared, disappeared or changed";
                return false;
            }

            if (!HasContentFingerprintWhenPresent(entry.OutputSymbols) ||
                !this.IsFileCurrent(entry.OutputSymbols))
            {
                reason = $"the written symbols of '{entry.Name}' changed";
                return false;
            }

            // The runtime config names the shared frameworks that resolution falls back to, so
            // editing it points the rewriter at different implementation assemblies without touching
            // a single file that anything else here records.
            if (!HasContentFingerprintWhenPresent(entry.RuntimeConfig) ||
                !this.IsFileCurrent(entry.RuntimeConfig))
            {
                reason = $"the runtime configuration of '{entry.Name}' appeared, disappeared or changed";
                return false;
            }

            // Which assemblies get rewritten is decided by probing the input directory for each
            // reference, so a reference file appearing or disappearing changes the set even when
            // every recorded file is untouched.
            string assemblyDirectory = Path.GetDirectoryName(entry.Input.Path);
            var presentReferences = new HashSet<string>(entry.PresentReferences, StringComparer.Ordinal);
            foreach (string referenceName in entry.ReferenceNames)
            {
                string referencePath = Path.Combine(assemblyDirectory, referenceName + ".dll");
                if (this.FileSystem.FileExists(referencePath) != presentReferences.Contains(referenceName))
                {
                    reason = $"the dependency '{referenceName}' of '{entry.Name}' appeared or disappeared";
                    return false;
                }
            }

            foreach (var artifact in entry.Artifacts ?? Enumerable.Empty<CacheFile>())
            {
                if (!HasContentFingerprintWhenPresent(artifact) ||
                    !this.IsFileCurrent(artifact))
                {
                    reason = $"the '{Path.GetFileName(artifact.Path)}' debug artifact changed";
                    return false;
                }
            }

            if (entry.ThreadStaticFields is null)
            {
                reason = $"the diagnostics of '{entry.Name}' were not recorded";
                return false;
            }

            return true;
        }

        private static bool HasContentFingerprint(CacheFile file) =>
            file?.Exists is true && IsFingerprint(file.Fingerprint);

        private static bool HasContentFingerprintWhenPresent(CacheFile file) =>
            file != null && (!file.Exists || IsFingerprint(file.Fingerprint));

        private static bool IsFingerprint(string value)
        {
            if (value is null || value.Length != 32)
            {
                return false;
            }

            foreach (char c in value)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Checks whether the specified file is exactly as it was when it was recorded, including
        /// having been absent then and now.
        /// </summary>
        internal bool IsFileCurrent(CacheFile file)
        {
            if (file is null || string.IsNullOrEmpty(file.Path))
            {
                return false;
            }

            CacheFile current;
            try
            {
                current = this.CaptureFile(file.Path, file.Fingerprint != null);
            }
            catch (Exception)
            {
                return false;
            }

            if (!current.Exists || !file.Exists)
            {
                return current.Exists == file.Exists;
            }

            if (current.Length != file.Length)
            {
                // Cheap rejection of the common case, a rebuilt assembly, without reading the file.
                return false;
            }

            return file.Fingerprint is null ||
                string.Equals(current.Fingerprint, file.Fingerprint, StringComparison.Ordinal);
        }

        /// <summary>
        /// Checks whether a search directory still offers what it did.
        /// </summary>
        internal bool IsDirectoryCurrent(CacheDirectory directory)
        {
            // Recaptured the way it was recorded. Comparing a content hash against a metadata one
            // would report every directory as changed, and deciding the form here from what this run
            // would choose would report a change whenever that decision moved rather than whenever
            // the directory did.
            try
            {
                var current = this.CaptureDirectory(directory.Path, directory.IsContentHashed);
                return current.Exists == directory.Exists &&
                    string.Equals(current.ContentHash, directory.ContentHash, StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Checks whether the same framework versions are installed as when this was recorded.
        /// </summary>
        internal bool IsInventoryCurrent(CacheDirectoryListing inventory)
        {
            try
            {
                var current = this.CaptureDirectoryNames(inventory.Path);
                return current.Exists == inventory.Exists &&
                    string.Equals(current.NamesHash, inventory.NamesHash, StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Captures which versions a shared framework directory currently offers, by name.
        /// </summary>
        /// <remarks>
        /// Names only, and not recursive. What this answers is which candidates the roll-forward that
        /// picks a framework directory would choose between, and that is decided by the names of the
        /// directories alone -- the content of the one it settles on is already recorded, as an
        /// ordinary search directory. Installing a newer patch of the same major adds a name here and
        /// changes nothing else anywhere, which is precisely the case that used to go unnoticed.
        ///
        /// So this reads no files, which is what makes it affordable to take over every framework a
        /// run asks for, on every check as well as on every write.
        /// </remarks>
        internal CacheDirectoryListing CaptureDirectoryNames(string path)
        {
            bool exists = this.FileSystem.DirectoryExists(path);
            var directories = exists ? this.FileSystem.GetDirectories(path, "*", false) :
                Array.Empty<string>();
            return CaptureDirectoryNames(path, exists, directories);
        }

        internal static CacheDirectoryListing CaptureDirectoryNames(string path, bool exists,
            IEnumerable<string> directories)
        {
            var ordered = (directories ?? Enumerable.Empty<string>())
                .OrderBy(directory => directory, StringComparer.Ordinal).ToArray();
            var builder = new StringBuilder();
            foreach (string directory in ordered)
            {
                builder.Append(Path.GetFileName(directory.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))).Append('\n');
            }

            return new CacheDirectoryListing()
            {
                Path = NormalizeDirectory(path),
                Exists = exists,
                NamesHash = exists ? ComputeFingerprint(Encoding.UTF8.GetBytes(builder.ToString())) : null
            };
        }

        /// <summary>
        /// Captures the current state of the specified file.
        /// </summary>
        /// <param name="path">The file to capture.</param>
        /// <param name="hashContent">
        /// True to record a content fingerprint, false to record only its length. Content is what decides
        /// whether a rewrite would produce something different, so anything feeding the rewrite and
        /// every generated artifact used to validate the cache is hashed.
        /// </param>
        internal CacheFile CaptureFile(string path, bool hashContent)
        {
            Exception lastError = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var before = this.FileSystem.GetFile(path);
                    var file = new CacheFile()
                    {
                        Path = NormalizeFile(path),
                        Exists = before.Exists,
                        Length = before.Exists ? before.Length : 0
                    };

                    if (before.Exists && hashContent)
                    {
                        file.Fingerprint = this.ComputeFileFingerprint(path);
                    }

                    var after = this.FileSystem.GetFile(path);
                    if (before.Exists == after.Exists &&
                        (!before.Exists || (before.Length == after.Length &&
                            before.LastWriteTimeUtc == after.LastWriteTimeUtc)))
                    {
                        return file;
                    }

                    lastError = new IOException($"The file '{path}' changed while it was being fingerprinted.");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    lastError = ex;
                }
            }

            throw new IOException($"Unable to capture a stable fingerprint of '{path}'.", lastError);
        }

        /// <summary>
        /// Captures the assemblies currently on offer in the specified directory.
        /// </summary>
        /// <remarks>
        /// The recorded modules answer "did anything this run read change", but not "is there now
        /// something else it would have read instead". An assembly that appears in a searched
        /// directory can win a resolution that previously went elsewhere, or satisfy one that
        /// previously failed, and nothing else here would notice: every file the last run touched is
        /// untouched. So what is recorded is the offer rather than the outcome -- the name and size
        /// of each assembly in the directory, which changes whenever one appears, goes, or is
        /// replaced.
        ///
        /// This is taken over every directory resolution was given, not only the configured ones, so
        /// that an installed framework patch or an assembly appearing beside the rewriter counts
        /// too. It deliberately reports a change for an assembly the rewriter would never have
        /// looked at, which costs a rewrite that was not strictly needed. That is the direction this
        /// class errs in everywhere: the alternative is trusting a resolution that did not happen.
        /// </remarks>
        /// <param name="path">The directory to capture.</param>
        /// <param name="hashContent">
        /// True to record the bytes of each assembly, false to record only its length and write
        /// time. Production cache manifests use the content form for every resolution directory;
        /// the metadata form remains only so old-format and focused validator cases fail closed.
        /// </param>
        internal CacheDirectory CaptureDirectory(string path, bool hashContent)
        {
            Exception lastError = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    return this.CaptureDirectoryOnce(path, hashContent);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    lastError = ex;
                }
            }

            throw new IOException($"Unable to capture a stable fingerprint of directory '{path}'.",
                lastError);
        }

        private CacheDirectory CaptureDirectoryOnce(string path, bool hashContent)
        {
            var directory = new CacheDirectory()
            {
                Path = path,
                Exists = this.FileSystem.DirectoryExists(path),
                IsContentHashed = hashContent
            };

            if (directory.Exists)
            {
                // Not recursive, because 'AddSearchDirectory' is not either.
                //
                // Each key is taken once and then sorted, rather than sorting on a key recomputed per
                // comparison and read again per line, and the name, length and write time all come
                // from the listing itself rather than from a call per file. This runs over the shared
                // framework directories, which hold several hundred assemblies each, and it runs both
                // when the manifest is checked and when it is written.
                var entries = this.GetResolvableEntries(path);
                var files = new List<KeyValuePair<string, string>>(entries.Count);
                foreach (var entry in entries)
                {
                    string identity = hashContent ? this.CaptureFile(entry.Path, true).Fingerprint :
                        entry.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
                    files.Add(new KeyValuePair<string, string>(Path.GetFileName(entry.Path),
                        entry.Length.ToString(CultureInfo.InvariantCulture) + "|" + identity));
                }

                files.Sort((left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));

                var builder = new StringBuilder();
                foreach (var file in files)
                {
                    builder.Append(file.Key).Append('|').Append(file.Value).Append('\n');
                }

                var after = this.GetResolvableEntries(path);
                if (!HaveSameEntries(entries, after))
                {
                    throw new IOException($"The directory '{path}' changed while it was being fingerprinted.");
                }

                directory.ContentHash = ComputeFingerprint(Encoding.UTF8.GetBytes(builder.ToString()));
            }

            if (this.FileSystem.DirectoryExists(path) != directory.Exists)
            {
                throw new IOException($"The directory '{path}' changed while it was being fingerprinted.");
            }

            return directory;
        }

        /// <summary>
        /// The file extensions an assembly resolver will accept as a candidate for a reference.
        /// </summary>
        /// <remarks>
        /// Mono.Cecil probes '.exe' and '.dll' for an ordinary reference, and '.winmd' and '.dll' for
        /// a Windows Runtime one. Recording only the assemblies meant an executable appearing in a
        /// searched directory changed nothing here while changing what resolution answers -- and it
        /// does not merely satisfy a reference that used to fail: '.exe' is probed *before* '.dll',
        /// so a new one takes a resolution that currently goes to an assembly beside it.
        /// </remarks>
        private static readonly string[] ResolvableExtensions = new[] { ".dll", ".exe", ".winmd" };

        /// <summary>
        /// Returns the files in the specified directory that could satisfy a reference.
        /// </summary>
        /// <remarks>
        /// Listed once and filtered here rather than asked for one pattern at a time: the listing is
        /// the expensive half, and this runs twice per directory per capture to notice a directory
        /// changing underneath it.
        /// </remarks>
        private IReadOnlyList<IFileEntry> GetResolvableEntries(string path)
        {
            var entries = new List<IFileEntry>();
            foreach (var entry in this.FileSystem.GetFileEntries(path, "*"))
            {
                if (ResolvableExtensions.Contains(Path.GetExtension(entry.Path),
                    StringComparer.OrdinalIgnoreCase))
                {
                    entries.Add(entry);
                }
            }

            return entries;
        }

        private static bool HaveSameEntries(IReadOnlyList<IFileEntry> left, IReadOnlyList<IFileEntry> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            var leftEntries = left.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
            var rightEntries = right.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
            for (int idx = 0; idx < leftEntries.Length; idx++)
            {
                if (!string.Equals(leftEntries[idx].Path, rightEntries[idx].Path, StringComparison.Ordinal) ||
                    leftEntries[idx].Length != rightEntries[idx].Length ||
                    leftEntries[idx].LastWriteTimeUtc != rightEntries[idx].LastWriteTimeUtc)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Computes the XXH128 fingerprint of the specified file.
        /// </summary>
        internal string ComputeFileFingerprint(string path) =>
            ComputeFileFingerprint(this.FileSystem, path);

        /// <summary>
        /// Computes a content fingerprint through the supplied file system.
        /// </summary>
        internal static string ComputeFileFingerprint(IFileSystem fileSystem, string path)
        {
            // Refuse a writer while reading. A hash of bytes from the middle of a replacement can
            // accidentally describe neither the old nor the new input, and that evidence is later
            // used to protect rewritten output from being overwritten.
            using var stream = fileSystem.OpenRead(path, FileReadSharing.DenyWriters);
            var algorithm = new XxHash128();
            byte[] buffer = new byte[FingerprintBufferSize];
            while (true)
            {
                int count = stream.Read(buffer, 0, buffer.Length);
                if (count is 0)
                {
                    break;
                }

                algorithm.Append(new ReadOnlySpan<byte>(buffer, 0, count));
            }

            return ToHexString(algorithm.GetCurrentHash());
        }

        /// <summary>
        /// Computes the XXH128 fingerprint of the specified data.
        /// </summary>
        internal static string ComputeFingerprint(byte[] data) =>
            ToHexString(XxHash128.Hash(data));

        /// <summary>
        /// Computes the SHA256 identity used by embedded rewriting signatures and small
        /// configuration values that are not on the large-file cache hot path.
        /// </summary>
        internal static string ComputeSha256(byte[] data)
        {
            using var algorithm = SHA256.Create();
            return ToHexString(algorithm.ComputeHash(data));
        }

        /// <summary>
        /// Returns the full path of the specified file, so that the same file is recorded the same
        /// way however it was spelled on the command line.
        /// </summary>
        internal static string NormalizeFile(string path) =>
            string.IsNullOrEmpty(path) ? string.Empty : Path.GetFullPath(path);

        /// <summary>
        /// Returns the full path of the specified directory, without a trailing separator.
        /// </summary>
        internal static string NormalizeDirectory(string path) =>
            string.IsNullOrEmpty(path) ? string.Empty :
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        /// <summary>
        /// Formats the specified data as a hexadecimal string.
        /// </summary>
        private static string ToHexString(byte[] data)
        {
            var builder = new StringBuilder(data.Length * 2);
            foreach (byte b in data)
            {
                builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }
    }
}

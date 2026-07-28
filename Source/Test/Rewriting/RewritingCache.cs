// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Coyote.Logging;

namespace Microsoft.Coyote.Rewriting
{
    /// <summary>
    /// Records what a rewriting run produced, so that a later run over unchanged inputs can skip it.
    /// </summary>
    /// <remarks>
    /// The <see cref="RewritingSignatureAttribute"/> that rewriting stamps onto an assembly answers
    /// "was this rewritten with this configuration", but not "is this output current with respect to
    /// its input": the signature contains no content hash of the input, so two different builds of the
    /// same assembly version carry the same signature. This cache answers the second question, by
    /// recording the content of every file a run consumed and produced and re-checking it on the next
    /// run. A stale answer here would mean running tests against an assembly that was never
    /// instrumented, which no other check in the pipeline detects, so every uncertain case -- an
    /// unreadable manifest, an unexpected path, a file that is present when it was recorded absent --
    /// resolves to "not up to date" rather than to a skip.
    /// </remarks>
    internal sealed class RewritingCache
    {
        /// <summary>
        /// The name of the manifest file, written in the output directory.
        /// </summary>
        internal const string ManifestFileName = "rewriting.cache.json";

        /// <summary>
        /// The layout of the manifest. Bump this whenever the recorded state changes meaning, so that
        /// a manifest written by an older build is discarded rather than misread.
        /// </summary>
        private const int CurrentSchemaVersion = 3;

        /// <summary>
        /// The path prefix shared by every module that ships with the .NET installation, else null if
        /// there is no discoverable installation.
        /// </summary>
        /// <remarks>
        /// Resolved once: <see cref="AssemblyInfo.GetDotnetRoot"/> reaches the file system, and this
        /// is consulted for every one of the hundreds of modules a run resolves.
        /// </remarks>
        private static readonly Lazy<string> SharedFrameworkPrefix = new Lazy<string>(() =>
        {
            string dotnetRoot = AssemblyInfo.GetDotnetRoot();
            return string.IsNullOrEmpty(dotnetRoot) ? null :
                NormalizeDirectory(Path.Combine(dotnetRoot, "shared")) + Path.DirectorySeparatorChar;
        });

        /// <summary>
        /// The rewriting options of the current run.
        /// </summary>
        private readonly RewritingOptions Options;

        /// <summary>
        /// Responsible for writing to the installed <see cref="ILogger"/>.
        /// </summary>
        private readonly LogWriter LogWriter;

        /// <summary>
        /// The path of the manifest file.
        /// </summary>
        private readonly string ManifestPath;

        /// <summary>
        /// Identifies everything about this run other than the content of the files it reads.
        /// </summary>
        private readonly string ConfigurationHash;

        /// <summary>
        /// The entries recorded during this run.
        /// </summary>
        private readonly List<CacheEntry> RecordedEntries;

        /// <summary>
        /// The modules resolved during this run, keyed by normalized path to keep one record per file.
        /// </summary>
        private readonly Dictionary<string, CacheFile> RecordedModules;

        /// <summary>
        /// The directories resolution searched during this run, normalized to keep one record each.
        /// </summary>
        private readonly HashSet<string> RecordedSearchDirectories;

        /// <summary>
        /// Compares paths as the file system holding this run's output does.
        /// </summary>
        private readonly StringComparer PathComparer;

        /// <summary>
        /// The <see cref="StringComparison"/> matching <see cref="PathComparer"/>.
        /// </summary>
        private readonly StringComparison PathComparison;

        /// <summary>
        /// True if the cache must not be consulted during this run, else false. A disabled cache is
        /// still written, so that disabling it once does not force the run after it to redo the work.
        /// </summary>
        private readonly bool IsDisabled;

        /// <summary>
        /// The manifest that <see cref="TryGetUpToDateRun"/> accepted, else null.
        /// </summary>
        private CacheManifest AcceptedManifest;

        /// <summary>
        /// Initializes a new instance of the <see cref="RewritingCache"/> class.
        /// </summary>
        internal RewritingCache(RewritingOptions options, Configuration configuration, LogWriter logWriter)
        {
            this.Options = options;
            this.LogWriter = logWriter;
            this.ManifestPath = Path.Combine(options.OutputDirectory, ManifestFileName);
            this.ConfigurationHash = ComputeConfigurationHash(options, configuration);
            this.PathComparer = GetPathComparer(options.OutputDirectory);
            this.PathComparison = GetPathComparison(options.OutputDirectory);
            this.RecordedEntries = new List<CacheEntry>();
            this.RecordedModules = new Dictionary<string, CacheFile>(this.PathComparer);
            this.RecordedSearchDirectories = new HashSet<string>(this.PathComparer);
            this.IsDisabled = options.IsIncrementalRewritingDisabled;
        }

        /// <summary>
        /// Checks whether every assembly this run was asked to rewrite is already up to date.
        /// </summary>
        /// <param name="protectedOutputPaths">
        /// The output files that must not be overwritten while copying the input directory. This is
        /// empty unless the whole run is up to date, because an output that is about to be rewritten
        /// has nothing worth protecting, and a manifest that failed validation must never be able to
        /// suppress a copy.
        /// </param>
        /// <returns>True if the run can be skipped in its entirety, else false.</returns>
        internal bool TryGetUpToDateRun(out HashSet<string> protectedOutputPaths)
        {
            protectedOutputPaths = new HashSet<string>(this.PathComparer);
            if (this.IsDisabled)
            {
                return false;
            }

            try
            {
                CacheManifest manifest = this.ReadManifest();
                if (manifest is null)
                {
                    return false;
                }

                if (!this.IsManifestCurrent(manifest, out string reason))
                {
                    this.LogWriter.LogDebug("..... Rewriting cache is not usable: {0}", reason);
                    return false;
                }

                this.AcceptedManifest = manifest;
                foreach (var entry in manifest.Entries)
                {
                    // The written symbols, not the ones beside the input: this set is matched against
                    // paths in the output directory, and the copy only runs when the two directories
                    // differ, so an input path here would never match anything.
                    protectedOutputPaths.Add(entry.Output.Path);
                    if (entry.OutputSymbols?.Exists is true)
                    {
                        protectedOutputPaths.Add(entry.OutputSymbols.Path);
                    }

                    // The debug artifacts too, and whether or not they are currently there. An input
                    // directory that was rewritten in place holds artifacts of its own under the same
                    // names, and the copy would otherwise put those over the ones this output
                    // directory is meant to hold -- or invent one where this configuration produces
                    // none. They are compared by length alone, which is affordable only because
                    // nothing is allowed to write them but the run that produces them.
                    foreach (var artifact in entry.Artifacts ?? Enumerable.Empty<CacheFile>())
                    {
                        protectedOutputPaths.Add(artifact.Path);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                // A cache that cannot be read is a cache that reports nothing as up to date. It must
                // never be a reason to fail a run.
                this.LogWriter.LogDebug("..... Unable to read the rewriting cache: {0}", ex.Message);
                protectedOutputPaths.Clear();
                this.AcceptedManifest = null;
                return false;
            }
        }

        /// <summary>
        /// Re-emits the diagnostics that the skipped run would have produced.
        /// </summary>
        /// <remarks>
        /// <see cref="ThreadStaticDetectionPass"/> reports on every assembly, including ones that are
        /// already rewritten, because an incremental build is exactly when its output is most likely to
        /// be read. Skipping the run must not silence it, so its findings are recorded and replayed.
        /// </remarks>
        internal void ReplayDiagnostics()
        {
            if (this.AcceptedManifest is null)
            {
                return;
            }

            foreach (var entry in this.AcceptedManifest.Entries)
            {
                if (entry.ThreadStaticFields?.Count > 0)
                {
                    ThreadStaticDetectionPass.ReportDetectedFields(this.LogWriter, entry.Name, entry.ThreadStaticFields);
                }
            }
        }

        /// <summary>
        /// Records what rewriting the specified assembly consumed and produced.
        /// </summary>
        /// <remarks>
        /// Called after the output has reached its final location, so that the recorded hashes describe
        /// the files as a later run will find them. This runs for assemblies that were skipped as
        /// already rewritten too: recording what is actually on disk, rather than what a rewrite would
        /// have produced, is what lets the cache settle instead of missing on every run.
        /// </remarks>
        internal void RecordAssembly(AssemblyInfo assembly, string outputPath, IEnumerable<string> threadStaticFields)
        {
            try
            {
                string assemblyDirectory = Path.GetDirectoryName(assembly.FilePath);
                var entry = new CacheEntry()
                {
                    Name = assembly.Name,
                    Input = CaptureFile(assembly.FilePath, true),
                    Output = CaptureFile(outputPath, true),

                    // Read from beside the input, which is what gates reading symbols and therefore
                    // writing them: recording the produced one instead would miss a symbol file
                    // appearing next to the input, which changes the output. Captured whether or not
                    // it is there, because a 'CacheFile' records absence as faithfully as content.
                    Symbols = CaptureFile(Path.ChangeExtension(assembly.FilePath, "pdb"), true),
                    OutputSymbols = CaptureFile(Path.ChangeExtension(outputPath, "pdb"), true),

                    // Which shared frameworks resolution falls back to is read from here, so its
                    // content decides what the rewriter can see just as the assemblies themselves do.
                    // The expression is the one 'AssemblyInfo.GetFrameworksFromRuntimeConfig' uses, so
                    // that this records the file that was actually read.
                    RuntimeConfig = CaptureFile(
                        Path.ChangeExtension(assembly.FilePath, ".runtimeconfig.json"), true),
                    ReferenceNames = assembly.ReferenceNames.ToList(),

                    // The subset that was found, rather than a flag per name, so that the check can
                    // tell a reference that was absent from one that was never looked for.
                    PresentReferences = assembly.ReferenceNames
                        .Where(name => File.Exists(Path.Combine(assemblyDirectory, name + ".dll"))).ToList(),
                    Artifacts = this.CaptureArtifacts(outputPath),
                    ThreadStaticFields = threadStaticFields?.ToList() ?? new List<string>()
                };

                this.RecordedEntries.Add(entry);
                foreach (string searchDirectory in assembly.SearchDirectories)
                {
                    this.RecordedSearchDirectories.Add(NormalizeDirectory(searchDirectory));
                }

                foreach (string modulePath in assembly.ResolvedModulePaths)
                {
                    // Keyed on the normalized path, which is also what gets recorded. There is one
                    // resolver per assembly, so without this two spellings of one file become two
                    // records, and both are hashed on every check.
                    string normalizedPath = NormalizeFile(modulePath);
                    if (!this.RecordedModules.ContainsKey(normalizedPath))
                    {
                        // Modules that ship with the .NET installation are recorded by length alone.
                        // They are immutable for a given installation and their path carries the
                        // version, so hashing tens of megabytes of them on every check would cost more
                        // than the run being skipped.
                        this.RecordedModules.Add(normalizedPath,
                            CaptureFile(modulePath, !this.IsSharedFrameworkModule(normalizedPath)));
                    }
                }
            }
            catch (Exception ex)
            {
                // Recording is best-effort. Losing an entry costs a rewrite on the next run, which is
                // the safe direction, so it must not fail the current one.
                this.LogWriter.LogDebug("..... Unable to record '{0}' in the rewriting cache: {1}",
                    assembly.Name, ex.Message);
            }
        }

        /// <summary>
        /// Writes the manifest, replacing any earlier one.
        /// </summary>
        /// <remarks>
        /// Call only once the whole run has succeeded. A manifest that describes a partially rewritten
        /// directory would report assemblies as up to date that were never reached.
        /// </remarks>
        internal void Save()
        {
            string tempPath = null;
            try
            {
                // The assemblies of a run resolve one another, so most of them are both an entry and a
                // resolved module. Their entry already records them, and recording them twice means
                // hashing them twice on every check.
                var recordedByEntries = new HashSet<string>(
                    this.RecordedEntries.SelectMany(entry => new[] { entry.Input.Path, entry.Output.Path }),
                    this.PathComparer);
                var manifest = new CacheManifest()
                {
                    SchemaVersion = CurrentSchemaVersion,
                    RewriterVersion = GetRewriterVersion(),
                    RewriterModuleId = GetRewriterModuleId(),
                    AssembliesDirectory = NormalizeDirectory(this.Options.AssembliesDirectory),
                    OutputDirectory = NormalizeDirectory(this.Options.OutputDirectory),
                    ConfigurationHash = this.ConfigurationHash,
                    ResolvedModules = this.RecordedModules.Values
                        .Where(file => !recordedByEntries.Contains(file.Path))
                        .OrderBy(file => file.Path, StringComparer.Ordinal).ToList(),
                    DependencySearchDirectories = this.RecordedSearchDirectories
                        .OrderBy(path => path, StringComparer.Ordinal)
                        .Select(CaptureDirectory).ToList(),
                    Entries = this.RecordedEntries.OrderBy(e => e.Name, StringComparer.Ordinal).ToList()
                };

                string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions() { WriteIndented = true });

                // Write to a private temporary file and move it into place, so that a reader either
                // sees the previous manifest or this one, and two concurrent writers do not share a
                // scratch file -- the guid is what makes it private. A torn write is harmless in any
                // case: it fails to parse, and a manifest that fails to parse reports nothing as up
                // to date.
                tempPath = string.Format(CultureInfo.InvariantCulture, "{0}.{1}.tmp",
                    this.ManifestPath, Guid.NewGuid().ToString("N"));
                File.WriteAllText(tempPath, json);
                if (File.Exists(this.ManifestPath))
                {
                    // 'File.Move' does not take an overwrite flag on all the frameworks this assembly
                    // targets, so replace explicitly when there is something to replace.
                    File.Replace(tempPath, this.ManifestPath, null);
                }
                else
                {
                    File.Move(tempPath, this.ManifestPath);
                }

                tempPath = null;
            }
            catch (Exception ex)
            {
                this.LogWriter.LogDebug("..... Unable to write the rewriting cache: {0}", ex.Message);
            }
            finally
            {
                if (tempPath != null)
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (Exception)
                    {
                        // Nothing useful to do about a leftover scratch file.
                    }
                }
            }
        }

        /// <summary>
        /// Reads the manifest, or returns null if there is none that can be read.
        /// </summary>
        private CacheManifest ReadManifest()
        {
            if (!File.Exists(this.ManifestPath))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<CacheManifest>(File.ReadAllText(this.ManifestPath));
            }
            catch (Exception)
            {
                // A manifest that cannot be parsed is treated as absent.
                return null;
            }
        }

        /// <summary>
        /// Checks whether the specified manifest describes the run that is about to happen, and whether
        /// every file it recorded is unchanged.
        /// </summary>
        private bool IsManifestCurrent(CacheManifest manifest, out string reason)
        {
            reason = null;
            if (manifest.SchemaVersion != CurrentSchemaVersion)
            {
                reason = "it was written in an older format";
                return false;
            }

            if (manifest.RewriterVersion != GetRewriterVersion() ||
                manifest.RewriterModuleId != GetRewriterModuleId())
            {
                // The version alone is not enough: it changes rarely, so a locally rebuilt rewriter
                // carries the same one while emitting different IL.
                reason = "it was written by a different build of the rewriter";
                return false;
            }

            if (manifest.ConfigurationHash != this.ConfigurationHash)
            {
                reason = "the rewriting configuration changed";
                return false;
            }

            if (!this.PathComparer.Equals(manifest.AssembliesDirectory, NormalizeDirectory(this.Options.AssembliesDirectory)) ||
                !this.PathComparer.Equals(manifest.OutputDirectory, NormalizeDirectory(this.Options.OutputDirectory)))
            {
                // Guards against a manifest that was copied into this directory from elsewhere, which
                // the input tree copy can do when an earlier in-place run left one behind.
                reason = "it was written for a different directory";
                return false;
            }

            if (manifest.Entries is null || manifest.ResolvedModules is null ||
                manifest.DependencySearchDirectories is null)
            {
                reason = "it is incomplete";
                return false;
            }

            // Every requested assembly must be described exactly once, by an entry whose recorded paths
            // are the ones this run would use. Anything else means the manifest describes a different
            // set of work, even if the files it does name are unchanged.
            var expectedInputs = new HashSet<string>(
                this.Options.AssemblyPaths.Select(NormalizeFile), this.PathComparer);
            var seenInputs = new HashSet<string>(this.PathComparer);
            foreach (var entry in manifest.Entries)
            {
                if (entry?.Input is null || entry.Output is null || entry.ReferenceNames is null ||
                    entry.PresentReferences is null || entry.RuntimeConfig is null)
                {
                    reason = "an entry is incomplete";
                    return false;
                }

                if (!seenInputs.Add(NormalizeFile(entry.Input.Path)))
                {
                    reason = $"'{entry.Name}' is recorded more than once";
                    return false;
                }

                if (!this.PathComparer.Equals(NormalizeFile(entry.Output.Path),
                    NormalizeFile(this.GetOutputPath(entry.Input.Path))))
                {
                    reason = $"the output path of '{entry.Name}' changed";
                    return false;
                }
            }

            if (!expectedInputs.IsSubsetOf(seenInputs))
            {
                reason = "it does not cover every requested assembly";
                return false;
            }

            foreach (var entry in manifest.Entries)
            {
                if (!IsEntryCurrent(entry, out string entryReason))
                {
                    reason = entryReason;
                    return false;
                }
            }

            foreach (var module in manifest.ResolvedModules)
            {
                if (!IsFileCurrent(module))
                {
                    reason = $"the resolved assembly '{module.Path}' changed";
                    return false;
                }
            }

            foreach (var directory in manifest.DependencySearchDirectories)
            {
                if (!IsDirectoryCurrent(directory))
                {
                    reason = $"the assemblies offered by the '{directory.Path}' search directory changed";
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Checks whether everything the specified entry recorded is unchanged.
        /// </summary>
        private static bool IsEntryCurrent(CacheEntry entry, out string reason)
        {
            reason = null;
            if (!IsFileCurrent(entry.Input))
            {
                reason = $"'{entry.Name}' changed";
                return false;
            }

            if (!IsFileCurrent(entry.Output))
            {
                reason = $"the rewritten '{entry.Name}' changed";
                return false;
            }

            // Symbols are read from beside the input, because that is what decides whether they are
            // read at all, and so whether they are written. A symbol file appearing or disappearing
            // there changes what a rewrite would produce, which is one of the cases 'IsFileCurrent'
            // already answers, alongside the file having changed.
            if (!IsFileCurrent(entry.Symbols))
            {
                reason = $"the symbols of '{entry.Name}' appeared, disappeared or changed";
                return false;
            }

            if (!IsFileCurrent(entry.OutputSymbols))
            {
                reason = $"the written symbols of '{entry.Name}' changed";
                return false;
            }

            // The runtime config names the shared frameworks that resolution falls back to, so editing
            // it points the rewriter at different implementation assemblies without touching a single
            // file that anything else here records.
            if (!IsFileCurrent(entry.RuntimeConfig))
            {
                reason = $"the runtime configuration of '{entry.Name}' appeared, disappeared or changed";
                return false;
            }

            // Which assemblies get rewritten is decided by probing the input directory for each
            // reference, so a reference file appearing or disappearing changes the set even when every
            // recorded file is untouched.
            string assemblyDirectory = Path.GetDirectoryName(entry.Input.Path);
            var presentReferences = new HashSet<string>(entry.PresentReferences, StringComparer.Ordinal);
            foreach (string referenceName in entry.ReferenceNames)
            {
                string referencePath = Path.Combine(assemblyDirectory, referenceName + ".dll");
                if (File.Exists(referencePath) != presentReferences.Contains(referenceName))
                {
                    reason = $"the dependency '{referenceName}' of '{entry.Name}' appeared or disappeared";
                    return false;
                }
            }

            foreach (var artifact in entry.Artifacts ?? Enumerable.Empty<CacheFile>())
            {
                if (!IsFileCurrent(artifact))
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

        /// <summary>
        /// Returns the path the specified input assembly is written to.
        /// </summary>
        private string GetOutputPath(string inputPath) => this.Options.IsReplacingAssemblies() ?
            inputPath : Path.Combine(this.Options.OutputDirectory, Path.GetFileName(inputPath));

        /// <summary>
        /// Captures the assemblies currently on offer in the specified directory.
        /// </summary>
        /// <remarks>
        /// The recorded modules answer "did anything this run read change", but not "is there now
        /// something else it would have read instead". An assembly that appears in a searched
        /// directory can win a resolution that previously went elsewhere, or satisfy one that
        /// previously failed, and nothing else here would notice: every file the last run touched is
        /// untouched. So what is recorded is the offer rather than the outcome -- the name and size of
        /// each assembly in the directory, which changes whenever one appears, goes, or is replaced.
        ///
        /// This is taken over every directory resolution was given, not only the configured ones, so
        /// that an installed framework patch or an assembly appearing beside the rewriter counts too.
        /// It deliberately reports a change for an assembly the rewriter would never have looked at,
        /// which costs a rewrite that was not strictly needed. That is the direction this class errs
        /// in everywhere: the alternative is trusting a resolution that did not happen.
        /// </remarks>
        private static CacheDirectory CaptureDirectory(string path)
        {
            var directory = new CacheDirectory()
            {
                Path = path,
                Exists = Directory.Exists(path)
            };

            if (directory.Exists)
            {
                // Not recursive, because 'AddSearchDirectory' is not either, and by name and length
                // rather than by content, because an assembly that is both offered and read is already
                // recorded with its hash among the resolved modules.
                var builder = new StringBuilder();
                foreach (var file in new DirectoryInfo(path).GetFiles("*.dll")
                    .OrderBy(file => file.Name, StringComparer.Ordinal))
                {
                    builder.Append(file.Name).Append('|')
                        .Append(file.Length.ToString(CultureInfo.InvariantCulture)).Append('\n');
                }

                directory.ContentHash = ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
            }

            return directory;
        }

        /// <summary>
        /// Checks whether a search directory still offers what it did.
        /// </summary>
        private static bool IsDirectoryCurrent(CacheDirectory directory)
        {
            var current = CaptureDirectory(directory.Path);
            return current.Exists == directory.Exists &&
                string.Equals(current.ContentHash, directory.ContentHash, StringComparison.Ordinal);
        }

        /// <summary>
        /// Captures the debug artifacts that the current configuration produces for the specified
        /// output, recording whether each one is there rather than assuming that it is.
        /// </summary>
        private List<CacheFile> CaptureArtifacts(string outputPath)
        {
            var artifacts = new List<CacheFile>();
            if (this.Options.IsLoggingAssemblyContents)
            {
                artifacts.Add(CaptureFile(Path.ChangeExtension(outputPath, ".il.json"), false));
                artifacts.Add(CaptureFile(Path.ChangeExtension(outputPath, ".rw.json"), false));
            }

            if (this.Options.IsDiffingAssemblyContents)
            {
                artifacts.Add(CaptureFile(Path.ChangeExtension(outputPath, ".diff.json"), false));
            }

            return artifacts;
        }

        /// <summary>
        /// Captures the current state of the specified file.
        /// </summary>
        /// <param name="path">The file to capture.</param>
        /// <param name="hashContent">
        /// True to record a content hash, false to record only its length. Content is what decides
        /// whether a rewrite would produce something different, so anything feeding the rewrite is
        /// hashed. Debug artifacts are not: they do not affect the instrumentation, and the IL diff of
        /// a large assembly runs to tens of megabytes.
        /// </param>
        private static CacheFile CaptureFile(string path, bool hashContent)
        {
            var info = new FileInfo(path);
            var file = new CacheFile()
            {
                Path = NormalizeFile(path),
                Exists = info.Exists,
                Length = info.Exists ? info.Length : 0
            };

            if (info.Exists && hashContent)
            {
                file.Sha256 = ComputeFileHash(path);
            }

            return file;
        }

        /// <summary>
        /// Checks whether the specified file is exactly as it was when it was recorded, including
        /// having been absent then and now.
        /// </summary>
        private static bool IsFileCurrent(CacheFile file)
        {
            if (file is null || string.IsNullOrEmpty(file.Path))
            {
                return false;
            }

            var info = new FileInfo(file.Path);
            if (!info.Exists || !file.Exists)
            {
                return info.Exists == file.Exists;
            }

            if (info.Length != file.Length)
            {
                // Cheap rejection of the common case, a rebuilt assembly, without reading the file.
                return false;
            }

            return file.Sha256 is null || ComputeFileHash(file.Path) == file.Sha256;
        }

        /// <summary>
        /// Computes the hash of everything about this run other than the content of the files it reads.
        /// </summary>
        /// <remarks>
        /// This covers every option that changes what rewriting emits or writes, not only the ones the
        /// <see cref="AssemblySignature"/> records. The identity of the rewriter build is part of it:
        /// the product version changes rarely, so without it a locally rebuilt rewriter would be served
        /// output produced by the previous one.
        /// </remarks>
        private static string ComputeConfigurationHash(RewritingOptions options, Configuration configuration)
        {
            var builder = new StringBuilder();
            void Append(string name, object value) =>
                builder.Append(name).Append('=').Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append('\n');

            Append("rewriter-version", GetRewriterVersion());
            Append("rewriter-module", GetRewriterModuleId());
            Append("assemblies-directory", NormalizeDirectory(options.AssembliesDirectory));
            Append("output-directory", NormalizeDirectory(options.OutputDirectory));
            foreach (string path in options.AssemblyPaths.Select(NormalizeFile).OrderBy(p => p, StringComparer.Ordinal))
            {
                Append("assembly", path);
            }

            // In the order they were given, not sorted, because that is the order they are searched in
            // and the first directory holding a given assembly name is the one that wins. Two search
            // paths that both hold a 'Foo.dll' resolve to different files depending on which comes
            // first, so a hash that ignored the order would call those two runs the same.
            foreach (string path in (options.DependencySearchPaths ?? Array.Empty<string>())
                .Select(NormalizeDirectory))
            {
                Append("dependency-search-path", path);
            }

            Append("ignored-assemblies", options.GetIgnoredAssembliesPatternText());
            Append("rewrite-memory-locations", options.IsRewritingMemoryLocations);
            Append("rewrite-concurrent-collections", options.IsRewritingConcurrentCollections);
            Append("assert-data-races", options.IsDataRaceCheckingEnabled);
            Append("rewrite-dependencies", options.IsRewritingDependencies);
            Append("rewrite-unit-tests", options.IsRewritingUnitTests);
            Append("dump-il", options.IsLoggingAssemblyContents);
            Append("dump-il-diff", options.IsDiffingAssemblyContents);

            if (options.IsRewritingUnitTests && configuration != null)
            {
                // Rewriting a unit test bakes these into the generated test body, so a change to any of
                // them changes the emitted IL. They are only read under this option, so that unrelated
                // configuration does not invalidate the cache.
                Append("test-iterations", configuration.TestingIterations);
                Append("test-max-unfair-steps", configuration.MaxUnfairSchedulingSteps);
                Append("test-max-fair-steps", configuration.MaxFairSchedulingSteps);
                Append("test-strategy", configuration.ExplorationStrategy);
                Append("test-strategy-bound", configuration.StrategyBound);
                Append("test-liveness-threshold", configuration.LivenessTemperatureThreshold);
                Append("test-explicit-liveness-threshold", configuration.UserExplicitlySetLivenessTemperatureThreshold);
                Append("test-timeout-delay", configuration.TimeoutDelay);
                Append("test-seed", configuration.RandomGeneratorSeed);
                Append("test-verbosity", configuration.VerbosityLevel);
                Append("test-telemetry", configuration.IsTelemetryEnabled);
                Append("test-attach-debugger", configuration.AttachDebugger);
            }

            return ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        }

        /// <summary>
        /// Checks whether the specified normalized module path belongs to a shared .NET framework
        /// directory.
        /// </summary>
        private bool IsSharedFrameworkModule(string normalizedPath) =>
            SharedFrameworkPrefix.Value != null &&
            normalizedPath.StartsWith(SharedFrameworkPrefix.Value, this.PathComparison);

        /// <summary>
        /// Returns the version of the assembly rewriter.
        /// </summary>
        private static string GetRewriterVersion() =>
            RewritingEngine.GetAssemblyRewriterVersion().ToString();

        /// <summary>
        /// Returns the identity of this particular build of the assembly rewriter.
        /// </summary>
        private static string GetRewriterModuleId() =>
            RewritingEngine.GetAssemblyRewriterModuleId();

        /// <summary>
        /// Returns the full path of the specified file, so that the same file is recorded the same way
        /// however it was spelled on the command line.
        /// </summary>
        private static string NormalizeFile(string path) =>
            string.IsNullOrEmpty(path) ? string.Empty : Path.GetFullPath(path);

        /// <summary>
        /// What a file system is assumed to do with case when it cannot be asked.
        /// </summary>
        private static readonly bool AssumedCaseInsensitive = !RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        /// <summary>
        /// The answer already found for a directory, so that the probe runs once per location.
        /// </summary>
        private static readonly Dictionary<string, bool> ProbedDirectories =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Returns a comparer that treats two paths under the specified directory as the file system
        /// holding it does.
        /// </summary>
        /// <remarks>
        /// <see cref="Path.GetFullPath(string)"/> resolves relative segments and separators, but never
        /// reaches the file system for the name itself, so it does not canonicalize case. A path that
        /// arrives through a configuration file or an assembly reference keeps whatever case it was
        /// spelled with, while one that arrives through a directory enumeration carries the case on
        /// disk. Where those two spellings name one file, comparing them ordinally would miss, and the
        /// run would decide a rewritten output is not protected and copy the original over it. Where
        /// they name two files, folding case would do the opposite and protect an output that nothing
        /// rewrote.
        ///
        /// Which of those holds is a property of the file system, not of the operating system: macOS
        /// ships case-insensitive but can be formatted case-sensitive, and Windows can be told to treat
        /// a directory case-sensitively. So it is asked rather than assumed, by looking for the
        /// directory under a name whose case has been flipped. Only when there is nothing to ask --
        /// no such directory, no letters in its name, an error -- does this fall back to what the
        /// platform usually does.
        /// </remarks>
        internal static StringComparer GetPathComparer(string directory) =>
            IsCaseInsensitive(directory) ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        /// <summary>
        /// Returns the <see cref="StringComparison"/> that matches <see cref="GetPathComparer"/>.
        /// </summary>
        private static StringComparison GetPathComparison(string directory) =>
            IsCaseInsensitive(directory) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        /// <summary>
        /// Checks whether the file system holding the specified directory ignores case.
        /// </summary>
        private static bool IsCaseInsensitive(string directory)
        {
            try
            {
                // The directory itself need not exist yet -- the output directory is created after
                // this -- but an ancestor of it does, and it is on the same file system.
                var info = new DirectoryInfo(Path.GetFullPath(directory));
                while (info != null && !info.Exists)
                {
                    info = info.Parent;
                }

                if (info?.Parent is null)
                {
                    return AssumedCaseInsensitive;
                }

                string flipped = FlipCase(info.Name);
                if (string.Equals(flipped, info.Name, StringComparison.Ordinal))
                {
                    return AssumedCaseInsensitive;
                }

                string probe = Path.Combine(info.Parent.FullName, flipped);
                lock (ProbedDirectories)
                {
                    if (!ProbedDirectories.TryGetValue(probe, out bool isCaseInsensitive))
                    {
                        isCaseInsensitive = Directory.Exists(probe);
                        ProbedDirectories.Add(probe, isCaseInsensitive);
                    }

                    return isCaseInsensitive;
                }
            }
            catch (Exception)
            {
                return AssumedCaseInsensitive;
            }
        }

        /// <summary>
        /// Returns the specified name with the case of every letter in it inverted.
        /// </summary>
        private static string FlipCase(string name)
        {
            var builder = new StringBuilder(name.Length);
            foreach (char character in name)
            {
                builder.Append(char.IsUpper(character) ? char.ToLowerInvariant(character) :
                    char.ToUpperInvariant(character));
            }

            return builder.ToString();
        }

        /// <summary>
        /// Returns the full path of the specified directory, without a trailing separator.
        /// </summary>
        private static string NormalizeDirectory(string path) =>
            string.IsNullOrEmpty(path) ? string.Empty :
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        /// <summary>
        /// Computes the SHA256 hash of the specified file.
        /// </summary>
        private static string ComputeFileHash(string path)
        {
            // Unbuffered and sequential: 'ComputeHash' reads the stream in chunks of its own, so a
            // buffering 'FileStream' would copy every byte of every assembly a second time.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 1, FileOptions.SequentialScan);
            using var algorithm = SHA256.Create();
            return ToHexString(algorithm.ComputeHash(stream));
        }

        /// <summary>
        /// Computes the SHA256 hash of the specified data.
        /// </summary>
        private static string ComputeHash(byte[] data)
        {
            using var algorithm = SHA256.Create();
            return ToHexString(algorithm.ComputeHash(data));
        }

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

        /// <summary>
        /// The recorded state of a rewriting run.
        /// </summary>
        internal sealed class CacheManifest
        {
            public int SchemaVersion { get; set; }

            public string RewriterVersion { get; set; }

            public string RewriterModuleId { get; set; }

            public string AssembliesDirectory { get; set; }

            public string OutputDirectory { get; set; }

            public string ConfigurationHash { get; set; }

            public List<CacheFile> ResolvedModules { get; set; }

            public List<CacheDirectory> DependencySearchDirectories { get; set; }

            public List<CacheEntry> Entries { get; set; }
        }

        /// <summary>
        /// The recorded state of one directory that resolution searches.
        /// </summary>
        internal sealed class CacheDirectory
        {
            public string Path { get; set; }

            public bool Exists { get; set; }

            public string ContentHash { get; set; }
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

            public string Sha256 { get; set; }
        }
    }
}

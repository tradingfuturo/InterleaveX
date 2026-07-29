// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Coyote.IO;
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
    ///
    /// The deciding itself lives in <see cref="RewritingCacheValidator"/>. What is left here is the
    /// recording: reading and writing the manifest, and gathering what a run consumed and produced.
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
        private const int CurrentSchemaVersion = 4;

        /// <summary>
        /// The rewriting options of the current run.
        /// </summary>
        private readonly RewritingOptions Options;

        /// <summary>
        /// Responsible for writing to the installed <see cref="ILogger"/>.
        /// </summary>
        private readonly LogWriter LogWriter;

        /// <summary>
        /// The file system this run reads and writes.
        /// </summary>
        private readonly IFileSystem FileSystem;

        /// <summary>
        /// Decides whether a recorded run still describes what is on disk.
        /// </summary>
        private readonly RewritingCacheValidator Validator;

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
        /// The complete set of assemblies loaded for this run, including discovered dependencies.
        /// </summary>
        private HashSet<string> ExpectedRewriteInputs;

        /// <summary>
        /// True if any assembly could not be recorded atomically.
        /// </summary>
        private bool HasRecordingFailure;

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
        internal RewritingCache(RewritingOptions options, Configuration configuration, LogWriter logWriter,
            IFileSystem fileSystem)
        {
            this.Options = options;
            this.LogWriter = logWriter;
            this.FileSystem = fileSystem;
            this.ManifestPath = Path.Combine(options.OutputDirectory, ManifestFileName);
            this.ConfigurationHash = ComputeConfigurationHash(options, configuration);

            this.Validator = new RewritingCacheValidator(fileSystem, new RewritingCacheExpectation(
                CurrentSchemaVersion, GetRewriterVersion(), GetRewriterModuleId(), this.ConfigurationHash,
                options.AssembliesDirectory, options.OutputDirectory, options.AssemblyPaths,
                options.IsReplacingAssemblies(), options.IsLoggingAssemblyContents,
                options.IsDiffingAssemblyContents));

            this.RecordedEntries = new List<CacheEntry>();
            this.RecordedModules = new Dictionary<string, CacheFile>(this.Validator.PathComparer);
            this.RecordedSearchDirectories = new HashSet<string>(this.Validator.PathComparer);
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
            protectedOutputPaths = new HashSet<string>(this.Validator.PathComparer);
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

                if (!this.Validator.IsManifestCurrent(manifest, out string reason))
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
                    // none. Their content fingerprints are confirmed with the rest of the manifest.
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
        /// Revalidates the manifest accepted before mirroring, closing the interval in which an input
        /// could change after it was fingerprinted while its rewritten output remained protected.
        /// </summary>
        internal bool TryConfirmUpToDateRun()
        {
            if (this.AcceptedManifest is null)
            {
                return false;
            }

            try
            {
                if (this.Validator.IsManifestCurrent(this.AcceptedManifest, out string reason))
                {
                    return true;
                }

                this.LogWriter.LogDebug("..... Rewriting cache changed while mirroring: {0}", reason);
            }
            catch (Exception ex)
            {
                this.LogWriter.LogDebug("..... Unable to confirm the rewriting cache: {0}", ex.Message);
            }

            this.AcceptedManifest = null;
            return false;
        }

        /// <summary>
        /// Removes any previous manifest before output is changed by a cache miss.
        /// </summary>
        internal void Invalidate()
        {
            this.AcceptedManifest = null;
            this.FileSystem.DeleteFile(this.ManifestPath);
        }

        /// <summary>
        /// Registers the exact closure that this run must record before a manifest may be written.
        /// </summary>
        internal void RegisterRewriteInputs(IEnumerable<AssemblyInfo> assemblies)
        {
            this.ExpectedRewriteInputs = new HashSet<string>(
                assemblies.Select(assembly => RewritingCacheValidator.NormalizeFile(assembly.FilePath)),
                this.Validator.PathComparer);
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
        /// Returns the generated files proven current by the accepted manifest.
        /// </summary>
        internal IEnumerable<string> GetAcceptedProducedPaths()
        {
            if (this.AcceptedManifest is null)
            {
                yield break;
            }

            foreach (var entry in this.AcceptedManifest.Entries)
            {
                if (entry.Output?.Exists is true)
                {
                    yield return entry.Output.Path;
                }

                if (entry.OutputSymbols?.Exists is true)
                {
                    yield return entry.OutputSymbols.Path;
                }

                foreach (var artifact in entry.Artifacts ?? Enumerable.Empty<CacheFile>())
                {
                    if (artifact?.Exists is true)
                    {
                        yield return artifact.Path;
                    }
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
                var capturedFiles = new Dictionary<string, CacheFile>(this.Validator.PathComparer);
                CacheFile Capture(string path)
                {
                    string normalizedPath = RewritingCacheValidator.NormalizeFile(path);
                    if (!capturedFiles.TryGetValue(normalizedPath, out CacheFile captured))
                    {
                        captured = this.Validator.CaptureFile(normalizedPath, true);
                        capturedFiles.Add(normalizedPath, captured);
                    }

                    return captured;
                }

                string assemblyDirectory = Path.GetDirectoryName(assembly.FilePath);
                var entry = new CacheEntry()
                {
                    Name = assembly.Name,
                    Input = Capture(assembly.FilePath),
                    Output = Capture(outputPath),

                    // Read from beside the input, which is what gates reading symbols and therefore
                    // writing them: recording the produced one instead would miss a symbol file
                    // appearing next to the input, which changes the output. Captured whether or not
                    // it is there, because a 'CacheFile' records absence as faithfully as content.
                    Symbols = Capture(Path.ChangeExtension(assembly.FilePath, "pdb")),
                    OutputSymbols = Capture(Path.ChangeExtension(outputPath, "pdb")),

                    // Which shared frameworks resolution falls back to is read from here, so its
                    // content decides what the rewriter can see just as the assemblies themselves do.
                    // The expression is the one 'AssemblyInfo.GetFrameworksFromRuntimeConfig' uses, so
                    // that this records the file that was actually read.
                    RuntimeConfig = Capture(
                        Path.ChangeExtension(assembly.FilePath, ".runtimeconfig.json")),
                    ReferenceNames = assembly.ReferenceNames.ToList(),

                    // The subset that was found, rather than a flag per name, so that the check can
                    // tell a reference that was absent from one that was never looked for.
                    PresentReferences = assembly.ReferenceNames
                        .Where(name => this.FileSystem.FileExists(
                            Path.Combine(assemblyDirectory, name + ".dll"))).ToList(),
                    Artifacts = this.CaptureArtifacts(outputPath, Capture),
                    ThreadStaticFields = threadStaticFields?.ToList() ?? new List<string>()
                };

                var searchDirectories = assembly.SearchDirectories
                    .Select(RewritingCacheValidator.NormalizeDirectory).ToArray();
                var modules = new Dictionary<string, CacheFile>(this.Validator.PathComparer);
                foreach (string modulePath in assembly.ResolvedModulePaths)
                {
                    string normalizedPath = RewritingCacheValidator.NormalizeFile(modulePath);
                    if ((this.ExpectedRewriteInputs is null ||
                        !this.ExpectedRewriteInputs.Contains(normalizedPath)) &&
                        !this.RecordedModules.ContainsKey(normalizedPath) &&
                        !modules.ContainsKey(normalizedPath))
                    {
                        modules.Add(normalizedPath, Capture(modulePath));
                    }
                }

                // Nothing above mutates shared recording state. Commit only after every capture for
                // this assembly succeeded, so a failure cannot leave a plausible partial entry.
                this.RecordedEntries.Add(entry);
                foreach (string searchDirectory in searchDirectories)
                {
                    this.RecordedSearchDirectories.Add(searchDirectory);
                }

                foreach (var module in modules)
                {
                    this.RecordedModules.Add(module.Key, module.Value);
                }
            }
            catch (Exception ex)
            {
                this.HasRecordingFailure = true;
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
            var recordedInputs = new HashSet<string>(
                this.RecordedEntries.Select(entry => RewritingCacheValidator.NormalizeFile(entry.Input.Path)),
                this.Validator.PathComparer);
            if (this.HasRecordingFailure || this.ExpectedRewriteInputs is null ||
                !recordedInputs.SetEquals(this.ExpectedRewriteInputs))
            {
                this.LogWriter.LogDebug("..... Not writing an incomplete rewriting cache.");
                this.Invalidate();
                return;
            }

            string tempPath = null;
            try
            {
                // The assemblies of a run resolve one another, so most of them are both an entry and a
                // resolved module. Their entry already records them, and recording them twice means
                // hashing them twice on every check.
                var recordedByEntries = new HashSet<string>(
                    this.RecordedEntries.SelectMany(entry => new[] { entry.Input.Path, entry.Output.Path }),
                    this.Validator.PathComparer);
                var manifest = new CacheManifest()
                {
                    SchemaVersion = CurrentSchemaVersion,
                    FingerprintAlgorithm = RewritingCacheValidator.FingerprintAlgorithm,
                    RewriterVersion = GetRewriterVersion(),
                    RewriterModuleId = GetRewriterModuleId(),
                    AssembliesDirectory = RewritingCacheValidator.NormalizeDirectory(this.Options.AssembliesDirectory),
                    OutputDirectory = RewritingCacheValidator.NormalizeDirectory(this.Options.OutputDirectory),
                    ConfigurationHash = this.ConfigurationHash,
                    RequestedInputs = this.Options.AssemblyPaths
                        .Select(RewritingCacheValidator.NormalizeFile)
                        .Distinct(this.Validator.PathComparer)
                        .OrderBy(path => path, StringComparer.Ordinal).ToList(),
                    RewriteInputs = this.ExpectedRewriteInputs
                        .OrderBy(path => path, StringComparer.Ordinal).ToList(),
                    ResolvedModules = this.RecordedModules.Values
                        .Where(file => !recordedByEntries.Contains(file.Path))
                        .OrderBy(file => file.Path, StringComparer.Ordinal).ToList(),
                    DependencySearchDirectories = this.RecordedSearchDirectories
                        .OrderBy(path => path, StringComparer.Ordinal)
                        .Select(path => this.Validator.CaptureDirectory(path, true)).ToList(),
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
                this.FileSystem.WriteAllText(tempPath, json);
                if (this.FileSystem.FileExists(this.ManifestPath))
                {
                    // 'File.Move' does not take an overwrite flag on all the frameworks this assembly
                    // targets, so replace explicitly when there is something to replace.
                    this.FileSystem.ReplaceFile(tempPath, this.ManifestPath, null);
                }
                else
                {
                    this.FileSystem.MoveFile(tempPath, this.ManifestPath);
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
                        this.FileSystem.DeleteFile(tempPath);
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
            if (!this.FileSystem.FileExists(this.ManifestPath))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<CacheManifest>(this.FileSystem.ReadAllText(this.ManifestPath));
            }
            catch (Exception)
            {
                // A manifest that cannot be parsed is treated as absent.
                return null;
            }
        }

        /// <summary>
        /// Captures the debug artifacts that the current configuration produces for the specified
        /// output, recording whether each one is there rather than assuming that it is.
        /// </summary>
        private List<CacheFile> CaptureArtifacts(string outputPath, Func<string, CacheFile> capture)
        {
            var artifacts = new List<CacheFile>();
            if (this.Options.IsLoggingAssemblyContents)
            {
                artifacts.Add(capture(Path.ChangeExtension(outputPath, ".il.json")));
                artifacts.Add(capture(Path.ChangeExtension(outputPath, ".rw.json")));
            }

            if (this.Options.IsDiffingAssemblyContents)
            {
                artifacts.Add(capture(Path.ChangeExtension(outputPath, ".diff.json")));
            }

            return artifacts;
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
            Append("assemblies-directory", RewritingCacheValidator.NormalizeDirectory(options.AssembliesDirectory));
            Append("output-directory", RewritingCacheValidator.NormalizeDirectory(options.OutputDirectory));
            foreach (string path in options.AssemblyPaths.Select(RewritingCacheValidator.NormalizeFile)
                .OrderBy(p => p, StringComparer.Ordinal))
            {
                Append("assembly", path);
            }

            // In the order they were given, not sorted, because that is the order they are searched in
            // and the first directory holding a given assembly name is the one that wins. Two search
            // paths that both hold a 'Foo.dll' resolve to different files depending on which comes
            // first, so a hash that ignored the order would call those two runs the same.
            foreach (string path in (options.DependencySearchPaths ?? Array.Empty<string>())
                .Select(RewritingCacheValidator.NormalizeDirectory))
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

            return RewritingCacheValidator.ComputeSha256(Encoding.UTF8.GetBytes(builder.ToString()));
        }

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
    }
}

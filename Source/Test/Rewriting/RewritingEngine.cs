// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Coyote.IO;
using Microsoft.Coyote.Logging;
using Microsoft.Coyote.Runtime;
using Mono.Cecil;

namespace Microsoft.Coyote.Rewriting
{
    /// <summary>
    /// Engine that can rewrite a set of assemblies for systematic testing.
    /// </summary>
    /// <remarks>
    /// See <see href="/coyote/get-started/rewriting">rewriting</see> for more information.
    /// </remarks>
    public class RewritingEngine
    {
        /// <summary>
        /// Temporary directory that is used to write the rewritten assemblies
        /// in the case that they are replacing the original ones.
        /// </summary>
        /// <remarks>
        /// We need this because it seems Mono.Cecil does not allow to rewrite in-place.
        /// </remarks>
        private const string TempDirectory = "__temp_coyote__";

        /// <summary>
        /// Options for rewriting assemblies.
        /// </summary>
        private readonly RewritingOptions Options;

        /// <summary>
        /// The test configuration to use when rewriting unit tests.
        /// </summary>
        private readonly Configuration Configuration;

        /// <summary>
        /// List of passes to invoke while rewriting IL.
        /// </summary>
        private readonly LinkedList<Pass> Passes;

        /// <summary>
        /// The pass holding the IL as it was before any rewriting, else null if the configuration does
        /// not ask for the IL to be captured.
        /// </summary>
        /// <remarks>
        /// <see cref="InitializePasses"/> installs an <see cref="AssemblyDiffingPass"/> at each end of
        /// <see cref="Passes"/>, so the first one runs before every rewriting pass and the last one
        /// after. Naming that convention here keeps the three readers of it from spelling it out --
        /// and from disagreeing about it -- each in their own way.
        /// </remarks>
        private AssemblyDiffingPass OriginalDiffingPass => this.Passes.First?.Value as AssemblyDiffingPass;

        /// <summary>
        /// The pass holding the IL as it is after rewriting, else null if the configuration does not
        /// ask for the IL to be captured.
        /// </summary>
        private AssemblyDiffingPass RewrittenDiffingPass => this.Passes.Last?.Value as AssemblyDiffingPass;

        /// <summary>
        /// Reports thread-static state in each assembly. Kept apart from <see cref="Passes"/> because it
        /// is an <see cref="AnalysisPass"/> rather than a rewriting one, and so must also run for
        /// assemblies that are already rewritten.
        /// </summary>
        private ThreadStaticDetectionPass ThreadStaticDetection;

        /// <summary>
        /// Simple cache to reduce redundant warnings.
        /// </summary>
        private readonly HashSet<string> ResolveWarnings;

        /// <summary>
        /// Responsible for writing to the installed <see cref="ILogger"/>.
        /// </summary>
        private readonly LogWriter LogWriter;

        /// <summary>
        /// The installed profiler.
        /// </summary>
        private readonly Profiler Profiler;

        /// <summary>
        /// Cached list of .NET shared framework directories for fallback assembly resolution.
        /// </summary>
        private List<string> CachedFrameworkDirectories;

        /// <summary>
        /// The shared framework directories the fallback resolution enumerated.
        /// </summary>
        /// <remarks>
        /// The fallback probes every installed shared framework, which is a candidate space no
        /// assembly's runtime configuration describes and which nothing else would record. An
        /// assembly appearing in any of them satisfies a reference that resolution previously gave up
        /// on, so what is recorded is which directories exist rather than which one answered.
        ///
        /// Gathered on the engine rather than handed to the cache as it is discovered, because the
        /// fallback first runs while the assemblies are still being loaded, which is before the run
        /// has a closure for the cache to attribute anything to.
        /// </remarks>
        private readonly List<CacheDirectoryListing> FallbackFrameworkInventories;

        /// <summary>
        /// The file system this run reads and writes, other than through Mono.Cecil.
        /// </summary>
        private readonly IFileSystem FileSystem;

        /// <summary>
        /// Reads an environment variable.
        /// </summary>
        private readonly Func<string, string> GetEnvironmentVariable;

        private readonly Action<IReadOnlyList<AssemblyInfo>> OnAssembliesLoaded;

        /// <summary>
        /// The .NET installation selected once for this run and shared by cache admission and resolution.
        /// </summary>
        internal readonly string EffectiveDotnetRoot;

        /// <summary>
        /// Copies the input directory into the output one, leaving up-to-date outputs alone.
        /// </summary>
        private readonly RewritingOutputMirror Mirror;

        /// <summary>
        /// Ownership of files in a separate output directory, else null for in-place rewriting.
        /// </summary>
        private RewritingOutputLedger OutputLedger;

        private RewritingCache CurrentCache;

        private Dictionary<string, MirroredFile> MirroredOutputFiles;

        private HashSet<string> ProducedOutputFiles;

        /// <summary>
        /// Every relative path any attempt of this run has put in the output directory.
        /// </summary>
        /// <remarks>
        /// The mirror is retried when the input tree moves underneath it, and the attempt that failed
        /// has already copied some of it. Those files belong to no run otherwise: the ledger owns what
        /// the previous run recorded and what this one commits, and a file copied from a source that
        /// then vanished is in neither. Accumulated across attempts rather than reset with each one,
        /// which is what makes the retry able to clean up after the attempt before it.
        /// </remarks>
        private HashSet<string> AttemptedMirroredFiles;

        /// <summary>
        /// Durable rollback state for every mutation of the selected output.
        /// </summary>
        private RewritingOutputChangeJournal OutputJournal;

        private sealed class StagedOutput
        {
            internal string SourcePath;

            internal string TargetPath;
        }

        private sealed class PendingCacheRecord
        {
            internal AssemblyInfo Assembly;

            internal string OutputPath;

            internal string[] ThreadStaticFields;
        }

        private readonly List<StagedOutput> StagedOutputs = new List<StagedOutput>();

        private readonly List<string> PublishedStagedOutputPaths = new List<string>();

        private readonly List<PendingCacheRecord> PendingCacheRecords = new List<PendingCacheRecord>();

        /// <summary>
        /// Initializes a new instance of the <see cref="RewritingEngine"/> class.
        /// </summary>
        internal RewritingEngine(RewritingOptions options, Configuration configuration, LogWriter logWriter,
            Profiler profiler, IFileSystem fileSystem, Func<string, string> getEnvironmentVariable,
            Action<IReadOnlyList<AssemblyInfo>> onAssembliesLoaded = null)
        {
            this.Options = options.Sanitize();
            this.Configuration = configuration;
            this.Passes = new LinkedList<Pass>();
            this.ResolveWarnings = new HashSet<string>();
            this.FallbackFrameworkInventories = new List<CacheDirectoryListing>();
            this.LogWriter = logWriter;
            this.Profiler = profiler;
            this.FileSystem = fileSystem;
            this.GetEnvironmentVariable = getEnvironmentVariable;
            this.OnAssembliesLoaded = onAssembliesLoaded;
            this.EffectiveDotnetRoot = AssemblyInfo.GetDotnetRoot(fileSystem, getEnvironmentVariable);
            this.Mirror = new RewritingOutputMirror(fileSystem, logWriter);
        }

        /// <summary>
        /// Runs the engine using the specified rewriting options.
        /// </summary>
        internal static void Run(RewritingOptions options, Configuration configuration, LogWriter logWriter, Profiler profiler) =>
            Run(options, configuration, logWriter, profiler, HostFileSystem.Instance, Environment.GetEnvironmentVariable);

        /// <summary>
        /// Runs the engine over the specified file system and environment.
        /// </summary>
        /// <remarks>
        /// Reading and writing the assemblies themselves stays on real paths whatever is passed here:
        /// Mono.Cecil reaches the disk through its own resolver, and a module read from a stream has
        /// no file name for the cache to record. What this reaches is everything around that.
        /// </remarks>
        internal static void Run(RewritingOptions options, Configuration configuration, LogWriter logWriter,
            Profiler profiler, IFileSystem fileSystem, Func<string, string> getEnvironmentVariable,
            Action<IReadOnlyList<AssemblyInfo>> onAssembliesLoaded = null)
        {
            var engine = new RewritingEngine(options, configuration, logWriter, profiler,
                fileSystem, getEnvironmentVariable, onAssembliesLoaded);
            engine.Run();
        }

        /// <summary>
        /// Runs the rewriting engine.
        /// </summary>
        private void Run()
        {
            string outputDirectory = null;
            RewritingInputSnapshot snapshot = null;
            RewritingOutputLock outputLock = null;
            bool isProfilerStarted = false;
            try
            {
                this.Profiler.StartMeasuringExecutionTime();
                isProfilerStarted = true;

                outputLock = RewritingOutputLock.Acquire(
                    this.Options.OutputDirectory, TimeSpan.FromSeconds(60));
                RewritingOutputChangeJournal.RecoverAll(this.FileSystem, this.Options.OutputDirectory);
                this.OutputJournal = new RewritingOutputChangeJournal(
                    this.FileSystem, this.Options.OutputDirectory);

                var cache = this.CreateCache();
                this.CurrentCache = cache;
                if (!this.Options.IsReplacingAssemblies())
                {
                    this.OutputLedger = new RewritingOutputLedger(this.FileSystem, this.LogWriter,
                        this.Options.AssembliesDirectory, this.Options.OutputDirectory);
                    var comparer = this.FileSystem.IsCaseInsensitive(this.Options.OutputDirectory) ?
                        StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                    this.MirroredOutputFiles = new Dictionary<string, MirroredFile>(comparer);
                    this.ProducedOutputFiles = new HashSet<string>(comparer);
                    this.AttemptedMirroredFiles = new HashSet<string>(comparer);
                }

                bool isUpToDate = cache.TryGetUpToDateRun(out HashSet<string> protectedOutputPaths);
                var snapshotExcludedDirectories = new List<string>()
                {
                    this.OutputJournal.BackupDirectory
                };
                var snapshotExcludedFiles = new List<string>() { outputLock.Path };
                if (this.Options.IsReplacingAssemblies())
                {
                    snapshotExcludedDirectories.Add(Path.Combine(
                        this.Options.OutputDirectory, TempDirectory));
                    snapshotExcludedFiles.Add(Path.Combine(
                        this.Options.OutputDirectory, RewritingCache.ManifestFileName));
                }

                snapshot = RewritingInputSnapshot.Create(this.FileSystem, this.LogWriter,
                    this.Options.AssembliesDirectory, this.Options.OutputDirectory,
                    excludedDirectories: snapshotExcludedDirectories,
                    excludedFiles: snapshotExcludedFiles);
                if (!isUpToDate)
                {
                    this.OutputJournal.Capture(Path.Combine(
                        this.Options.OutputDirectory, RewritingCache.ManifestFileName));
                    cache.Invalidate();
                }

                outputDirectory = this.CreateOutputDirectoryAndCopyFiles(
                    protectedOutputPaths, snapshot.SnapshotDirectory);

                if (isUpToDate && cache.TryConfirmUpToDateRun())
                {
                    // The findings of the analysis passes hold whether or not anything was rewritten,
                    // so they are replayed rather than lost.
                    cache.ReplayDiagnostics();
                    foreach (string path in cache.GetAcceptedProducedPaths())
                    {
                        this.TrackProducedOutput(path);
                    }

                    if (this.Options.IsReplacingAssemblies())
                    {
                        snapshot.VerifyUnchanged();
                        this.PublishStagedOutputs();
                    }

                    this.OutputJournal.Capture(Path.Combine(
                        this.Options.OutputDirectory, RewritingOutputLedger.ManifestFileName));
                    if (!this.Options.IsReplacingAssemblies())
                    {
                        snapshot.VerifyUnchanged();
                    }

                    this.OutputLedger?.Commit(this.MirroredOutputFiles.Keys, this.ProducedOutputFiles,
                        this.AttemptedMirroredFiles, this.OutputJournal.Capture);
                    this.OutputJournal.Complete();
                    this.LogWriter.LogImportant("... Skipping rewriting as every assembly is up to date");
                    return;
                }

                if (isUpToDate)
                {
                    // Something changed after the first validation. The protected mirror deliberately
                    // left original inputs out of the output, so run it again without protection
                    // before rewriting from the now-current source tree.
                    this.OutputJournal.Capture(Path.Combine(
                        this.Options.OutputDirectory, RewritingCache.ManifestFileName));
                    cache.Invalidate();
                    protectedOutputPaths.Clear();
                    outputDirectory = this.CreateOutputDirectoryAndCopyFiles(
                        protectedOutputPaths, snapshot.SnapshotDirectory);
                }

                // Get the set of assemblies to rewrite.
                var assemblies = AssemblyInfo.LoadAssembliesToRewrite(this.Options,
                    this.OnResolveAssemblyFailure, this.FileSystem, this.GetEnvironmentVariable,
                    this.EffectiveDotnetRoot, snapshot.ToReadPath, snapshot.ToLogicalPath).ToList();
                this.RewriteAssemblyBatch(assemblies, outputDirectory, cache);

                if (this.Options.IsReplacingAssemblies())
                {
                    snapshot.VerifyUnchanged();
                    this.PublishStagedOutputs();
                    foreach (PendingCacheRecord record in this.PendingCacheRecords)
                    {
                        cache.RecordAssembly(
                            record.Assembly, record.OutputPath, record.ThreadStaticFields,
                            this.PublishedStagedOutputPaths);
                    }
                }

                // After the passes, because the fallback resolution that fills this runs throughout
                // them and not only while the assemblies are being loaded.
                cache.RecordFrameworkInventories(this.FallbackFrameworkInventories);

                // Only once every assembly has been dealt with: a manifest describing a partially
                // rewritten directory would report assemblies as up to date that were never reached.
                this.OutputJournal.Capture(Path.Combine(
                    this.Options.OutputDirectory, RewritingCache.ManifestFileName));
                if (!this.Options.IsReplacingAssemblies())
                {
                    snapshot.VerifyUnchanged();
                }

                cache.Save();
                this.OutputJournal.Capture(Path.Combine(
                    this.Options.OutputDirectory, RewritingOutputLedger.ManifestFileName));
                if (!this.Options.IsReplacingAssemblies())
                {
                    snapshot.VerifyUnchanged();
                }

                this.OutputLedger?.Commit(this.MirroredOutputFiles.Keys, this.ProducedOutputFiles,
                    this.AttemptedMirroredFiles, this.OutputJournal.Capture);
                this.OutputJournal.Complete();
            }
            catch (Exception ex)
            {
                if (this.OutputJournal != null)
                {
                    try
                    {
                        if (this.Options.IsReplacingAssemblies() &&
                            !string.IsNullOrEmpty(outputDirectory) &&
                            this.FileSystem.DirectoryExists(outputDirectory))
                        {
                            // Staging is not published output and can contain files that were copied
                            // without journal entries. Remove it before the journal removes its captured
                            // directory skeleton.
                            this.FileSystem.DeleteDirectory(outputDirectory, true);
                        }

                        this.OutputJournal.Restore();
                        this.OutputJournal.Complete();
                    }
                    catch (Exception restoreError)
                    {
                        throw new IOException(
                            $"Rewriting failed and output rollback also failed. Recovery remains at " +
                            $"'{this.OutputJournal.BackupDirectory}'.",
                            new AggregateException(ex, restoreError));
                    }
                }

                ExceptionDispatchInfo.Capture(ex).Throw();
            }
            finally
            {
                try
                {
                    snapshot?.Dispose();
                }
                finally
                {
                    try
                    {
                        if (this.Options.IsReplacingAssemblies() && !string.IsNullOrEmpty(outputDirectory) &&
                            this.FileSystem.DirectoryExists(outputDirectory))
                        {
                            // If we are replacing the original assemblies, then delete the temporary output directory.
                            this.FileSystem.DeleteDirectory(outputDirectory, true);
                        }
                    }
                    finally
                    {
                        try
                        {
                            if (isProfilerStarted)
                            {
                                this.Profiler.StopMeasuringExecutionTime();
                            }
                        }
                        finally
                        {
                            outputLock?.Dispose();
                        }
                    }
                }

            }
        }

        /// <summary>
        /// Creates the cache using the same frozen runtime root as resolution.
        /// </summary>
        internal RewritingCache CreateCache() => new RewritingCache(
            this.Options, this.Configuration, this.LogWriter, this.FileSystem, this.EffectiveDotnetRoot);

        /// <summary>
        /// Initializes the passes to invoke during rewriting.
        /// </summary>
        private void InitializePasses(IEnumerable<AssemblyInfo> assemblies)
        {
            this.ThreadStaticDetection = new ThreadStaticDetectionPass(assemblies, this.LogWriter);

            // Add the default type rewriting passes. We must first rewrite member types,
            // such as fields and method signatures, before we can rewrite the method bodies.
            this.Passes.AddFirst(new MemberTypeRewritingPass(this.Options, assemblies, this.LogWriter));
            this.Passes.AddLast(new MethodBodyTypeRewritingPass(this.Options, assemblies, this.LogWriter));

            // Add a pass that injects callbacks to the runtime for extracting call-site information.
            this.Passes.AddLast(new CallSiteExtractionRewritingPass(assemblies, this.LogWriter));

            if (this.Options.IsRewritingUnitTests)
            {
                // We are running this pass last, as we are rewriting the original method, and
                // we need the other rewriting passes to happen before this pass.
                this.Passes.AddLast(new MSTestRewritingPass(this.Configuration, assemblies, this.LogWriter));
            }

            if (this.Options.IsRewritingMemoryLocations)
            {
                // Add a pass that rewrites memory-access locations for checking fine-grained races.
                this.Passes.AddLast(new MemoryAccessRewritingPass(assemblies, this.LogWriter));
            }

            this.Passes.AddLast(new InterAssemblyInvocationRewritingPass(assemblies, this.LogWriter));
            this.Passes.AddLast(new UncontrolledInvocationRewritingPass(assemblies, this.LogWriter));

            // Add a pass that rewrites exception handlers to make sure that any exceptions
            // used internally by the runtime are not consumed by the user code.
            this.Passes.AddLast(new ExceptionFilterRewritingPass(assemblies, this.LogWriter));

            if (this.Options.IsLoggingAssemblyContents || this.Options.IsDiffingAssemblyContents)
            {
                // Parsing the contents of an assembly must happen before and after any other pass.
                this.Passes.AddFirst(new AssemblyDiffingPass(assemblies, this.LogWriter));
                this.Passes.AddLast(new AssemblyDiffingPass(assemblies, this.LogWriter));
            }
        }

        /// <summary>
        /// Owns the complete loaded assembly batch through every operation that can fail before
        /// publication.
        /// </summary>
        private void RewriteAssemblyBatch(
            IReadOnlyList<AssemblyInfo> assemblies, string outputDirectory, RewritingCache cache)
        {
            Exception primaryFailure = null;
            try
            {
                this.OnAssembliesLoaded?.Invoke(assemblies);
                cache.RegisterRewriteInputs(assemblies);
                cache.RecordFrameworkInventories(assemblies.SelectMany(assembly =>
                    assembly.FrameworkInventorySnapshots));
                this.InitializePasses(assemblies);
                foreach (var assembly in assemblies)
                {
                    string assemblyOutputDirectory = outputDirectory;
                    if (this.Options.IsReplacingAssemblies())
                    {
                        assemblyOutputDirectory = Path.Combine(outputDirectory,
                            "assembly-" + Guid.NewGuid().ToString("N"));
                        this.FileSystem.CreateDirectory(assemblyOutputDirectory);
                    }

                    string outputPath = Path.Combine(assemblyOutputDirectory, assembly.Name);
                    this.RewriteAssembly(assembly, outputPath, cache);
                    this.TrackAssemblyProducts(outputPath);
                }
            }
            catch (Exception ex)
            {
                primaryFailure = ex;
            }

            Exception disposalFailure = null;
            foreach (AssemblyInfo assembly in assemblies)
            {
                try
                {
                    assembly.Dispose();
                }
                catch (Exception ex)
                {
                    disposalFailure = disposalFailure is null ? ex :
                        new AggregateException(disposalFailure, ex);
                }
            }

            if (primaryFailure != null)
            {
                if (disposalFailure != null)
                {
                    throw new IOException(
                        "Rewriting failed and the loaded assembly batch could not be fully disposed.",
                        new AggregateException(primaryFailure, disposalFailure));
                }

                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            }

            if (disposalFailure != null)
            {
                throw new IOException("The loaded assembly batch could not be fully disposed.", disposalFailure);
            }
        }

        /// <summary>
        /// Rewrites the specified assembly.
        /// </summary>
        private void RewriteAssembly(AssemblyInfo assembly, string outputPath, RewritingCache cache)
        {
            string resolvedOutputPath = outputPath;
            string[] threadStaticFields = Array.Empty<string>();

            // Read here rather than below, because rewriting stamps the signature that sets it, so by
            // the end of the transformation every assembly reports itself as rewritten. Everything after that --
            // putting the output in place, and recording what this produced -- has to run either way.
            bool wasAlreadyRewritten = assembly.IsRewritten;
            {
                this.LogWriter.LogImportant("... Rewriting the '{0}' assembly ({1})", assembly.Name, assembly.FullName);

                // Runs before the check below, because it only reports on the assembly and does not
                // modify it. Reporting it alongside the other passes would hide it on every incremental
                // build, which is exactly when a developer is most likely to be reading this output.
                assembly.Invoke(this.ThreadStaticDetection);

                // Captured here rather than read back at the end, because a single instance of the pass
                // visits every assembly in turn and holds only the findings of the latest one.
                threadStaticFields = this.ThreadStaticDetection.ReportedFields.ToArray();

                if (wasAlreadyRewritten)
                {
                    this.LogWriter.LogImportant("..... Skipping as assembly is already rewritten with matching signature");
                    this.WriteILOfRewrittenAssembly(assembly, resolvedOutputPath);
                }
                else
                {
                    // Snapshot the assembly's original references so that any core-library references
                    // introduced during rewriting from a mismatched framework can be normalized below.
                    var originalReferences = assembly.Definition.MainModule.AssemblyReferences.ToArray();

                    // Traverse the assembly to invoke each pass.
                    foreach (var pass in this.Passes)
                    {
                        this.LogWriter.LogDebug("..... Invoking the '{0}' pass", pass.GetType().Name);
                        assembly.Invoke(pass);
                    }

                    // Apply the rewriting signature to the assembly metadata.
                    assembly.ApplyRewritingSignatureAttribute(GetAssemblyRewriterVersion());

                    // Normalize any core-library references that rewriting introduced from a mismatched
                    // framework (e.g. the net10 rewriter adding net10 'System.Private.CoreLib' references
                    // to a net8 assembly), so the rewritten assembly loads on the target's runtime.
                    NormalizeCoreLibraryReferences(assembly.Definition.MainModule, originalReferences);

                    // Write the binary in the output path with portable symbols enabled.
                    this.LogWriter.LogImportant("..... Writing the modified '{0}' assembly to {1}", assembly.Name, resolvedOutputPath);
                    this.OutputJournal.Capture(outputPath);
                    this.OutputJournal.Capture(Path.ChangeExtension(outputPath, "pdb"));
                    assembly.Write(outputPath);
                    if (this.Options.IsReplacingAssemblies())
                    {
                        // Later assemblies in the same batch must resolve signatures from earlier
                        // transformed dependencies. Overlay only the engine-owned snapshot copy; live
                        // source publication remains deferred until the final drift gate succeeds.
                        this.FileSystem.CopyFile(outputPath, assembly.ReadPath, true);
                    }

                    if (this.Options.IsLoggingAssemblyContents)
                    {
                        // Write the IL before and after rewriting to a JSON file.
                        this.WriteILToJson(assembly, false, resolvedOutputPath);
                        this.WriteILToJson(assembly, true, resolvedOutputPath);
                    }

                    if (this.Options.IsDiffingAssemblyContents)
                    {
                        // Write the IL diff before and after rewriting to a JSON file.
                        this.WriteILDiffToJson(assembly, resolvedOutputPath);
                    }
                }
            }

            if (wasAlreadyRewritten && !this.Options.IsReplacingAssemblies())
            {
                // An assembly that is already rewritten is not written again, so what puts it in the
                // output directory is the copy of the input tree. That copy mirrors the input, while
                // the output is named after the assembly alone, so the two agree only for an input
                // that sits at the root of its directory. For one named through a subdirectory they
                // do not, and nothing else would ever produce the output this run just recorded.
                //
                // Unconditional rather than only when the output is missing. Reaching here at all
                // means the cache did not accept the previous run, and one reason it does not is an
                // output that was modified since. Leaving such a file in place would record its hash
                // into the new manifest and report the run as a success, which is the one outcome
                // this class exists to prevent.
                this.LogWriter.LogDebug("..... Placing the already rewritten '{0}' assembly at {1}",
                    assembly.Name, resolvedOutputPath);
                this.OutputJournal.Capture(resolvedOutputPath);
                this.CopyWithRetriesAsync(assembly.ReadPath, resolvedOutputPath).Wait();
                string symbolFile = Path.ChangeExtension(assembly.ReadPath, "pdb");
                if (File.Exists(symbolFile))
                {
                    this.OutputJournal.Capture(Path.ChangeExtension(resolvedOutputPath, "pdb"));
                    this.CopyWithRetriesAsync(symbolFile, Path.ChangeExtension(resolvedOutputPath, "pdb")).Wait();
                }
            }

            if (!wasAlreadyRewritten && this.Options.IsReplacingAssemblies())
            {
                this.StageOutput(outputPath, assembly.FilePath);
                if (assembly.IsSymbolFileAvailable())
                {
                    string pdbFile = Path.ChangeExtension(outputPath, "pdb");
                    string targetPdbFile = Path.ChangeExtension(assembly.FilePath, "pdb");
                    this.StageOutput(pdbFile, targetPdbFile);
                }
            }

            if (this.Options.IsReplacingAssemblies())
            {
                this.StageAssemblyArtifacts(outputPath, assembly.FilePath);
                this.PendingCacheRecords.Add(new PendingCacheRecord()
                {
                    Assembly = assembly,
                    OutputPath = assembly.FilePath,
                    ThreadStaticFields = threadStaticFields
                });
                return;
            }

            // Recorded once the output is in its final place, and for skipped assemblies too: the cache
            // records what is on disk rather than what a rewrite would have produced, which is what
            // lets it settle instead of reporting the same assembly as stale on every run.
            cache.RecordAssembly(assembly, resolvedOutputPath, threadStaticFields);
        }

        /// <summary>
        /// Writes the IL of an assembly that was already rewritten, if the configuration asks for it.
        /// </summary>
        /// <remarks>
        /// Rewriting in place consumes the original assembly, so once it has run there is nothing left
        /// to compare against: the original IL and the diff cannot be produced for such an assembly at
        /// all, and writing the rewritten IL under a name that claims to hold the original would be
        /// worse than writing nothing. The rewritten IL is still there to be read, so it is dumped, and
        /// what cannot be produced is reported rather than passed over in silence.
        /// </remarks>
        private void WriteILOfRewrittenAssembly(AssemblyInfo assembly, string outputPath)
        {
            if (!this.Options.IsLoggingAssemblyContents && !this.Options.IsDiffingAssemblyContents)
            {
                return;
            }

            var unavailable = new List<string>();
            if (this.Options.IsLoggingAssemblyContents)
            {
                if (this.RewrittenDiffingPass != null)
                {
                    assembly.Invoke(this.RewrittenDiffingPass);
                    this.WriteILToJson(assembly, true, outputPath);
                }

                unavailable.Add("original IL");
            }

            if (this.Options.IsDiffingAssemblyContents)
            {
                unavailable.Add("IL diff");
            }

            this.LogWriter.LogImportant(
                "..... Cannot write the {0} of '{1}' because it is already rewritten and its original IL is gone. " +
                "Rebuild it, or rewrite it into a separate output directory, to produce it again.",
                string.Join(" or the ", unavailable), assembly.Name);
        }

        /// <summary>
        /// Writes the original or rewritten IL to a JSON file in the specified output path.
        /// </summary>
        internal void WriteILToJson(AssemblyInfo assembly, bool isRewritten, string outputPath)
        {
            var diffingPass = isRewritten ? this.RewrittenDiffingPass : this.OriginalDiffingPass;
            if (diffingPass != null)
            {
                string json = diffingPass.GetJson(assembly);
                if (string.IsNullOrEmpty(json))
                {
                    // Reported rather than passed over, so that an asked-for dump that does not appear
                    // says why. Silence here is what made an already-rewritten assembly look as though
                    // it had produced its artifacts when it had produced nothing.
                    this.LogWriter.LogWarning("..... No {0} IL was captured for '{1}', so none was written",
                        isRewritten ? "rewritten" : "original", assembly.Name);
                    return;
                }

                string jsonFile = Path.ChangeExtension(outputPath, $".{(isRewritten ? "rw" : "il")}.json");
                this.LogWriter.LogImportant("..... Writing the {0} IL of '{1}' as JSON to {2}",
                    isRewritten ? "rewritten" : "original", assembly.Name, jsonFile);
                this.OutputJournal.Capture(jsonFile);
                this.FileSystem.WriteAllText(jsonFile, json);
            }
        }

        /// <summary>
        /// Writes the IL diff to a JSON file in the specified output path.
        /// </summary>
        internal void WriteILDiffToJson(AssemblyInfo assembly, string outputPath)
        {
            var originalDiffingPass = this.OriginalDiffingPass;
            var rewrittenDiffingPass = this.RewrittenDiffingPass;
            if (originalDiffingPass != null && rewrittenDiffingPass != null)
            {
                // Compute the diff between the original and rewritten IL and dump it to JSON.
                string diffJson = originalDiffingPass.GetDiffJson(assembly, rewrittenDiffingPass);
                if (string.IsNullOrEmpty(diffJson))
                {
                    this.LogWriter.LogWarning("..... No IL diff was captured for '{0}', so none was written", assembly.Name);
                    return;
                }

                string jsonFile = Path.ChangeExtension(outputPath, ".diff.json");
                this.LogWriter.LogImportant("..... Writing the IL diff of '{0}' as JSON to {1}", assembly.Name, jsonFile);
                this.OutputJournal.Capture(jsonFile);
                this.FileSystem.WriteAllText(jsonFile, diffJson);
            }
        }

        /// <summary>
        /// Checks if the specified assembly has been already rewritten with the current version.
        /// </summary>
        /// <param name="assembly">The assembly to check.</param>
        /// <returns>True if the assembly has been rewritten with the current version, else false.</returns>
        public static bool IsAssemblyRewritten(Assembly assembly) =>
            assembly.GetCustomAttribute(typeof(RewritingSignatureAttribute)) is RewritingSignatureAttribute attribute &&
            attribute.Version == GetAssemblyRewriterVersion().ToString();

        /// <summary>
        /// Returns the version of the assembly rewriter.
        /// </summary>
        internal static Version GetAssemblyRewriterVersion() => Assembly.GetExecutingAssembly().GetName().Version;

        /// <summary>
        /// Returns the identity of this particular build of the assembly rewriter.
        /// </summary>
        /// <remarks>
        /// The version alone does not identify the rewriter: it changes only when the product is
        /// released, so a locally rebuilt rewriter carries the same one while emitting different IL.
        /// Both the per-assembly <see cref="AssemblySignature"/> and the <see cref="RewritingCache"/>
        /// manifest are checked against this, and the two must give the same answer -- one deciding an
        /// assembly is current while the other decides it is not is how a run ends up either throwing
        /// or silently keeping stale instrumentation. So they read it from here rather than each
        /// spelling out the same expression.
        /// </remarks>
        internal static string GetAssemblyRewriterModuleId() =>
            Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId.ToString("N");

        /// <summary>
        /// Creates the output directory, if it does not already exists, and copies all necessary files.
        /// </summary>
        /// <param name="protectedOutputPaths">
        /// Output files that are already up to date and must survive the copy. Without this, copying
        /// the input directory would put the original assembly back over the rewritten one, and the
        /// run that decided nothing needed rewriting would leave an uninstrumented output behind.
        /// </param>
        /// <param name="sourceDirectory">The immutable source snapshot to mirror.</param>
        /// <returns>The output directory path.</returns>
        private string CreateOutputDirectoryAndCopyFiles(HashSet<string> protectedOutputPaths,
            string sourceDirectory)
        {
            // The full path is taken from 'Path', not from the 'DirectoryInfo' the creation used to
            // return: it is the same string, and asking for it separately is what lets the creation
            // itself go through the seam.
            string outputDirectory = Path.GetFullPath(this.Options.IsReplacingAssemblies() ?
                Path.Combine(this.Options.OutputDirectory, TempDirectory) : this.Options.OutputDirectory);
            this.OutputJournal.CaptureDirectory(outputDirectory);
            this.FileSystem.CreateDirectory(outputDirectory);
            if (!this.Options.IsReplacingAssemblies())
            {
                this.LogWriter.LogImportant("... Copying all files to the '{0}' directory", outputDirectory);
                Exception lastMirrorError = null;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        this.MirroredOutputFiles =
                            this.Mirror.GetMirroredFiles(sourceDirectory, outputDirectory,
                                includeFingerprints: false,
                                excludedDirectories: new[] { this.OutputJournal.BackupDirectory });

                        this.OutputLedger.RemoveStaleMirroredFiles(this.MirroredOutputFiles.Keys,
                            this.AttemptedMirroredFiles, this.OutputJournal.Capture);
                        this.Mirror.Mirror(sourceDirectory, outputDirectory, protectedOutputPaths,
                            this.MirroredOutputFiles.Keys, this.AttemptedMirroredFiles,
                            this.OutputJournal.Capture, this.OutputJournal.CaptureDirectory);
                        var confirmed =
                            this.Mirror.GetMirroredFiles(sourceDirectory, outputDirectory,
                                includeFingerprints: false,
                                excludedDirectories: new[] { this.OutputJournal.BackupDirectory });

                        // Compared on length and write time as well as on name. Equal inventories say
                        // nothing about a file that was rewritten in place after this copied it, and
                        // that file is now in the output holding bytes the input no longer has.
                        if (this.Mirror.DescribeSameFiles(sourceDirectory, outputDirectory,
                            this.MirroredOutputFiles, confirmed, protectedOutputPaths))
                        {
                            this.MirroredOutputFiles = confirmed;
                            lastMirrorError = null;
                            break;
                        }

                        lastMirrorError = new IOException(
                            $"The source directory '{sourceDirectory}' changed while it was being mirrored.");
                    }
                    catch (Exception ex) when (
                        ex is IOException || ex is UnauthorizedAccessException)
                    {
                        lastMirrorError = ex;
                    }

                    if (attempt is 0 && lastMirrorError != null)
                    {
                        try
                        {
                            // Every retry starts from the same output state. In particular, a file
                            // that existed before mirroring must be restored after an earlier attempt
                            // overwrote it, rather than being mistaken for newly-owned output.
                            this.OutputJournal.Restore();
                        }
                        catch (Exception restoreError)
                        {
                            throw new IOException(
                                $"Unable to retry mirroring '{sourceDirectory}', and rollback failed. " +
                                $"The recovery journal remains at '{this.OutputJournal.BackupDirectory}'.",
                                new AggregateException(lastMirrorError, restoreError));
                        }

                        this.AttemptedMirroredFiles.Clear();
                    }
                }

                if (lastMirrorError != null)
                {
                    throw new IOException(
                        $"Unable to mirror a stable snapshot of '{sourceDirectory}'.", lastMirrorError);
                }
            }

            // Copy all the dependent assemblies, but do not overwrite an assembly that is already
            // present in the output directory with the same version. The output directory can contain
            // target-framework-appropriate copies of these assemblies (for example, deployed via NuGet
            // for a different target framework than the rewriter itself runs on); overwriting them with
            // the rewriter's own copies would introduce a target framework mismatch at runtime.
            foreach (var type in new Type[]
                {
                    typeof(CoyoteRuntime),
                    typeof(RewritingEngine),
                    typeof(TelemetryConfiguration),
                    typeof(EventTelemetry),
                    typeof(ITelemetry),
                    typeof(TelemetryClient)
                })
            {
                string assemblyPath = type.Assembly.Location;
                string destination = Path.Combine(this.Options.OutputDirectory, Path.GetFileName(assemblyPath));
                if (this.FileSystem.FileExists(destination) &&
                    GetAssemblyVersion(destination) == type.Assembly.GetName().Version)
                {
                    this.LogWriter.LogDebug("..... Preserving the existing '{0}' assembly in the output directory",
                        Path.GetFileName(assemblyPath));
                    this.TrackProducedOutput(destination);
                    continue;
                }

                if (this.Options.IsReplacingAssemblies())
                {
                    string staged = Path.Combine(outputDirectory, Path.GetFileName(assemblyPath));
                    this.Mirror.CopyFile(assemblyPath, outputDirectory);
                    this.StageOutput(staged, destination);
                }
                else
                {
                    this.OutputJournal.Capture(destination);
                    this.Mirror.CopyFile(assemblyPath, this.Options.OutputDirectory);
                }

                this.TrackProducedOutput(destination);
            }

            return outputDirectory;
        }

        /// <summary>
        /// Records the generated assembly, symbols and enabled debug artifacts for output ownership.
        /// </summary>
        private void TrackAssemblyProducts(string outputPath)
        {
            this.TrackProducedOutput(outputPath);
            this.TrackProducedOutputIfPresent(Path.ChangeExtension(outputPath, "pdb"));
            if (this.Options.IsLoggingAssemblyContents)
            {
                this.TrackProducedOutputIfPresent(Path.ChangeExtension(outputPath, ".il.json"));
                this.TrackProducedOutputIfPresent(Path.ChangeExtension(outputPath, ".rw.json"));
            }

            if (this.Options.IsDiffingAssemblyContents)
            {
                this.TrackProducedOutputIfPresent(Path.ChangeExtension(outputPath, ".diff.json"));
            }
        }

        private void TrackProducedOutputIfPresent(string path)
        {
            if (this.FileSystem.FileExists(path))
            {
                this.TrackProducedOutput(path);
            }
        }

        private void TrackProducedOutput(string path)
        {
            if (this.OutputLedger != null &&
                this.OutputLedger.TryGetRelativeOutputPath(path, out string relativePath))
            {
                this.ProducedOutputFiles.Add(relativePath);
            }
        }

        private void StageAssemblyArtifacts(string stagedOutputPath, string targetOutputPath)
        {
            if (this.Options.IsLoggingAssemblyContents)
            {
                this.StageOutputIfPresent(
                    Path.ChangeExtension(stagedOutputPath, ".il.json"),
                    Path.ChangeExtension(targetOutputPath, ".il.json"));
                this.StageOutputIfPresent(
                    Path.ChangeExtension(stagedOutputPath, ".rw.json"),
                    Path.ChangeExtension(targetOutputPath, ".rw.json"));
            }

            if (this.Options.IsDiffingAssemblyContents)
            {
                this.StageOutputIfPresent(
                    Path.ChangeExtension(stagedOutputPath, ".diff.json"),
                    Path.ChangeExtension(targetOutputPath, ".diff.json"));
            }
        }

        private void StageOutputIfPresent(string sourcePath, string targetPath)
        {
            if (this.FileSystem.FileExists(sourcePath))
            {
                this.StageOutput(sourcePath, targetPath);
            }
        }

        private void StageOutput(string sourcePath, string targetPath)
        {
            string normalizedTarget = RewritingCacheValidator.NormalizeFile(targetPath);
            if (!this.StagedOutputs.Any(output => string.Equals(
                output.TargetPath, normalizedTarget,
                this.FileSystem.IsCaseInsensitive(this.Options.OutputDirectory) ?
                    StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            {
                this.StagedOutputs.Add(new StagedOutput()
                {
                    SourcePath = RewritingCacheValidator.NormalizeFile(sourcePath),
                    TargetPath = normalizedTarget
                });
            }
        }

        private void PublishStagedOutputs()
        {
            foreach (StagedOutput output in this.StagedOutputs)
            {
                this.OutputJournal.Capture(output.TargetPath);
                this.CopyWithRetriesAsync(output.SourcePath, output.TargetPath).Wait();
                this.PublishedStagedOutputPaths.Add(output.TargetPath);
            }

            this.StagedOutputs.Clear();
        }

        /// <summary>
        /// Returns the assembly version of the specified file, or null if it cannot be read.
        /// </summary>
        private static Version GetAssemblyVersion(string filePath)
        {
            try
            {
                return AssemblyName.GetAssemblyName(filePath).Version;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Names of the core-library assemblies whose types the runtime unifies. These can be
        /// imported during rewriting from the rewriter's own framework rather than the target's.
        /// </summary>
        private static readonly string[] CoreLibraryAssemblyNames = new[]
        {
            "System.Runtime",
            "System.Private.CoreLib",
            "netstandard",
            "mscorlib"
        };

        /// <summary>
        /// Remaps core-library references that rewriting introduced from a framework other than the
        /// one the rewritten assembly targets back to the assembly's own core-library reference.
        /// </summary>
        /// <remarks>
        /// When the rewriter runs on a different framework than the assembly being rewritten -- for
        /// example, the net10 'interleavex' tool rewriting a net8 assembly -- importing system types
        /// (often via reflection) can add references to the rewriter's framework, such as net10
        /// 'System.Private.CoreLib'. The target's runtime cannot load that assembly, so the rewritten
        /// assembly would fail with a 'Could not load ... System.Runtime/System.Private.CoreLib'
        /// exception. Pointing those references back at the assembly's original core library keeps the
        /// rewritten assembly loadable on its own runtime. This is a no-op in the common case where the
        /// rewriter and the target share a framework (no mismatched references exist).
        /// </remarks>
        private static void NormalizeCoreLibraryReferences(ModuleDefinition module,
            IList<AssemblyNameReference> originalReferences)
        {
            // The assembly's own core-library reference, captured before rewriting.
            AssemblyNameReference canonical = null;
            foreach (string name in CoreLibraryAssemblyNames)
            {
                canonical = originalReferences.FirstOrDefault(r => r.Name == name);
                if (canonical != null)
                {
                    break;
                }
            }

            if (canonical is null)
            {
                return;
            }

            // Core-library references whose version differs from the target's were introduced during
            // rewriting from a mismatched framework.
            var mismatched = new HashSet<AssemblyNameReference>(module.AssemblyReferences.Where(
                r => CoreLibraryAssemblyNames.Contains(r.Name) && r.Version != canonical.Version));
            if (mismatched.Count is 0)
            {
                return;
            }

            foreach (TypeReference typeReference in module.GetTypeReferences())
            {
                if (typeReference.Scope is AssemblyNameReference scope && mismatched.Contains(scope))
                {
                    typeReference.Scope = canonical;
                }
            }

            foreach (ExportedType exportedType in module.ExportedTypes)
            {
                if (exportedType.Scope is AssemblyNameReference scope && mismatched.Contains(scope))
                {
                    exportedType.Scope = canonical;
                }
            }

            foreach (AssemblyNameReference reference in mismatched)
            {
                module.AssemblyReferences.Remove(reference);
            }
        }

        /// <summary>
        /// Copies the specified file to the destination with retries.
        /// </summary>
        private async Task CopyWithRetriesAsync(string srcFile, string targetFile)
        {
            for (int retries = 10; retries >= 0; retries--)
            {
                try
                {
                    File.Copy(srcFile, targetFile, true);

                    // Without this the loop runs to exhaustion, copying the file eleven times and
                    // failing the run if the last of those attempts happens to hit a transient lock.
                    return;
                }
                catch (Exception)
                {
                    if (retries is 0)
                    {
                        throw;
                    }

                    await Task.Delay(100);
                    this.LogWriter.LogWarning("... Retrying write to {0}", targetFile);
                }
            }
        }

        /// <summary>
        /// Attempts to resolve an assembly by probing all .NET shared framework directories.
        /// This is a fallback for when the primary .runtimeconfig.json-based discovery did not
        /// add the right directories (e.g. when rewriting a class library without its own config).
        /// </summary>
        /// <remarks>
        /// Which installation to probe is asked of the environment this run was given, so a caller
        /// describing a different one gets a different answer. What is then read out of it stays on
        /// real paths, and deliberately: Mono.Cecil reads the candidate through its own resolver, so
        /// a file system that reported a candidate the disk does not hold would be answering a
        /// question nothing downstream asks.
        /// </remarks>
        internal AssemblyDefinition TryResolveFromSharedFrameworks(AssemblyNameReference reference)
        {
            if (this.CachedFrameworkDirectories is null)
            {
                this.CachedFrameworkDirectories = new List<string>();
                string dotnetRoot = this.EffectiveDotnetRoot;
                if (dotnetRoot != null)
                {
                    string sharedDir = Path.Combine(dotnetRoot, "shared");
                    IReadOnlyList<string> frameworkDirectories;
                    this.FallbackFrameworkInventories.AddRange(CaptureFallbackFrameworkInventories(
                        this.FileSystem, sharedDir, out frameworkDirectories));
                    this.CachedFrameworkDirectories.AddRange(frameworkDirectories);
                }
            }

            string fileName = reference.Name + ".dll";
            foreach (string dir in this.CachedFrameworkDirectories)
            {
                string candidate = Path.Combine(dir, fileName);
                this.CurrentCache?.RecordResolutionCandidate(candidate);
                if (this.FileSystem.FileExists(candidate))
                {
                    try
                    {
                        byte[] content;
                        using (var stream = this.FileSystem.OpenRead(candidate, FileReadSharing.DenyWriters))
                        using (var memory = new MemoryStream())
                        {
                            stream.CopyTo(memory);
                            content = memory.ToArray();
                        }

                        this.CurrentCache?.RecordConsumedResolution(candidate, content);
                        return AssemblyDefinition.ReadAssembly(new MemoryStream(content, false),
                            new ReaderParameters { ReadSymbols = false, InMemory = true });
                    }
                    catch
                    {
                        // Ignore and continue searching.
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Captures the directory names that determine the shared-framework fallback candidates.
        /// </summary>
        /// <remarks>
        /// The shared directory itself is recorded even when it is absent. Its later appearance
        /// changes the candidate space just as surely as a new framework or version under it does.
        /// </remarks>
        internal static IReadOnlyList<CacheDirectoryListing> CaptureFallbackFrameworkInventories(
            IFileSystem fileSystem, string sharedDirectory, out IReadOnlyList<string> versionDirectories)
        {
            bool sharedExists = fileSystem.DirectoryExists(sharedDirectory);
            string[] frameworkDirectories = sharedExists ?
                fileSystem.GetDirectories(sharedDirectory, "*", false) : Array.Empty<string>();
            var snapshots = new List<CacheDirectoryListing>()
            {
                RewritingCacheValidator.CaptureDirectoryNames(
                    sharedDirectory, sharedExists, frameworkDirectories)
            };
            var versions = new List<string>();

            if (sharedExists)
            {
                foreach (string frameworkDirectory in OrderFrameworkDirectories(frameworkDirectories))
                {
                    string[] installedVersions = fileSystem.GetDirectories(
                        frameworkDirectory, "*", false);
                    snapshots.Add(RewritingCacheValidator.CaptureDirectoryNames(
                        frameworkDirectory, true, installedVersions));
                    versions.AddRange(OrderVersionDirectories(installedVersions));
                }
            }

            versionDirectories = versions;
            return snapshots;
        }

        private static IEnumerable<string> OrderFrameworkDirectories(IEnumerable<string> directories)
        {
            return directories
                .OrderBy(path => string.Equals(Path.GetFileName(path), "Microsoft.NETCore.App",
                    StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(path => Path.GetFileName(path), StringComparer.Ordinal);
        }

        private static IEnumerable<string> OrderVersionDirectories(IEnumerable<string> directories)
        {
            return directories
                .Select(path =>
                {
                    string name = Path.GetFileName(path);
                    int dash = name.IndexOf('-');
                    string core = dash < 0 ? name : name.Substring(0, dash);
                    return new
                    {
                        Path = path,
                        IsValid = Version.TryParse(core, out Version version),
                        Version = version,
                        IsPrerelease = dash >= 0
                    };
                })
                .OrderByDescending(item => item.IsValid)
                .ThenByDescending(item => item.Version)
                .ThenBy(item => item.IsPrerelease)
                .ThenBy(item => item.Path, StringComparer.Ordinal)
                .Select(item => item.Path);
        }

        /// <summary>
        /// Handles an assembly resolution error.
        /// </summary>
        private AssemblyDefinition OnResolveAssemblyFailure(object sender, AssemblyNameReference reference)
        {
            // Try to resolve from .NET shared framework directories as a fallback.
            var resolved = this.TryResolveFromSharedFrameworks(reference);
            if (resolved != null)
            {
                return resolved;
            }

            if (!this.ResolveWarnings.Contains(reference.FullName))
            {
                this.LogWriter.LogWarning("Unable to resolve assembly: '{0}'", reference.FullName);
                this.ResolveWarnings.Add(reference.FullName);
            }

            return null;
        }
    }
}

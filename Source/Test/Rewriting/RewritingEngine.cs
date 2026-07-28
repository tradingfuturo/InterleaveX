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
        /// Initializes a new instance of the <see cref="RewritingEngine"/> class.
        /// </summary>
        private RewritingEngine(RewritingOptions options, Configuration configuration, LogWriter logWriter, Profiler profiler)
        {
            this.Options = options.Sanitize();
            this.Configuration = configuration;
            this.Passes = new LinkedList<Pass>();
            this.ResolveWarnings = new HashSet<string>();
            this.LogWriter = logWriter;
            this.Profiler = profiler;
        }

        /// <summary>
        /// Runs the engine using the specified rewriting options.
        /// </summary>
        internal static void Run(RewritingOptions options, Configuration configuration, LogWriter logWriter, Profiler profiler)
        {
            var engine = new RewritingEngine(options, configuration, logWriter, profiler);
            engine.Run();
        }

        /// <summary>
        /// Runs the rewriting engine.
        /// </summary>
        private void Run()
        {
            this.Profiler.StartMeasuringExecutionTime();

            // Ask the cache before anything is copied or loaded. An up-to-date run has to leave the
            // rewritten outputs alone, and the copy below would otherwise overwrite them with the
            // original assemblies; and loading the assemblies is itself a large part of the cost that
            // an up-to-date run exists to avoid.
            var cache = new RewritingCache(this.Options, this.Configuration, this.LogWriter);
            bool isUpToDate = cache.TryGetUpToDateRun(out HashSet<string> protectedOutputPaths);

            // Create the output directory and copy any necessary files. This still runs when everything
            // is up to date: the output directory mirrors the input one, and nothing else in it is
            // tracked by the cache.
            string outputDirectory = this.CreateOutputDirectoryAndCopyFiles(protectedOutputPaths);

            try
            {
                if (isUpToDate)
                {
                    // The findings of the analysis passes hold whether or not anything was rewritten,
                    // so they are replayed rather than lost.
                    cache.ReplayDiagnostics();
                    this.LogWriter.LogImportant("... Skipping rewriting as every assembly is up to date");
                    return;
                }

                // Get the set of assemblies to rewrite.
                var assemblies = AssemblyInfo.LoadAssembliesToRewrite(this.Options, this.OnResolveAssemblyFailure);
                this.InitializePasses(assemblies);
                foreach (var assembly in assemblies)
                {
                    string outputPath = Path.Combine(outputDirectory, assembly.Name);
                    this.RewriteAssembly(assembly, outputPath, cache);
                }

                // Only once every assembly has been dealt with: a manifest describing a partially
                // rewritten directory would report assemblies as up to date that were never reached.
                cache.Save();
            }
            catch (Exception ex)
            {
                ExceptionDispatchInfo.Capture(ex).Throw();
            }
            finally
            {
                if (this.Options.IsReplacingAssemblies())
                {
                    // If we are replacing the original assemblies, then delete the temporary output directory.
                    Directory.Delete(outputDirectory, true);
                }

                this.Profiler.StopMeasuringExecutionTime();
            }
        }

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
        /// Rewrites the specified assembly.
        /// </summary>
        private void RewriteAssembly(AssemblyInfo assembly, string outputPath, RewritingCache cache)
        {
            string resolvedOutputPath = this.Options.IsReplacingAssemblies() ? assembly.FilePath : outputPath;
            string[] threadStaticFields = Array.Empty<string>();

            // Read here rather than below, because rewriting stamps the signature that sets it, so by
            // the end of the 'try' every assembly reports itself as rewritten. Everything after that --
            // putting the output in place, and recording what this produced -- has to run either way.
            bool wasAlreadyRewritten = assembly.IsRewritten;
            try
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
                    assembly.Write(outputPath);

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
            finally
            {
                assembly.Dispose();
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
                this.CopyWithRetriesAsync(assembly.FilePath, resolvedOutputPath).Wait();
                string symbolFile = Path.ChangeExtension(assembly.FilePath, "pdb");
                if (File.Exists(symbolFile))
                {
                    this.CopyWithRetriesAsync(symbolFile, Path.ChangeExtension(resolvedOutputPath, "pdb")).Wait();
                }
            }

            if (!wasAlreadyRewritten && this.Options.IsReplacingAssemblies())
            {
                string targetPath = Path.Combine(this.Options.AssembliesDirectory, assembly.Name);
                this.CopyWithRetriesAsync(outputPath, assembly.FilePath).Wait();
                if (assembly.IsSymbolFileAvailable())
                {
                    string pdbFile = Path.ChangeExtension(outputPath, "pdb");
                    string targetPdbFile = Path.ChangeExtension(targetPath, "pdb");
                    this.CopyWithRetriesAsync(pdbFile, targetPdbFile).Wait();
                }
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
                File.WriteAllText(jsonFile, json);
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
                File.WriteAllText(jsonFile, diffJson);
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
        /// <returns>The output directory path.</returns>
        private string CreateOutputDirectoryAndCopyFiles(HashSet<string> protectedOutputPaths)
        {
            string sourceDirectory = this.Options.AssembliesDirectory;
            string outputDirectory = Directory.CreateDirectory(this.Options.IsReplacingAssemblies() ?
                Path.Combine(this.Options.OutputDirectory, TempDirectory) : this.Options.OutputDirectory).FullName;
            if (!this.Options.IsReplacingAssemblies())
            {
                this.LogWriter.LogImportant("... Copying all files to the '{0}' directory", outputDirectory);

                // Copy all files to the output directory, skipping any nested directory files.
                foreach (string filePath in Directory.GetFiles(sourceDirectory, "*"))
                {
                    this.CopyFileUnlessProtected(filePath, outputDirectory, protectedOutputPaths);
                }

                // Copy all nested directories to the output directory, while preserving directory structure.
                foreach (string directoryPath in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
                {
                    // Avoid copying the output directory itself.
                    if (!directoryPath.StartsWith(outputDirectory))
                    {
                        this.LogWriter.LogDebug("..... Copying the '{0}' directory", directoryPath);
                        string path = Path.Combine(outputDirectory, directoryPath.Remove(0, sourceDirectory.Length)
                            .TrimStart('\\', '/'));
                        Directory.CreateDirectory(path);
                        foreach (string filePath in Directory.GetFiles(directoryPath, "*"))
                        {
                            this.CopyFileUnlessProtected(filePath, path, protectedOutputPaths);
                        }
                    }
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
                if (File.Exists(destination) && GetAssemblyVersion(destination) == type.Assembly.GetName().Version)
                {
                    this.LogWriter.LogDebug("..... Preserving the existing '{0}' assembly in the output directory",
                        Path.GetFileName(assemblyPath));
                    continue;
                }

                CopyFile(assemblyPath, this.Options.OutputDirectory);
            }

            return outputDirectory;
        }

        /// <summary>
        /// Copies the specified file to the destination, unless doing so would overwrite an output
        /// that is already up to date, or the cache manifest itself.
        /// </summary>
        private void CopyFileUnlessProtected(string filePath, string destination, HashSet<string> protectedOutputPaths)
        {
            if (string.Equals(Path.GetFileName(filePath), RewritingCache.ManifestFileName, StringComparison.Ordinal))
            {
                // An input directory that was itself rewritten in place holds a manifest describing
                // that run. Copying it here would leave a manifest in the output directory that
                // describes a different one.
                this.LogWriter.LogDebug("..... Skipping the '{0}' file, which belongs to another run", filePath);
                return;
            }

            string targetPath = Path.Combine(destination, Path.GetFileName(filePath));
            if (protectedOutputPaths.Contains(Path.GetFullPath(targetPath)))
            {
                this.LogWriter.LogDebug("..... Preserving the up-to-date '{0}' file", targetPath);
                return;
            }

            if (IsAlreadyCopied(filePath, targetPath))
            {
                // This copy runs even when the whole run is up to date, for the sake of the untracked
                // files in the directory, so it is on the path that exists to do as little as possible.
                // Two stat calls in place of rewriting tens of megabytes of assemblies, symbols and IL
                // dumps that are already byte for byte what would be written over them.
                this.LogWriter.LogDebug("..... Skipping the unchanged '{0}' file", targetPath);
                return;
            }

            this.LogWriter.LogDebug("..... Copying the '{0}' file", filePath);
            CopyFile(filePath, destination);
        }

        /// <summary>
        /// Checks whether the destination already holds what copying the source would put there.
        /// </summary>
        /// <remarks>
        /// Decided on content, not on length and last-write time as the MSBuild copy task does. Equal
        /// metadata is not equal bytes: a file restored, checked out or unpacked with its timestamp
        /// preserved keeps the size and time of the one it replaced, and skipping it on that evidence
        /// would leave the previous bytes in the output. That matters most for exactly the file this
        /// is most likely to be asked about -- a dependency that rewriting resolved from its new
        /// source, while what runs beside the rewritten assembly is the old copy left here.
        ///
        /// Compared rather than hashed because both files are in hand: a digest would have to read
        /// both sides too, and would add a collision class in exchange for nothing. This stops at the
        /// first byte that differs, and at the first block for files that differ early, so the whole
        /// read happens only when the answer is that no copy is needed.
        /// </remarks>
        private static bool IsAlreadyCopied(string filePath, string targetPath)
        {
            var source = new FileInfo(filePath);
            var target = new FileInfo(targetPath);
            if (!target.Exists || source.Length != target.Length)
            {
                return false;
            }

            try
            {
                return HasSameContent(filePath, targetPath);
            }
            catch (IOException)
            {
                // Unreadable for the moment says nothing about the content, and the copy that follows
                // reports the problem in its own terms if it is still there.
                return false;
            }
        }

        /// <summary>
        /// Checks whether two files of equal length hold the same bytes.
        /// </summary>
        private static bool HasSameContent(string leftPath, string rightPath)
        {
            const int BlockSize = 1 << 16;
            using var left = new FileStream(leftPath, FileMode.Open, FileAccess.Read, FileShare.Read, BlockSize);
            using var right = new FileStream(rightPath, FileMode.Open, FileAccess.Read, FileShare.Read, BlockSize);
            byte[] leftBlock = new byte[BlockSize];
            byte[] rightBlock = new byte[BlockSize];
            while (true)
            {
                int count = ReadBlock(left, leftBlock);
                if (count != ReadBlock(right, rightBlock))
                {
                    return false;
                }

                if (count is 0)
                {
                    return true;
                }

                // Eight bytes at a time, because this runs over every file in the directory and the
                // files it runs over are assemblies and IL dumps rather than a handful of bytes.
                int whole = count - (count % sizeof(long));
                for (int index = 0; index < whole; index += sizeof(long))
                {
                    if (BitConverter.ToInt64(leftBlock, index) != BitConverter.ToInt64(rightBlock, index))
                    {
                        return false;
                    }
                }

                for (int index = whole; index < count; index++)
                {
                    if (leftBlock[index] != rightBlock[index])
                    {
                        return false;
                    }
                }
            }
        }

        /// <summary>
        /// Fills the specified block from the stream, returning how much was read. A short read is not
        /// the end of the stream, so this asks again until the block is full or there is no more.
        /// </summary>
        private static int ReadBlock(Stream stream, byte[] block)
        {
            int total = 0;
            while (total < block.Length)
            {
                int read = stream.Read(block, total, block.Length - total);
                if (read is 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }

        /// <summary>
        /// Copies the specified file to the destination.
        /// </summary>
        private static void CopyFile(string filePath, string destination) =>
            File.Copy(filePath, Path.Combine(destination, Path.GetFileName(filePath)), true);

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
        private AssemblyDefinition TryResolveFromSharedFrameworks(AssemblyNameReference reference)
        {
            if (this.CachedFrameworkDirectories is null)
            {
                this.CachedFrameworkDirectories = new List<string>();
                string dotnetRoot = AssemblyInfo.GetDotnetRoot();
                if (dotnetRoot != null)
                {
                    string sharedDir = Path.Combine(dotnetRoot, "shared");
                    if (Directory.Exists(sharedDir))
                    {
                        foreach (string frameworkDir in Directory.GetDirectories(sharedDir))
                        {
                            foreach (string versionDir in Directory.GetDirectories(frameworkDir))
                            {
                                this.CachedFrameworkDirectories.Add(versionDir);
                            }
                        }
                    }
                }
            }

            string fileName = reference.Name + ".dll";
            foreach (string dir in this.CachedFrameworkDirectories)
            {
                string candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                {
                    try
                    {
                        return AssemblyDefinition.ReadAssembly(candidate,
                            new ReaderParameters { ReadSymbols = false });
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

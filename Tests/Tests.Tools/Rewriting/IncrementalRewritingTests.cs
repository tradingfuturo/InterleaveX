// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Coyote.Logging;
using Microsoft.Coyote.Rewriting;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Tools.Tests
{
    /// <summary>
    /// Tests that rewriting skips work it has already done, and only when it is safe to.
    /// </summary>
    /// <remarks>
    /// A wrong answer here is not a slow build but a silent one: tests would run against an assembly
    /// that was never instrumented, which nothing downstream detects. So each test drives the real
    /// engine over a staged copy of a real assembly and asserts on what reached the log, rather than
    /// on how long anything took.
    /// </remarks>
    public class IncrementalRewritingTests : BaseToolsTest
    {
        /// <summary>
        /// Logged when a run decided that nothing needed rewriting.
        /// </summary>
        private const string UpToDateMessage = "Skipping rewriting as every assembly is up to date";

        /// <summary>
        /// Logged when an assembly was actually rewritten.
        /// </summary>
        private const string RewroteMessage = "Writing the modified";

        public IncrementalRewritingTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 60000)]
        public void TestSecondRunIsUpToDate()
        {
            using var workspace = Workspace.Create();
            Assert.Contains(RewroteMessage, workspace.Rewrite());
            Assert.Contains(UpToDateMessage, workspace.Rewrite());
        }

        [Fact(Timeout = 60000)]
        public void TestUpToDateRunKeepsTheRewrittenOutput()
        {
            // The input directory is copied over the output directory on every run, so an output that
            // is up to date has to be excluded from that copy. Without it, the run that decides
            // nothing needs rewriting is exactly the run that replaces the rewritten assembly with
            // the original one.
            using var workspace = Workspace.Create();
            workspace.Rewrite();
            long rewrittenLength = new FileInfo(workspace.OutputAssemblyPath).Length;

            Assert.Contains(UpToDateMessage, workspace.Rewrite());
            Assert.Equal(rewrittenLength, new FileInfo(workspace.OutputAssemblyPath).Length);
            Assert.NotEqual(new FileInfo(workspace.InputAssemblyPath).Length, rewrittenLength);
        }

        [Fact(Timeout = 60000)]
        public void TestUpToDateRunKeepsTheRewrittenOutputSpelledDifferently()
        {
            // The manifest records the output path as the assembly was spelled on the way in, while
            // the copy that has to leave that output alone enumerates the directory and sees the name
            // as it is on disk. Where the file system ignores case those name the same file, so
            // comparing them ordinally would let the copy put the original back over the rewritten
            // assembly, and the same run would then report everything as up to date.
            using var workspace = Workspace.Create();
            if (!File.Exists(workspace.RecasedInputAssemblyPath))
            {
                // Two spellings are two files on a case-sensitive file system, and rewriting the one
                // that is not there would fail rather than demonstrate anything. Asked of the file
                // system rather than of the comparer, which is what this is here to check.
                return;
            }

            workspace.Rewrite(options => options.AssemblyPaths =
                new HashSet<string>() { workspace.RecasedInputAssemblyPath });
            long rewrittenLength = new FileInfo(workspace.OutputAssemblyPath).Length;

            Assert.Contains(UpToDateMessage, workspace.Rewrite(options => options.AssemblyPaths =
                new HashSet<string>() { workspace.RecasedInputAssemblyPath }));
            Assert.Equal(rewrittenLength, new FileInfo(workspace.OutputAssemblyPath).Length);
            Assert.NotEqual(new FileInfo(workspace.InputAssemblyPath).Length, rewrittenLength);
        }

        [Fact(Timeout = 60000)]
        public void TestUpToDateRunStaysUpToDate()
        {
            // Being up to date once is not enough: a run that skips must leave the output directory
            // exactly as it found it, or the run after it sees something it recorded change and
            // rewrites again. The symbol file is what makes this more than a repeat of the test
            // above -- it is written beside the assembly and copied over from the input directory.
            using var workspace = Workspace.Create();
            workspace.Rewrite();
            Assert.Contains(UpToDateMessage, workspace.Rewrite());
            Assert.Contains(UpToDateMessage, workspace.Rewrite());
        }

        [Fact(Timeout = 60000)]
        public void TestUpToDateRunStillCopiesUntrackedFiles()
        {
            // The output directory mirrors the input one, and the cache knows nothing about the rest
            // of what is in it, so the copy has to happen even when no assembly needs rewriting.
            using var workspace = Workspace.Create();
            workspace.Rewrite();

            string name = "untracked.txt";
            File.WriteAllText(Path.Combine(workspace.InputDirectory, name), "written after the first run");
            Assert.Contains(UpToDateMessage, workspace.Rewrite());
            Assert.True(File.Exists(Path.Combine(workspace.OutputDirectory, name)));
        }

        [Fact(Timeout = 60000)]
        public void TestChangedConfigurationIsRewritten()
        {
            using var workspace = Workspace.Create();
            workspace.Rewrite();
            Assert.Contains(RewroteMessage, workspace.Rewrite(
                options => options.IsDataRaceCheckingEnabled = !options.IsDataRaceCheckingEnabled));
        }

        [Fact(Timeout = 60000)]
        public void TestDisabledCacheIsRewritten()
        {
            using var workspace = Workspace.Create();
            workspace.Rewrite();
            Assert.Contains(RewroteMessage, workspace.Rewrite(
                options => options.IsIncrementalRewritingDisabled = true));

            // Disabling the cache for one run must not stop it being maintained, otherwise the run
            // after it would repeat the work for no reason.
            Assert.Contains(UpToDateMessage, workspace.Rewrite());
        }

        [Fact(Timeout = 60000)]
        public void TestDeletedOutputIsRewritten()
        {
            using var workspace = Workspace.Create();
            workspace.Rewrite();
            File.Delete(workspace.OutputAssemblyPath);
            Assert.Contains(RewroteMessage, workspace.Rewrite());
        }

        [Fact(Timeout = 60000)]
        public void TestAppearingSymbolFileIsRewritten()
        {
            // Whether symbols exist decides whether they are read and written at all, so a symbol file
            // that appears changes what a rewrite would produce.
            using var workspace = Workspace.Create(includeSymbols: false);
            workspace.Rewrite();
            workspace.AddInputSymbolFile();
            Assert.Contains(RewroteMessage, workspace.Rewrite());
        }

        [Fact(Timeout = 60000)]
        public void TestAppearingDependencyIsRewritten()
        {
            // Which assemblies get rewritten is decided by probing the input directory for each
            // reference, so a reference that appears changes the set even though every file the
            // previous run recorded is untouched.
            using var workspace = Workspace.Create();
            workspace.Rewrite();

            Assert.NotNull(workspace.AddInputCopyOfReferencedAssembly());
            Assert.Contains(RewroteMessage, workspace.Rewrite());
        }

        [Fact(Timeout = 60000)]
        public void TestAppearingAssemblyInSearchPathIsRewritten()
        {
            // A search directory decides what a reference resolves to, so an assembly appearing in one
            // can win a resolution that went elsewhere, or satisfy one that failed. Nothing the
            // previous run read has changed, so only the offer itself can report this.
            using var workspace = Workspace.Create();
            string searchPath = workspace.AddSearchDirectory();
            Assert.Contains(RewroteMessage, workspace.Rewrite(
                options => options.DependencySearchPaths = new List<string>() { searchPath }));
            Assert.Contains(UpToDateMessage, workspace.Rewrite(
                options => options.DependencySearchPaths = new List<string>() { searchPath }));

            File.Copy(workspace.InputAssemblyPath, Path.Combine(searchPath, "Appeared.dll"));
            Assert.Contains(RewroteMessage, workspace.Rewrite(
                options => options.DependencySearchPaths = new List<string>() { searchPath }));
        }

        [Fact(Timeout = 60000)]
        public void TestChangedRuntimeConfigIsRewritten()
        {
            // The runtime config names the shared frameworks that resolution falls back to, so it
            // decides which implementation assemblies the rewriter reads without being one itself.
            using var workspace = Workspace.Create();
            workspace.Rewrite();
            Assert.Contains(UpToDateMessage, workspace.Rewrite());

            string runtimeConfig = Path.ChangeExtension(workspace.InputAssemblyPath, ".runtimeconfig.json");
            Assert.True(File.Exists(runtimeConfig), "File not found: " + runtimeConfig);
            File.WriteAllText(runtimeConfig, File.ReadAllText(runtimeConfig).Replace("8.0.0", "8.0.1"));
            Assert.Contains(RewroteMessage, workspace.Rewrite());
        }

        [Fact(Timeout = 60000)]
        public void TestUpToDateRunKeepsTheWrittenDiff()
        {
            // An input directory that was rewritten in place holds debug artifacts under the same
            // names as the ones this output directory produces, and the copy that mirrors the input
            // would otherwise put those over them. They are compared by length alone, so a stale one
            // of the right size would then be taken for the real thing on every run after.
            using var workspace = Workspace.Create();
            workspace.Rewrite(options => options.IsDiffingAssemblyContents = true);

            string outputDiff = Path.ChangeExtension(workspace.OutputAssemblyPath, ".diff.json");
            Assert.True(File.Exists(outputDiff), "File not found: " + outputDiff);
            string written = File.ReadAllText(outputDiff);

            File.WriteAllText(Path.ChangeExtension(workspace.InputAssemblyPath, ".diff.json"),
                "[ \"an artifact of some other run\" ]");
            Assert.Contains(UpToDateMessage, workspace.Rewrite(
                options => options.IsDiffingAssemblyContents = true));
            Assert.Equal(written, File.ReadAllText(outputDiff));
        }

        [Fact(Timeout = 60000)]
        public void TestChangedFileWithUnchangedMetadataIsCopied()
        {
            // The copy that mirrors the input directory skips a file the output already holds. Deciding
            // that on length and timestamp would keep whatever is in the output when a file is restored
            // or checked out with its metadata preserved, which is how source control and package
            // restore put files down, and the output would go on serving the previous bytes.
            using var workspace = Workspace.Create();
            string name = "dependency.bin";
            string input = Path.Combine(workspace.InputDirectory, name);
            string output = Path.Combine(workspace.OutputDirectory, name);
            // The same length, so that only the content can tell the two revisions apart.
            File.WriteAllText(input, new string('a', 4096));
            workspace.Rewrite();
            Assert.True(File.Exists(output), "File not found: " + output);

            var stamp = new FileInfo(input).LastWriteTimeUtc;
            File.WriteAllText(input, new string('b', 4096));
            File.SetLastWriteTimeUtc(input, stamp);
            File.SetLastWriteTimeUtc(output, stamp);
            Assert.Equal(new FileInfo(input).Length, new FileInfo(output).Length);

            Assert.Contains(UpToDateMessage, workspace.Rewrite());
            Assert.Equal(File.ReadAllText(input), File.ReadAllText(output));
        }

        [Fact(Timeout = 60000)]
        public void TestPathComparerMatchesTheFileSystem()
        {
            // Whether two spellings name one file is a property of the file system rather than of the
            // platform: macOS ships case-insensitive but can be formatted otherwise. So the comparer is
            // checked against what this file system actually does, asked here independently.
            using var workspace = Workspace.Create();
            string directory = Path.Combine(workspace.InputDirectory, "CasedDirectory");
            Directory.CreateDirectory(directory);
            bool isCaseInsensitive = Directory.Exists(
                Path.Combine(workspace.InputDirectory, "caseddirectory"));

            Assert.Equal(isCaseInsensitive,
                RewritingCache.GetPathComparer(workspace.InputDirectory).Equals(directory, directory.ToLowerInvariant()));
        }

        [Fact(Timeout = 60000)]
        public void TestUnreadableManifestIsIgnored()
        {
            // A cache that cannot be read reports nothing as up to date, and never fails the run.
            using var workspace = Workspace.Create();
            workspace.Rewrite();
            File.WriteAllText(workspace.ManifestPath, "{ this is not a manifest");
            Assert.Contains(RewroteMessage, workspace.Rewrite());
            Assert.Contains(UpToDateMessage, workspace.Rewrite());
        }

        [Fact(Timeout = 60000)]
        public void TestManifestOfAnotherDirectoryIsIgnored()
        {
            // An input directory that was rewritten in place carries a manifest of its own, and the
            // copy would otherwise bring it along and let it describe this run.
            using var workspace = Workspace.Create();
            workspace.Rewrite();
            string manifest = File.ReadAllText(workspace.ManifestPath);
            File.WriteAllText(workspace.ManifestPath,
                manifest.Replace(workspace.OutputDirectory.Replace("\\", "\\\\"), "C:\\\\somewhere\\\\else"));
            Assert.Contains(RewroteMessage, workspace.Rewrite());
        }

        [Fact(Timeout = 60000)]
        public void TestTouchedInputIsUpToDate()
        {
            // Change is decided by content, not by timestamps, which tools and source control rewrite
            // freely.
            using var workspace = Workspace.Create();
            workspace.Rewrite();
            File.SetLastWriteTimeUtc(workspace.InputAssemblyPath, DateTime.UtcNow.AddMinutes(1));
            Assert.Contains(UpToDateMessage, workspace.Rewrite());
        }

        [Fact(Timeout = 60000)]
        public void TestThreadStaticReportSurvivesUpToDateRun()
        {
            // The report holds whether or not anything was rewritten, and an incremental build is when
            // it is most likely to be read, so a skipped run has to reproduce it.
            using var workspace = Workspace.Create();
            string rewritten = workspace.Rewrite();
            string skipped = workspace.Rewrite();
            Assert.Contains(UpToDateMessage, skipped);

            const string Report = "thread-static field(s) in";
            Assert.Equal(rewritten.Contains(Report), skipped.Contains(Report));
        }

        /// <summary>
        /// A directory holding a copy of an assembly to rewrite, with a separate output directory.
        /// </summary>
        private sealed class Workspace : IDisposable
        {
            /// <summary>
            /// The assembly that gets rewritten. It is small, is not itself rewritten by the build,
            /// and sits beside everything it references.
            /// </summary>
            private const string TargetAssemblyName = "Microsoft.Coyote.Tests.Tools.dll";

            internal string InputDirectory { get; private set; }

            internal string OutputDirectory { get; private set; }

            internal string InputAssemblyPath => Path.Combine(this.InputDirectory, TargetAssemblyName);

            internal string OutputAssemblyPath => Path.Combine(this.OutputDirectory, TargetAssemblyName);

            /// <summary>
            /// The same file as <see cref="InputAssemblyPath"/>, named the way a configuration file
            /// or an assembly reference may well spell it rather than the way it is on disk.
            /// </summary>
            internal string RecasedInputAssemblyPath =>
                Path.Combine(this.InputDirectory, TargetAssemblyName.ToUpperInvariant());

            internal string ManifestPath => Path.Combine(this.OutputDirectory, RewritingCache.ManifestFileName);

            private string RootDirectory;

            internal static Workspace Create(bool includeSymbols = true)
            {
                string source = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var workspace = new Workspace()
                {
                    RootDirectory = Path.Combine(Path.GetTempPath(),
                        "coyote-incremental-" + Guid.NewGuid().ToString("N"))
                };

                workspace.InputDirectory = Path.Combine(workspace.RootDirectory, "input");
                workspace.OutputDirectory = Path.Combine(workspace.RootDirectory, "output");
                Directory.CreateDirectory(workspace.InputDirectory);
                foreach (string file in Directory.GetFiles(source, "*"))
                {
                    string name = Path.GetFileName(file);
                    if (!includeSymbols && string.Equals(name, Path.ChangeExtension(TargetAssemblyName, "pdb"),
                        StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    File.Copy(file, Path.Combine(workspace.InputDirectory, name), true);
                }

                return workspace;
            }

            /// <summary>
            /// Rewrites the staged assembly and returns everything the run logged.
            /// </summary>
            internal string Rewrite(Action<RewritingOptions> configure = null)
            {
                var options = RewritingOptions.Create();
                options.AssembliesDirectory = this.InputDirectory;
                options.OutputDirectory = this.OutputDirectory;
                options.AssemblyPaths = new HashSet<string>() { this.InputAssemblyPath };
                configure?.Invoke(options);

                var configuration = Configuration.Create().WithVerbosityEnabled(VerbosityLevel.Info);
                using var logWriter = new MemoryLogWriter(configuration);
                RewritingEngine.Run(options, configuration, logWriter, new Profiler());
                return logWriter.GetObservedMessages();
            }

            /// <summary>
            /// Creates an empty directory for resolution to search, and returns its path.
            /// </summary>
            internal string AddSearchDirectory()
            {
                string path = Path.Combine(this.RootDirectory, "search");
                Directory.CreateDirectory(path);
                return path;
            }

            /// <summary>
            /// Restores the symbol file of the staged assembly, and returns its path.
            /// </summary>
            internal string AddInputSymbolFile()
            {
                string source = Path.ChangeExtension(
                    Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                        TargetAssemblyName), "pdb");
                string target = Path.ChangeExtension(this.InputAssemblyPath, "pdb");
                File.Copy(source, target, true);
                return target;
            }

            /// <summary>
            /// Puts a file named after one of the target's references into the input directory, where
            /// dependency discovery probes for it, and returns its path.
            /// </summary>
            internal string AddInputCopyOfReferencedAssembly()
            {
                string missing = Assembly.GetExecutingAssembly().GetReferencedAssemblies()
                    .Select(reference => Path.Combine(this.InputDirectory, reference.Name + ".dll"))
                    .FirstOrDefault(path => !File.Exists(path));
                if (missing is null)
                {
                    return null;
                }

                // The content only has to be a readable assembly: what changes is that a file with
                // this name is now there to be found.
                File.Copy(this.InputAssemblyPath, missing, true);
                return missing;
            }

            public void Dispose()
            {
                try
                {
                    Directory.Delete(this.RootDirectory, true);
                }
                catch (Exception)
                {
                    // A leftover temporary directory is not worth failing a test over.
                }
            }
        }
    }
}

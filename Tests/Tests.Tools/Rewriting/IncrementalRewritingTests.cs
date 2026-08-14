// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Coyote.IO;
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
        public void TestDeletedMirroredFileIsRemovedButUnownedOutputSurvives()
        {
            using var workspace = Workspace.Create();
            string ownedName = "mirrored.txt";
            string ownedInput = Path.Combine(workspace.InputDirectory, ownedName);
            string ownedOutput = Path.Combine(workspace.OutputDirectory, ownedName);
            string unownedOutput = Path.Combine(workspace.OutputDirectory, "custom.txt");
            File.WriteAllText(ownedInput, "mirrored");
            workspace.Rewrite();
            File.WriteAllText(unownedOutput, "custom");

            File.Delete(ownedInput);
            workspace.Rewrite();

            Assert.False(File.Exists(ownedOutput));
            Assert.Equal("custom", File.ReadAllText(unownedOutput));
        }

        [Fact(Timeout = 60000)]
        public void TestDisablingDiffGenerationRemovesTheOwnedArtifact()
        {
            using var workspace = Workspace.Create();
            workspace.Rewrite(options => options.IsDiffingAssemblyContents = true);
            string diffPath = Path.ChangeExtension(workspace.OutputAssemblyPath, ".diff.json");
            Assert.True(File.Exists(diffPath), "File not found: " + diffPath);

            Assert.Contains(RewroteMessage, workspace.Rewrite(
                options => options.IsDiffingAssemblyContents = false));

            Assert.False(File.Exists(diffPath));
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

            // The version named here is the one this assembly was built against, so it cannot be
            // written out literally: a version substituted for itself leaves the file byte for byte
            // as it was, the cache is right to report it unchanged, and the test fails while looking
            // like the cache missed something. Bumping whatever is there keeps this about the cache
            // on every framework rather than on the one whose version the test happened to name.
            string original = File.ReadAllText(runtimeConfig);
            string changed = Regex.Replace(original, "(\"version\":\\s*\")(\\d+)\\.(\\d+)\\.(\\d+)(\")",
                match => string.Concat(match.Groups[1].Value, match.Groups[2].Value, ".",
                    match.Groups[3].Value, ".",
                    (int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) + 1)
                        .ToString(CultureInfo.InvariantCulture),
                    match.Groups[5].Value));
            Assert.NotEqual(original, changed);

            File.WriteAllText(runtimeConfig, changed);
            Assert.Contains(RewroteMessage, workspace.Rewrite());
        }

        [Fact(Timeout = 60000)]
        public void TestUpToDateRunKeepsTheWrittenDiff()
        {
            // An input directory that was rewritten in place holds debug artifacts under the same
            // names as the ones this output directory produces, and the copy that mirrors the input
            // would otherwise put those over them. Their content fingerprints also prevent a stale
            // equal-length artifact from being taken for the real thing on every run after.
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
        public void TestInputChangedAfterValidationIsNotProtectedFromRewriting()
        {
            using var workspace = Workspace.Create();
            workspace.Rewrite();
            string runtimeConfig = Path.ChangeExtension(
                workspace.InputAssemblyPath, ".runtimeconfig.json");
            bool mutated = false;
            var fileSystem = new CallbackFileSystem(HostFileSystem.Instance,
                (directory, searchPattern) =>
                {
                    if (!mutated &&
                        string.Equals(directory, workspace.InputDirectory,
                            StringComparison.OrdinalIgnoreCase) &&
                        searchPattern == "*")
                    {
                        byte[] contents = File.ReadAllBytes(runtimeConfig);
                        int whitespace = Array.IndexOf(contents, (byte)' ');
                        Assert.True(whitespace >= 0, "The staged runtime configuration has no space to replace.");
                        contents[whitespace] = (byte)'\t';
                        File.WriteAllBytes(runtimeConfig, contents);
                        mutated = true;
                    }
                });

            string log = workspace.Rewrite(fileSystem);

            Assert.True(mutated);
            Assert.Contains(RewroteMessage, log);
            Assert.NotEqual(
                new FileInfo(workspace.InputAssemblyPath).Length,
                new FileInfo(workspace.OutputAssemblyPath).Length);
        }

        [Fact(Timeout = 60000)]
        public void TestFileDisappearingFromMirrorInventoryIsRetried()
        {
            using var workspace = Workspace.Create();
            workspace.Rewrite();
            string transientInput = Path.Combine(workspace.InputDirectory, "transient.txt");
            string transientOutput = Path.Combine(workspace.OutputDirectory, "transient.txt");
            File.WriteAllText(transientInput, "short lived");
            bool removed = false;
            var fileSystem = new CallbackFileSystem(HostFileSystem.Instance, beforeCopyFile:
                (source, _, __) =>
                {
                    if (!removed &&
                        string.Equals(source, transientInput, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(transientInput);
                        removed = true;
                    }
                });

            string log = workspace.Rewrite(fileSystem);

            Assert.True(removed);
            Assert.Contains(UpToDateMessage, log);
            Assert.False(File.Exists(transientOutput));
        }

        [Fact(Timeout = 60000)]
        public void TestRetryRestoresPreexistingOutputOverwrittenByVanishedSource()
        {
            using var workspace = Workspace.Create();
            workspace.Rewrite();
            string transientInput = Path.Combine(workspace.InputDirectory, "transient.txt");
            string transientOutput = Path.Combine(workspace.OutputDirectory, "transient.txt");
            File.WriteAllText(transientInput, "source-owned");
            File.WriteAllText(transientOutput, "user-owned");
            bool copied = false;
            bool removed = false;
            var fileSystem = new CallbackFileSystem(HostFileSystem.Instance,
                beforeGetFiles: (directory, _) =>
                {
                    if (copied && !removed &&
                        string.Equals(directory, workspace.InputDirectory,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(transientInput);
                        removed = true;
                    }
                },
                beforeCopyFile: (source, _, __) =>
                {
                    if (string.Equals(source, transientInput, StringComparison.OrdinalIgnoreCase))
                    {
                        copied = true;
                    }
                });

            string log = workspace.Rewrite(fileSystem);

            Assert.True(removed);
            Assert.Contains(UpToDateMessage, log);
            Assert.Equal("user-owned", File.ReadAllText(transientOutput));
            Assert.Empty(Directory.GetDirectories(
                Path.GetDirectoryName(workspace.OutputDirectory),
                Path.GetFileName(workspace.OutputDirectory) + ".mirror-backup-*"));
        }

        [Fact(Timeout = 60000)]
        public void TestFailedMirrorRollbackRemovesRecoveryJournal()
        {
            using var workspace = Workspace.Create();
            workspace.Rewrite();
            string transientInput = Path.Combine(workspace.InputDirectory, "transient.txt");
            File.WriteAllText(transientInput, "will not copy");
            var fileSystem = new CallbackFileSystem(HostFileSystem.Instance,
                beforeCopyFile: (source, _, __) =>
                {
                    if (string.Equals(source, transientInput, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("injected mirror failure");
                    }
                });

            Assert.Throws<IOException>(() => workspace.Rewrite(fileSystem));
            Assert.Empty(Directory.GetDirectories(
                Path.GetDirectoryName(workspace.OutputDirectory),
                Path.GetFileName(workspace.OutputDirectory) + ".mirror-backup-*"));
        }

        [Theory(Timeout = 120000)]
        [InlineData(RewritingCache.ManifestFileName)]
        [InlineData(RewritingOutputLedger.ManifestFileName)]
        [Trait("Category", "RewritingRemediation")]
        public void TestPublicationFailureRestoresTheEntireOutput(string failedManifestName)
        {
            using var workspace = Workspace.Create();
            workspace.Rewrite();
            Dictionary<string, byte[]> before = CaptureFiles(workspace.OutputDirectory);
            string failedTarget = Path.Combine(workspace.OutputDirectory, failedManifestName);
            var fileSystem = new CallbackFileSystem(HostFileSystem.Instance,
                beforeMoveFile: (_, target) =>
                {
                    if (string.Equals(target, failedTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("injected publication failure");
                    }
                },
                beforeReplaceFile: (_, target, __) =>
                {
                    if (string.Equals(target, failedTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("injected publication failure");
                    }
                });

            Assert.Throws<IOException>(() => workspace.Rewrite(fileSystem,
                options => options.IsDataRaceCheckingEnabled = !options.IsDataRaceCheckingEnabled));

            Dictionary<string, byte[]> after = CaptureFiles(workspace.OutputDirectory);
            Assert.Equal(before.Keys.OrderBy(path => path), after.Keys.OrderBy(path => path));
            foreach (string path in before.Keys)
            {
                Assert.Equal(before[path], after[path]);
            }

            string parent = Path.GetDirectoryName(workspace.OutputDirectory);
            string outputName = Path.GetFileName(workspace.OutputDirectory);
            Assert.Empty(Directory.GetDirectories(parent, outputName + ".mirror-backup-*"));
            Assert.Empty(Directory.GetDirectories(parent,
                outputName + RewritingInputSnapshot.DirectoryMarker + "*"));
            Assert.True(File.Exists(workspace.OutputDirectory + RewritingOutputLock.FileSuffix));
        }

        [Fact(Timeout = 60000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestLoadedAssemblyBatchIsDisposedWhenPostLoadPhaseFails()
        {
            using var workspace = Workspace.Create();
            IReadOnlyList<AssemblyInfo> loaded = null;
            var injected = new IOException("injected post-load failure");

            IOException error = Assert.Throws<IOException>(() => workspace.Rewrite(
                HostFileSystem.Instance, onAssembliesLoaded: assemblies =>
                {
                    loaded = assemblies;
                    throw injected;
                }));

            Assert.Same(injected, error);
            Assert.NotNull(loaded);
            Assert.NotEmpty(loaded);
            Assert.All(loaded, assembly => Assert.True(assembly.IsDisposedForTesting));
            Assert.False(Directory.Exists(workspace.OutputDirectory));
            string parent = Path.GetDirectoryName(workspace.OutputDirectory);
            string outputName = Path.GetFileName(workspace.OutputDirectory);
            Assert.Empty(Directory.GetDirectories(parent, outputName + ".mirror-backup-*"));
            Assert.Empty(Directory.GetDirectories(parent,
                outputName + RewritingInputSnapshot.DirectoryMarker + "*"));
        }

        [Fact(Timeout = 120000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestSeparateOutputRejectsAssemblyDriftBeforePublication()
        {
            using var workspace = Workspace.Create();
            workspace.Rewrite();
            Dictionary<string, byte[]> before = CaptureFiles(workspace.OutputDirectory);
            string ledgerPath = Path.Combine(
                workspace.OutputDirectory, RewritingOutputLedger.ManifestFileName);
            byte[] external = File.ReadAllBytes(workspace.InputAssemblyPath);
            external[external.Length / 2] ^= 0x5a;
            bool mutated = false;
            var fileSystem = new CallbackFileSystem(HostFileSystem.Instance, beforeCopyFile:
                (source, _, __) =>
                {
                    if (!mutated && string.Equals(source, ledgerPath, StringComparison.OrdinalIgnoreCase))
                    {
                        File.WriteAllBytes(workspace.InputAssemblyPath, external);
                        mutated = true;
                    }
                });

            IOException error = Assert.Throws<IOException>(() => workspace.Rewrite(fileSystem,
                options => options.IsDataRaceCheckingEnabled = !options.IsDataRaceCheckingEnabled));

            Assert.True(mutated);
            Assert.Contains("changed after its rewrite snapshot was created", error.Message);
            Assert.Equal(external, File.ReadAllBytes(workspace.InputAssemblyPath));
            AssertFileSetEquals(before, CaptureFiles(workspace.OutputDirectory));
        }

        [Fact(Timeout = 120000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestSeparateOutputRejectsMirroredFileDriftBeforePublication()
        {
            using var workspace = Workspace.Create();
            workspace.Rewrite();
            Dictionary<string, byte[]> before = CaptureFiles(workspace.OutputDirectory);
            string ledgerPath = Path.Combine(
                workspace.OutputDirectory, RewritingOutputLedger.ManifestFileName);
            string runtimeConfig = Path.ChangeExtension(
                workspace.InputAssemblyPath, ".runtimeconfig.json");
            byte[] external = File.ReadAllBytes(runtimeConfig);
            external[Array.IndexOf(external, (byte)' ')] = (byte)'\t';
            bool mutated = false;
            var fileSystem = new CallbackFileSystem(HostFileSystem.Instance, beforeCopyFile:
                (source, _, __) =>
                {
                    if (!mutated && string.Equals(source, ledgerPath, StringComparison.OrdinalIgnoreCase))
                    {
                        File.WriteAllBytes(runtimeConfig, external);
                        mutated = true;
                    }
                });

            IOException error = Assert.Throws<IOException>(() => workspace.Rewrite(fileSystem,
                options => options.IsDataRaceCheckingEnabled = !options.IsDataRaceCheckingEnabled));

            Assert.True(mutated);
            Assert.Contains("changed after its rewrite snapshot was created", error.Message);
            Assert.Equal(external, File.ReadAllBytes(runtimeConfig));
            AssertFileSetEquals(before, CaptureFiles(workspace.OutputDirectory));
        }

        private static Dictionary<string, byte[]> CaptureFiles(string directory) =>
            Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(directory, path).Replace('\\', '/'),
                    File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

        private static void AssertFileSetEquals(
            IReadOnlyDictionary<string, byte[]> expected,
            IReadOnlyDictionary<string, byte[]> actual)
        {
            Assert.Equal(expected.Keys.OrderBy(path => path), actual.Keys.OrderBy(path => path));
            foreach (string path in expected.Keys)
            {
                Assert.Equal(expected[path], actual[path]);
            }
        }

        [Fact(Timeout = 60000)]
        public void TestCaseSensitivityProbeMatchesTheFileSystem()
        {
            // Whether two spellings name one file is a property of the file system rather than of the
            // platform: macOS ships case-insensitive but can be formatted otherwise. So the probe is
            // checked against what this file system actually does, asked here independently. This is
            // the one question an injected file system cannot be asked, which is why it is asked of
            // the real one -- and of the very member production consults, rather than of a second
            // spelling of the same rule kept alive for this test.
            using var workspace = Workspace.Create();
            string directory = Path.Combine(workspace.InputDirectory, "CasedDirectory");
            Directory.CreateDirectory(directory);

            // Only the leaf is respelled, and the same directory is then given to both halves, so the
            // file system and the probe are asked about one path rather than about two that happen to
            // differ in the same way.
            string recased = Path.Combine(workspace.InputDirectory, "CASEDDIRECTORY");
            bool isCaseInsensitive = Directory.Exists(recased);

            Assert.Equal(isCaseInsensitive, HostFileSystem.Instance.IsCaseInsensitive(directory));
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
                foreach (string name in GetStagedFileNames(source, includeSymbols))
                {
                    File.Copy(Path.Combine(source, name), Path.Combine(workspace.InputDirectory, name), true);
                }

                return workspace;
            }

            /// <summary>
            /// Returns the names of the files to stage for a rewriting run.
            /// </summary>
            /// <remarks>
            /// This used to copy the entire build output, which is seven megabytes across forty nine
            /// files, and every test here stages a fresh one. Most of that is the test host, the
            /// xunit runner and the test platform: none of it is read while rewriting one assembly,
            /// and none of it is what any test here is about.
            ///
            /// What is staged instead is what rewriting actually reaches for -- the assembly itself,
            /// the files beside it that are named after it, and the assemblies it references that
            /// exist here to be found. The references that are not here are the framework ones, and
            /// they stay missing on purpose: resolution finds those through the shared framework
            /// directories, and <see cref="AddInputCopyOfReferencedAssembly"/> depends on there
            /// still being a referenced assembly absent from this directory.
            /// </remarks>
            private static IEnumerable<string> GetStagedFileNames(string source, bool includeSymbols)
            {
                var names = new List<string>()
                {
                    TargetAssemblyName,
                    Path.ChangeExtension(TargetAssemblyName, "runtimeconfig.json"),
                    Path.ChangeExtension(TargetAssemblyName, "deps.json")
                };

                if (includeSymbols)
                {
                    names.Add(Path.ChangeExtension(TargetAssemblyName, "pdb"));
                }

                foreach (var reference in Assembly.GetExecutingAssembly().GetReferencedAssemblies())
                {
                    names.Add(reference.Name + ".dll");
                    names.Add(reference.Name + ".pdb");
                }

                return names.Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(name => File.Exists(Path.Combine(source, name)));
            }

            /// <summary>
            /// Rewrites the staged assembly and returns everything the run logged.
            /// </summary>
            internal string Rewrite(Action<RewritingOptions> configure = null)
                => this.Rewrite(HostFileSystem.Instance, configure);

            internal string Rewrite(IFileSystem fileSystem, Action<RewritingOptions> configure = null,
                Action<IReadOnlyList<AssemblyInfo>> onAssembliesLoaded = null)
            {
                var options = RewritingOptions.Create();
                options.AssembliesDirectory = this.InputDirectory;
                options.OutputDirectory = this.OutputDirectory;
                options.AssemblyPaths = new HashSet<string>() { this.InputAssemblyPath };
                configure?.Invoke(options);

                var configuration = Configuration.Create().WithVerbosityEnabled(VerbosityLevel.Info);
                using var logWriter = new MemoryLogWriter(configuration);
                RewritingEngine.Run(options, configuration, logWriter, new Profiler(),
                    fileSystem, Environment.GetEnvironmentVariable, onAssembliesLoaded);
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

        /// <summary>
        /// Delegates to the host file system and exposes the start of mirroring to one regression.
        /// </summary>
        private sealed class CallbackFileSystem : IFileSystem
        {
            private readonly IFileSystem Inner;
            private readonly Action<string, string> BeforeGetFiles;
            private readonly Action<string, string, bool> BeforeCopyFile;
            private readonly Action<string, string> BeforeMoveFile;
            private readonly Action<string, string, string> BeforeReplaceFile;

            internal CallbackFileSystem(IFileSystem inner,
                Action<string, string> beforeGetFiles = null,
                Action<string, string, bool> beforeCopyFile = null,
                Action<string, string> beforeMoveFile = null,
                Action<string, string, string> beforeReplaceFile = null)
            {
                this.Inner = inner;
                this.BeforeGetFiles = beforeGetFiles;
                this.BeforeCopyFile = beforeCopyFile;
                this.BeforeMoveFile = beforeMoveFile;
                this.BeforeReplaceFile = beforeReplaceFile;
            }

            public bool FileExists(string path) => this.Inner.FileExists(path);

            public bool DirectoryExists(string path) => this.Inner.DirectoryExists(path);

            public IFileEntry GetFile(string path) => this.Inner.GetFile(path);

            public string ReadAllText(string path) => this.Inner.ReadAllText(path);

            public void WriteAllText(string path, string contents) =>
                this.Inner.WriteAllText(path, contents);

            public Stream OpenRead(string path, FileReadSharing sharing) =>
                this.Inner.OpenRead(path, sharing);

            public void CopyFile(string sourcePath, string targetPath, bool overwrite)
            {
                this.BeforeCopyFile?.Invoke(sourcePath, targetPath, overwrite);
                this.Inner.CopyFile(sourcePath, targetPath, overwrite);
            }

            public void MoveFile(string sourcePath, string targetPath)
            {
                this.BeforeMoveFile?.Invoke(sourcePath, targetPath);
                this.Inner.MoveFile(sourcePath, targetPath);
            }

            public void ReplaceFile(string sourcePath, string targetPath, string backupPath)
            {
                this.BeforeReplaceFile?.Invoke(sourcePath, targetPath, backupPath);
                this.Inner.ReplaceFile(sourcePath, targetPath, backupPath);
            }

            public void DeleteFile(string path) => this.Inner.DeleteFile(path);

            public void CreateDirectory(string path) => this.Inner.CreateDirectory(path);

            public void DeleteDirectory(string path, bool recursive) =>
                this.Inner.DeleteDirectory(path, recursive);

            public string[] GetFiles(string directory, string searchPattern)
            {
                this.BeforeGetFiles?.Invoke(directory, searchPattern);
                return this.Inner.GetFiles(directory, searchPattern);
            }

            /// <remarks>
            /// The same hook as <see cref="GetFiles"/>, because what a caller wants to interpose on
            /// is a directory being listed and not which of the two calls does it. The mirror takes
            /// its inventory through this one, so a hook that fired only on the other would silently
            /// stop describing the thing its tests are named after.
            /// </remarks>
            public IReadOnlyList<IFileEntry> GetFileEntries(string directory, string searchPattern)
            {
                this.BeforeGetFiles?.Invoke(directory, searchPattern);
                return this.Inner.GetFileEntries(directory, searchPattern);
            }

            public string[] GetDirectories(string directory, string searchPattern, bool recursive) =>
                this.Inner.GetDirectories(directory, searchPattern, recursive);

            public bool IsCaseInsensitive(string directory) =>
                this.Inner.IsCaseInsensitive(directory);
        }
    }
}

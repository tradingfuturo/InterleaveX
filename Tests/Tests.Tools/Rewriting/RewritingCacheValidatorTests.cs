// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Coyote.IO;
using Microsoft.Coyote.Rewriting;
using Microsoft.Coyote.Tests.Common.IO;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Tools.Tests
{
    /// <summary>
    /// Tests the decision that lets a rewriting run skip work it has already done.
    /// </summary>
    /// <remarks>
    /// A wrong answer here is not a slow build but a silent one: the suite would run against an
    /// assembly that was never instrumented, and nothing downstream detects it. So each of the ways
    /// a recorded run can stop describing what is on disk is exercised on its own, against a file
    /// system held in memory.
    ///
    /// The manifest each test starts from is captured from that file system rather than written out
    /// by hand, so it is up to date by construction and every test below says exactly one thing:
    /// here is the single change that must stop it being accepted. Reaching these cases used to mean
    /// copying a build output directory into a temporary location and running the whole engine over
    /// it, at roughly ten seconds a case, which is why most of them had no test at all.
    /// </remarks>
    public class RewritingCacheValidatorTests : BaseToolsTest
    {
        public RewritingCacheValidatorTests(ITestOutputHelper output)
            : base(output)
        {
        }

        private const int SchemaVersion = 5;
        private const string RewriterVersion = "1.2.3.4";
        private const string RewriterModuleId = "11111111-1111-1111-1111-111111111111";
        private const string ConfigurationHash = "configuration-hash";

        /// <summary>
        /// An absolute path that names nothing on the machine running this. Fully qualified, because
        /// the validator normalizes what it is given and a relative path would be resolved against
        /// wherever the test runner happened to be started.
        /// </summary>
        private static readonly string Root =
            Path.GetFullPath(Path.DirectorySeparatorChar + "coyote-cache-tests");

        private static string In(params string[] parts) =>
            Path.Combine(new[] { Root, "input" }.Concat(parts).ToArray());

        private static string Out(params string[] parts) =>
            Path.Combine(new[] { Root, "output" }.Concat(parts).ToArray());

        private static string InputDirectory => Path.Combine(Root, "input");

        private static string OutputDirectory => Path.Combine(Root, "output");

        /// <summary>
        /// The directory holding every installed version of a shared framework, as the roll-forward
        /// that picks one of them sees it.
        /// </summary>
        private static string FrameworkDirectory => Path.Combine(Root, "dotnet", "shared", "Framework");

        /// <summary>
        /// A file system holding one assembly that has been rewritten, and the manifest describing it.
        /// </summary>
        private sealed class Fixture
        {
            internal InMemoryFileSystem FileSystem { get; set; }

            internal RewritingCacheValidator Validator { get; set; }

            internal CacheManifest Manifest { get; set; }

            /// <summary>
            /// The only entry, for tests that change one thing about it.
            /// </summary>
            internal CacheEntry Entry => this.Manifest.Entries[0];

            internal bool IsCurrent(out string reason) =>
                this.Validator.IsManifestCurrent(this.Manifest, out reason);

            internal bool IsCurrent() => this.IsCurrent(out _);
        }

        /// <summary>
        /// Returns a validator reading the specified file system, expecting the run the fixture below
        /// describes.
        /// </summary>
        private static RewritingCacheValidator CreateValidator(
            InMemoryFileSystem fileSystem, bool withArtifacts = false) =>
            new RewritingCacheValidator(fileSystem, new RewritingCacheExpectation(
                SchemaVersion, RewriterVersion, RewriterModuleId, ConfigurationHash,
                InputDirectory, OutputDirectory, new[] { In("App.dll") }, false,
                isDiffingAssemblyContents: withArtifacts));

        /// <summary>
        /// Builds a run that is up to date, so that each test can make exactly one thing stale.
        /// </summary>
        private static Fixture CreateUpToDate(bool isCaseInsensitive = true, bool withArtifacts = false)
        {
            var fileSystem = new InMemoryFileSystem(isCaseInsensitive)
                .WithFile(In("App.dll"), "the original assembly")
                .WithFile(In("App.pdb"), "the original symbols")
                .WithFile(In("App.runtimeconfig.json"), "{ }")
                .WithFile(In("Dependency.dll"), "a dependency")
                .WithFile(Out("App.dll"), "the rewritten assembly")
                .WithFile(Out("App.pdb"), "the rewritten symbols")
                .WithDirectory(Out())
                .WithDirectory(Path.Combine(FrameworkDirectory, "8.0.10"))
                .WithDirectory(Path.Combine(FrameworkDirectory, "8.0.2"));

            var validator = CreateValidator(fileSystem, withArtifacts);

            var artifacts = new List<CacheFile>();
            if (withArtifacts)
            {
                fileSystem.WithFile(Out("App.diff.json"), "[ ]");
                artifacts.Add(validator.CaptureFile(Out("App.diff.json"), true));
            }

            return new Fixture()
            {
                FileSystem = fileSystem,
                Validator = validator,
                Manifest = CreateManifest(validator, artifacts)
            };
        }

        /// <summary>
        /// Describes the run <see cref="CreateUpToDate"/> stages, captured from the file system
        /// rather than written out, so that a test changes one thing and not two.
        /// </summary>
        private static CacheManifest CreateManifest(
            RewritingCacheValidator validator, List<CacheFile> artifacts = null) =>
            new CacheManifest()
            {
                SchemaVersion = SchemaVersion,
                FingerprintAlgorithm = RewritingCacheValidator.FingerprintAlgorithm,
                RewriterVersion = RewriterVersion,
                RewriterModuleId = RewriterModuleId,
                AssembliesDirectory = RewritingCacheValidator.NormalizeDirectory(InputDirectory),
                OutputDirectory = RewritingCacheValidator.NormalizeDirectory(OutputDirectory),
                ConfigurationHash = ConfigurationHash,
                RequestedInputs = new List<string>() { In("App.dll") },
                RewriteInputs = new List<string>() { In("App.dll") },
                ResolvedModules = new List<CacheFile>()
                {
                    validator.CaptureFile(In("Dependency.dll"), true)
                },
                DependencySearchDirectories = new List<CacheDirectory>()
                {
                    validator.CaptureDirectory(InputDirectory, hashContent: true)
                },
                FrameworkInventories = new List<CacheDirectoryListing>()
                {
                    validator.CaptureDirectoryNames(FrameworkDirectory)
                },
                Entries = new List<CacheEntry>()
                {
                    new CacheEntry()
                    {
                        Name = "App.dll",
                        Input = validator.CaptureFile(In("App.dll"), true),
                        Output = validator.CaptureFile(Out("App.dll"), true),
                        Symbols = validator.CaptureFile(In("App.pdb"), true),
                        OutputSymbols = validator.CaptureFile(Out("App.pdb"), true),
                        RuntimeConfig = validator.CaptureFile(In("App.runtimeconfig.json"), true),
                        ReferenceNames = new List<string>() { "Dependency", "Absent" },
                        PresentReferences = new List<string>() { "Dependency" },
                        Artifacts = artifacts ?? new List<CacheFile>(),
                        ThreadStaticFields = new List<string>()
                    }
                }
            };

        [Fact(Timeout = 5000)]
        public void TestNothingChangedIsUpToDate()
        {
            // The baseline every other test here depends on: if this were not accepted, each of them
            // would pass for the wrong reason.
            var fixture = CreateUpToDate();
            Assert.True(fixture.IsCurrent(out string reason), reason);
        }

        [Fact(Timeout = 5000)]
        public void TestThereIsNoManifest()
        {
            var fixture = CreateUpToDate();
            Assert.False(fixture.Validator.IsManifestCurrent(null, out string reason));
            Assert.Contains("none", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestManifestFromAnOlderFormat()
        {
            var fixture = CreateUpToDate();
            fixture.Manifest.SchemaVersion = SchemaVersion - 1;
            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("older format", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestManifestUsingAnUnknownFingerprint()
        {
            var fixture = CreateUpToDate();
            fixture.Manifest.FingerprintAlgorithm = "sha256";
            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("unsupported content fingerprint", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestManifestFromADifferentRewriterVersion()
        {
            var fixture = CreateUpToDate();
            fixture.Manifest.RewriterVersion = "9.9.9.9";
            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("different build of the rewriter", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestManifestFromADifferentRewriterBuildOfTheSameVersion()
        {
            // The version changes rarely, so a locally rebuilt rewriter carries the same one while
            // emitting different IL. This is the half of that check that the version cannot make.
            var fixture = CreateUpToDate();
            fixture.Manifest.RewriterModuleId = "22222222-2222-2222-2222-222222222222";
            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("different build of the rewriter", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestChangedConfiguration()
        {
            var fixture = CreateUpToDate();
            fixture.Manifest.ConfigurationHash = "something else";
            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("configuration changed", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestManifestWrittenForAnotherDirectory()
        {
            // An in-place run leaves a manifest in the input directory, and the copy that mirrors the
            // input into the output brings it along. Without this it would describe the wrong run.
            var fixture = CreateUpToDate();
            fixture.Manifest.OutputDirectory =
                RewritingCacheValidator.NormalizeDirectory(Path.Combine(Root, "elsewhere"));
            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("different directory", reason);
        }

        [Theory(Timeout = 5000)]
        [InlineData("entries")]
        [InlineData("modules")]
        [InlineData("directories")]
        [InlineData("requested")]
        [InlineData("closure")]
        public void TestIncompleteManifest(string missing)
        {
            var fixture = CreateUpToDate();
            if (missing is "entries")
            {
                fixture.Manifest.Entries = null;
            }
            else if (missing is "modules")
            {
                fixture.Manifest.ResolvedModules = null;
            }
            else if (missing is "directories")
            {
                fixture.Manifest.DependencySearchDirectories = null;
            }
            else if (missing is "requested")
            {
                fixture.Manifest.RequestedInputs = null;
            }
            else
            {
                fixture.Manifest.RewriteInputs = null;
            }

            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("incomplete", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestIncompleteEntry()
        {
            var fixture = CreateUpToDate();
            fixture.Entry.RuntimeConfig = null;
            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("entry is incomplete", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestAssemblyRecordedTwice()
        {
            var fixture = CreateUpToDate();
            fixture.Manifest.Entries.Add(fixture.Entry);
            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("recorded more than once", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestOutputPathChanged()
        {
            var fixture = CreateUpToDate();
            fixture.Entry.Output = fixture.Validator.CaptureFile(Out("Renamed.dll"), true);
            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("output path", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestManifestDoesNotCoverEveryRequestedAssembly()
        {
            // The manifest is internally consistent; what is wrong is that this run was asked to
            // rewrite something the manifest says nothing about.
            var fileSystem = new InMemoryFileSystem().WithFile(In("Other.dll"), "another assembly");
            var fixture = CreateUpToDate();
            var validator = new RewritingCacheValidator(fixture.FileSystem, new RewritingCacheExpectation(
                SchemaVersion, RewriterVersion, RewriterModuleId, ConfigurationHash,
                InputDirectory, OutputDirectory, new[] { In("App.dll"), In("Other.dll") }, false));

            Assert.False(validator.IsManifestCurrent(fixture.Manifest, out string reason));
            Assert.Contains("different set of requested assemblies", reason);
            Assert.NotNull(fileSystem);
        }

        [Fact(Timeout = 5000)]
        public void TestClosureMustExactlyMatchItsEntries()
        {
            var fixture = CreateUpToDate();
            fixture.Manifest.RewriteInputs.Add(In("Dependency.dll"));

            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("exactly cover", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestClosureRejectsAnUnreachableExtraEntry()
        {
            var fixture = CreateUpToDate();
            fixture.FileSystem.WithFile(Out("Dependency.dll"), "rewritten dependency");
            fixture.Manifest.RewriteInputs.Add(In("Dependency.dll"));
            fixture.Manifest.Entries.Add(new CacheEntry()
            {
                Name = "Dependency.dll",
                Input = fixture.Validator.CaptureFile(In("Dependency.dll"), true),
                Output = fixture.Validator.CaptureFile(Out("Dependency.dll"), true),
                Symbols = fixture.Validator.CaptureFile(In("Dependency.pdb"), true),
                OutputSymbols = fixture.Validator.CaptureFile(Out("Dependency.pdb"), true),
                RuntimeConfig = fixture.Validator.CaptureFile(In("Dependency.runtimeconfig.json"), true),
                ReferenceNames = new List<string>(),
                PresentReferences = new List<string>(),
                Artifacts = new List<CacheFile>(),
                ThreadStaticFields = new List<string>()
            });
            fixture.Entry.PresentReferences.Clear();

            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("unreachable", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestInputAssemblyChanged()
        {
            var fixture = CreateUpToDate();
            fixture.FileSystem.WriteAllText(In("App.dll"), "a rebuilt assembly");
            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("'App.dll' changed", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestInputChangedWithoutChangingLength()
        {
            // Length is checked first as a cheap rejection, so a change that keeps it has to be
            // caught by the hash. Without this, the only thing proving the hash is consulted at all
            // would be a test that also changes the length.
            var fixture = CreateUpToDate();
            string original = fixture.FileSystem.GetContents(In("App.dll"));
            string changed = new string(original.Reverse().ToArray());
            Assert.Equal(original.Length, changed.Length);
            Assert.NotEqual(original, changed);

            fixture.FileSystem.WriteAllText(In("App.dll"), changed);
            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("'App.dll' changed", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestRewrittenOutputDeleted()
        {
            var fixture = CreateUpToDate();
            fixture.FileSystem.DeleteFile(Out("App.dll"));
            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("the rewritten 'App.dll' changed", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestSymbolFileDisappeared()
        {
            var fixture = CreateUpToDate();
            fixture.FileSystem.DeleteFile(In("App.pdb"));
            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("symbols of 'App.dll'", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestSymbolFileAppeared()
        {
            // Whether symbols are read decides whether they are written, so one appearing beside the
            // input changes what a rewrite would produce even though nothing recorded was touched.
            var fixture = CreateUpToDate();
            fixture.FileSystem.DeleteFile(In("App.pdb"));
            fixture.Entry.Symbols = fixture.Validator.CaptureFile(In("App.pdb"), true);
            Assert.True(fixture.IsCurrent(out string reason), reason);

            fixture.FileSystem.WithFile(In("App.pdb"), "symbols that were not there before");
            Assert.False(fixture.IsCurrent(out reason));
            Assert.Contains("symbols of 'App.dll'", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestRuntimeConfigurationChanged()
        {
            // The runtime config names the shared frameworks resolution falls back to, so editing it
            // points the rewriter at different implementation assemblies without touching a single
            // file that anything else here records.
            var fixture = CreateUpToDate();
            fixture.FileSystem.WriteAllText(In("App.runtimeconfig.json"), "{ \"changed\": true }");
            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("runtime configuration of 'App.dll'", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestDependencyDisappeared()
        {
            var fixture = CreateUpToDate();
            fixture.FileSystem.DeleteFile(In("Dependency.dll"));
            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("'Dependency' of 'App.dll' appeared or disappeared", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestDependencyAppeared()
        {
            // Which assemblies get rewritten is decided by probing the input directory per reference,
            // so one appearing changes the set while every recorded file stays untouched.
            var fixture = CreateUpToDate();
            fixture.FileSystem.WithFile(In("Absent.dll"), "an assembly that was not there before");
            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("'Absent' of 'App.dll' appeared or disappeared", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestResolvedModuleChanged()
        {
            var fixture = CreateUpToDate();
            fixture.Manifest.Entries[0].ReferenceNames = new List<string>();
            fixture.Manifest.Entries[0].PresentReferences = new List<string>();
            fixture.FileSystem.WriteAllText(In("Dependency.dll"), "a rebuilt dependency");
            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("resolved assembly", reason);
        }

        [Theory(Timeout = 5000)]
        [InlineData("short")]
        [InlineData("0123456789abcdef0123456789abcdeG")]
        public void TestMalformedResolvedModuleFingerprint(string fingerprint)
        {
            var fixture = CreateUpToDate();
            fixture.Manifest.ResolvedModules[0].Fingerprint = fingerprint;

            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("resolved assembly", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestDuplicateResolvedModuleIsRejected()
        {
            var fixture = CreateUpToDate();
            fixture.Manifest.ResolvedModules.Add(fixture.Manifest.ResolvedModules[0]);

            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("resolved assembly", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestSearchDirectoryOffersSomethingNew()
        {
            // Nothing the last run read has changed. What changed is that a resolution which
            // previously went elsewhere, or failed, would now find this.
            var fixture = CreateUpToDate();
            fixture.FileSystem.WithFile(In("Newcomer.dll"), "an assembly nobody asked for");
            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("search directory changed", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestSearchDirectoryDisappeared()
        {
            var fixture = CreateUpToDate();
            fixture.Manifest.DependencySearchDirectories.Add(
                fixture.Validator.CaptureDirectory(Path.Combine(Root, "search"), hashContent: true));
            Assert.True(fixture.IsCurrent(out string reason), reason);

            fixture.FileSystem.WithDirectory(Path.Combine(Root, "search"));
            Assert.False(fixture.IsCurrent(out reason));
            Assert.Contains("search directory changed", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestMetadataOnlySearchDirectoryIsRejected()
        {
            var fixture = CreateUpToDate();
            fixture.Manifest.DependencySearchDirectories[0].IsContentHashed = false;

            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("search directory changed", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestDuplicateSearchDirectoryIsRejected()
        {
            var fixture = CreateUpToDate();
            fixture.Manifest.DependencySearchDirectories.Add(
                fixture.Manifest.DependencySearchDirectories[0]);

            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("search directory changed", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestANewerInstalledFrameworkIsNoticed()
        {
            // Which shared framework directory gets searched is a roll-forward over whatever is
            // installed, and it takes the highest. Installing a newer patch adds a directory beside
            // the chosen one: no file the last run read has changed, and neither has any directory
            // it searched, so nothing else here would ever notice that a fresh run would now resolve
            // against something different.
            var fixture = CreateUpToDate();
            fixture.FileSystem.WithDirectory(Path.Combine(FrameworkDirectory, "8.0.20"));

            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("framework versions installed", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestARemovedFrameworkVersionIsNoticed()
        {
            // The other direction, which sends the roll-forward to an older version it would not
            // have picked before.
            var fixture = CreateUpToDate();
            fixture.FileSystem.DeleteDirectory(Path.Combine(FrameworkDirectory, "8.0.10"), true);

            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("framework versions installed", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestAFrameworkThatWasNotInstalledAppearing()
        {
            // A framework asked for and not installed is recorded as absent rather than skipped, so
            // that the run after it is installed does not accept output produced without it.
            var fixture = CreateUpToDate();
            string absent = Path.Combine(Root, "dotnet", "shared", "Absent");
            fixture.Manifest.FrameworkInventories.Add(fixture.Validator.CaptureDirectoryNames(absent));
            Assert.True(fixture.IsCurrent(out string reason), reason);

            fixture.FileSystem.WithDirectory(Path.Combine(absent, "1.0.0"));
            Assert.False(fixture.IsCurrent(out reason));
            Assert.Contains("framework versions installed", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestADuplicateFrameworkInventoryIsRejected()
        {
            var fixture = CreateUpToDate();
            fixture.Manifest.FrameworkInventories.Add(fixture.Manifest.FrameworkInventories[0]);

            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("framework versions installed", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestAMissingFrameworkInventoryListIsRejected()
        {
            // A manifest from before these were recorded parses into a null list. Treated as
            // incomplete rather than as "no frameworks", which would accept exactly the runs this
            // was added to reject.
            var fixture = CreateUpToDate();
            fixture.Manifest.FrameworkInventories = null;

            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("incomplete", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestAFrameworkInventoryDependsOnTheFilesInIt()
        {
            // Candidate framework versions are part of the resolution input, including assemblies
            // in a version directory that was not selected by the current run.
            var fixture = CreateUpToDate();
            fixture.FileSystem.WithFile(
                Path.Combine(FrameworkDirectory, "8.0.10", "System.Runtime.dll"), "an assembly");

            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("framework versions installed", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestAnExecutableAppearingInASearchDirectoryIsNoticed()
        {
            // Mono.Cecil probes '.exe' before '.dll'. So an executable arriving beside an assembly
            // does not merely satisfy a reference that used to fail -- it takes a resolution that
            // currently goes to the assembly next to it, which recording only '*.dll' could not see.
            var fixture = CreateUpToDate();
            fixture.FileSystem.WithFile(In("Dependency.exe"), "an executable that now wins");

            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("search directory changed", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestAWinmdAppearingInASearchDirectoryIsNoticed()
        {
            // The Windows Runtime half of the same probe.
            var fixture = CreateUpToDate();
            fixture.FileSystem.WithFile(In("Dependency.winmd"), "a windows metadata file");

            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("search directory changed", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestAFileNoResolverWouldLookAtIsIgnored()
        {
            // The other side of it. A search directory is an ordinary build output holding logs,
            // configuration and documentation, and invalidating on those would rewrite everything on
            // every run for no reason at all.
            var fixture = CreateUpToDate();
            fixture.FileSystem.WithFile(In("build.log"), "a log nobody resolves");

            Assert.True(fixture.IsCurrent(out string reason), reason);
        }

        [Fact(Timeout = 5000)]
        public void TestDependencyRootsDifferingOnlyInCaseAreBothChecked()
        {
            // Two genuinely different directories on a case-sensitive volume, recorded by a run
            // whose output sits on a case-insensitive one. Deciding how to compare paths from the
            // output directory folds these two into one, after which only one of them is
            // fingerprinted and anything appearing in the other is invisible.
            string sensitiveRoot = Path.Combine(Root, "deps");
            var fileSystem = new InMemoryFileSystem(isCaseInsensitive: false)
                .WithFile(In("App.dll"), "the original assembly")
                .WithFile(In("App.pdb"), "the original symbols")
                .WithFile(In("App.runtimeconfig.json"), "{ }")
                .WithFile(In("Dependency.dll"), "a dependency")
                .WithFile(Out("App.dll"), "the rewritten assembly")
                .WithFile(Out("App.pdb"), "the rewritten symbols")
                .WithFile(Path.Combine(sensitiveRoot, "Lib", "One.dll"), "one")
                .WithFile(Path.Combine(sensitiveRoot, "lib", "Two.dll"), "two")
                .WithDirectory(Out())
                .WithDirectory(Path.Combine(FrameworkDirectory, "8.0.10"))
                .WithDirectory(Path.Combine(FrameworkDirectory, "8.0.2"))
                .WithCaseSensitivity(OutputDirectory, true);

            var validator = CreateValidator(fileSystem);
            var manifest = CreateManifest(validator);
            manifest.DependencySearchDirectories.Add(
                validator.CaptureDirectory(Path.Combine(sensitiveRoot, "Lib"), hashContent: true));
            manifest.DependencySearchDirectories.Add(
                validator.CaptureDirectory(Path.Combine(sensitiveRoot, "lib"), hashContent: true));

            Assert.True(validator.IsManifestCurrent(manifest, out string reason), reason);

            // In the one whose name differs from the other only in case. If the two collapsed, this
            // directory is not the one that got fingerprinted and this goes unnoticed.
            fileSystem.WithFile(Path.Combine(sensitiveRoot, "lib", "Newcomer.dll"), "unnoticed");

            Assert.False(validator.IsManifestCurrent(manifest, out reason));
            Assert.Contains("search directory changed", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestCapturingADirectoryDoesNotAskAboutEachFile()
        {
            // A cost rather than an answer, and so invisible to every other test here: describing the
            // files one at a time gives exactly the same capture as listing them. It is worth a test
            // because of where this runs -- over the shared framework directories, which hold several
            // hundred assemblies each, and on both the path that checks the manifest and the one that
            // writes it -- so what is a call per file here is a few thousand per build.
            var fileSystem = new InMemoryFileSystem();
            string directory = Path.Combine(Root, "framework");
            for (int index = 0; index < 50; index++)
            {
                fileSystem.WithFile(Path.Combine(directory, $"Assembly{index}.dll"), $"assembly {index}");
            }

            var validator = CreateValidator(fileSystem);
            int before = fileSystem.GetFileCount;
            var captured = validator.CaptureDirectory(directory, hashContent: false);

            Assert.NotNull(captured.ContentHash);
            Assert.Equal(before, fileSystem.GetFileCount);
        }

        [Fact(Timeout = 5000)]
        public void TestCapturingADirectoryDoesNotDependOnTheOrderItWasBuilt()
        {
            // The capture is a hash of a sorted listing, so two directories holding the same
            // assemblies are the same offer however they came to hold them. Without the sort, a file
            // system that enumerated in a different order would report a search directory as changed
            // on every run and no rewrite would ever be skipped.
            string directory = Path.Combine(Root, "framework");
            var names = new[] { "Alpha.dll", "beta.dll", "Gamma.dll", "delta.dll" };

            string Capture(IEnumerable<string> order)
            {
                var fileSystem = new InMemoryFileSystem();
                foreach (string name in order)
                {
                    fileSystem.WithFile(Path.Combine(directory, name), name);
                }

                return CreateValidator(fileSystem).CaptureDirectory(directory, hashContent: true).ContentHash;
            }

            Assert.Equal(Capture(names), Capture(names.Reverse()));
        }

        [Fact(Timeout = 5000)]
        public void TestSameLengthReplacementOfAnUnresolvedOfferingIsNoticed()
        {
            // The gap that name and length alone leave. An assembly that failed to resolve is in no
            // recorded module, so its hash is nowhere; replaced by a different assembly of the same
            // length it changes nothing the run looks at, and the rewrite is skipped although
            // resolution would now succeed and produce different IL.
            var fixture = CreateUpToDate();
            Assert.True(fixture.IsCurrent(out string reason), reason);

            fixture.FileSystem.WithFile(In("Unresolved.dll"), "aaaaaaaa");
            fixture.Manifest.DependencySearchDirectories = new List<CacheDirectory>()
            {
                fixture.Validator.CaptureDirectory(InputDirectory, hashContent: true)
            };
            Assert.True(fixture.IsCurrent(out reason), reason);

            fixture.FileSystem.WriteAllText(In("Unresolved.dll"), "bbbbbbbb");
            Assert.Equal(8, fixture.FileSystem.GetFile(In("Unresolved.dll")).Length);
            Assert.False(fixture.IsCurrent(out reason));
            Assert.Contains("search directory changed", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestAMetadataCaptureStillNoticesARewrittenFile()
        {
            // Not what any directory gets today -- every one resolution is offered is hashed -- but
            // the form an older manifest can still be holding, so rechecking one has to mean
            // something rather than throw. Weaker than a hash, and knowably so: it catches a
            // replacement that changes the length or the write time, which is every replacement that
            // is not a deliberate restore of both.
            var fileSystem = new InMemoryFileSystem().WithFile(In("Framework.dll"), "aaaaaaaa");
            var validator = CreateValidator(fileSystem);
            var captured = validator.CaptureDirectory(InputDirectory, hashContent: false);
            Assert.True(validator.IsDirectoryCurrent(captured));

            // Same length, later write time, which is what a rewrite in place looks like.
            fileSystem.WriteAllText(In("Framework.dll"), "bbbbbbbb");
            Assert.False(validator.IsDirectoryCurrent(captured));
        }

        [Fact(Timeout = 5000)]
        public void TestACaptureIsRecheckedTheWayItWasRecorded()
        {
            // The two forms are not comparable, and only the run that wrote one knows which it chose.
            // Rechecking a metadata capture as a hashed one would report every directory an older
            // manifest recorded that way as changed, on a run where nothing had changed at all.
            var fileSystem = new InMemoryFileSystem().WithFile(In("Framework.dll"), "an assembly");
            var validator = CreateValidator(fileSystem);

            var hashed = validator.CaptureDirectory(InputDirectory, hashContent: true);
            var metadata = validator.CaptureDirectory(InputDirectory, hashContent: false);

            Assert.True(hashed.IsContentHashed);
            Assert.False(metadata.IsContentHashed);
            Assert.NotEqual(hashed.ContentHash, metadata.ContentHash);
            Assert.True(validator.IsDirectoryCurrent(hashed));
            Assert.True(validator.IsDirectoryCurrent(metadata));
        }

        [Fact(Timeout = 5000)]
        public void TestFingerprintingDeniesWriters()
        {
            var fileSystem = new InMemoryFileSystem().WithFile(In("App.dll"), "an assembly");
            CreateValidator(fileSystem).ComputeFileFingerprint(In("App.dll"));

            Assert.Equal(FileReadSharing.DenyWriters, Assert.Single(fileSystem.Reads).Sharing);
        }

        [Fact(Timeout = 5000)]
        public void TestFingerprintCaptureRetriesAFileChangedAfterMetadataWasRead()
        {
            var fileSystem = new InMemoryFileSystem().WithFile(In("App.dll"), "aaaaaaaa");
            int reads = 0;
            fileSystem.BeforeOpenRead = (_, __) =>
            {
                if (reads++ is 0)
                {
                    fileSystem.WriteAllText(In("App.dll"), "bbbbbbbb");
                }
            };
            var validator = CreateValidator(fileSystem);

            CacheFile captured = validator.CaptureFile(In("App.dll"), true);

            Assert.Equal(2, fileSystem.Reads.Count);
            Assert.Equal(validator.ComputeFileFingerprint(In("App.dll")), captured.Fingerprint);
        }

        [Fact(Timeout = 5000)]
        public void TestFingerprintCaptureRejectsAContinuouslyChangingFile()
        {
            var fileSystem = new InMemoryFileSystem().WithFile(In("App.dll"), "aaaaaaaa");
            bool alternate = false;
            fileSystem.BeforeOpenRead = (_, __) =>
            {
                alternate = !alternate;
                fileSystem.WriteAllText(In("App.dll"), alternate ? "bbbbbbbb" : "cccccccc");
            };

            Assert.Throws<IOException>(() => CreateValidator(fileSystem).CaptureFile(In("App.dll"), true));
            Assert.Equal(2, fileSystem.Reads.Count);
        }

        [Fact(Timeout = 5000)]
        public void TestStreamedFingerprintMatchesTheSingleBufferForm()
        {
            byte[] content = Enumerable.Range(0, (1 << 17) + 13).Select(value => (byte)value).ToArray();
            var fileSystem = new InMemoryFileSystem().WithFile(In("App.dll"), content);
            var validator = CreateValidator(fileSystem);

            Assert.Equal(
                RewritingCacheValidator.ComputeFingerprint(content),
                validator.ComputeFileFingerprint(In("App.dll")));
            Assert.Single(fileSystem.Reads);
        }

        [Fact(Timeout = 5000)]
        public void TestDebugArtifactChanged()
        {
            var fixture = CreateUpToDate(withArtifacts: true);
            Assert.True(fixture.IsCurrent(out string reason), reason);

            fixture.FileSystem.WriteAllText(Out("App.diff.json"), "[ \"a different diff\" ]");
            Assert.False(fixture.IsCurrent(out reason));
            Assert.Contains("debug artifact changed", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestEqualLengthDebugArtifactChanged()
        {
            var fixture = CreateUpToDate(withArtifacts: true);
            fixture.FileSystem.WriteAllText(Out("App.diff.json"), "{ }");

            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("debug artifact changed", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestConfiguredDebugArtifactCannotBeOmitted()
        {
            var fixture = CreateUpToDate(withArtifacts: true);
            fixture.Entry.Artifacts.Clear();

            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("incomplete or unexpected", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestUnexpectedDebugArtifactIsRejected()
        {
            var fixture = CreateUpToDate();
            fixture.FileSystem.WithFile(Out("App.diff.json"), "[ ]");
            fixture.Entry.Artifacts.Add(
                fixture.Validator.CaptureFile(Out("App.diff.json"), true));

            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("incomplete or unexpected", reason);
        }

        [Fact(Timeout = 5000)]
        public void TestDiagnosticsWereNotRecorded()
        {
            // A manifest written before diagnostics were replayed would silence them on every run
            // after, so an entry that does not carry them is not usable.
            var fixture = CreateUpToDate();
            fixture.Entry.ThreadStaticFields = null;
            Assert.False(fixture.IsCurrent(out string reason));
            Assert.Contains("diagnostics of 'App.dll'", reason);
        }

        [Theory(Timeout = 5000)]
        [InlineData(true)]
        [InlineData(false)]
        public void TestPathsAreComparedAsTheFileSystemDoes(bool isCaseInsensitive)
        {
            // The half of this that matters is the one a developer's machine cannot run: where two
            // spellings name one file, comparing them ordinally makes the run decide a rewritten
            // output is unprotected and copy the original over it.
            var fixture = CreateUpToDate(isCaseInsensitive);
            fixture.Manifest.OutputDirectory = RewritingCacheValidator
                .NormalizeDirectory(OutputDirectory).ToUpperInvariant();

            Assert.Equal(isCaseInsensitive, fixture.IsCurrent(out string reason));
            if (!isCaseInsensitive)
            {
                Assert.Contains("different directory", reason);
            }
        }
    }
}

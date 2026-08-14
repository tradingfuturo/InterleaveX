// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Coyote.IO;
using Microsoft.Coyote.Tests.Common.IO;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Tools.Tests
{
    public class RewritingOutputChangeJournalTests : BaseToolsTest
    {
        public RewritingOutputChangeJournalTests(ITestOutputHelper output)
            : base(output)
        {
        }

        private static readonly string Root =
            Path.GetFullPath(Path.DirectorySeparatorChar + "coyote-journal-tests");

        private static string Out(params string[] parts) =>
            Path.Combine(new[] { Root, "output" }.Concat(parts).ToArray());

        [Fact(Timeout = 5000)]
        public void TestJournalRestoresOverwrittenAndNewFiles()
        {
            var fileSystem = new InMemoryFileSystem()
                .WithFile(Out("existing.txt"), "before")
                .WithDirectory(Out());
            var journal = new Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal(
                fileSystem, Out());

            journal.Capture(Out("existing.txt"));
            fileSystem.WriteAllText(Out("existing.txt"), "after");
            journal.Capture(Out("new.txt"));
            fileSystem.WithFile(Out("new.txt"), "created");

            journal.Restore();

            Assert.Equal("before", fileSystem.GetContents(Out("existing.txt")));
            Assert.False(fileSystem.FileExists(Out("new.txt")));
            journal.Complete();
            Assert.False(fileSystem.DirectoryExists(journal.BackupDirectory));
        }

        [Fact(Timeout = 5000)]
        public void TestFailedRestoreIsPropagatedAndJournalIsRetained()
        {
            var fileSystem = new InMemoryFileSystem()
                .WithFile(Out("existing.txt"), "before")
                .WithDirectory(Out());
            var journal = new Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal(
                fileSystem, Out());
            journal.Capture(Out("existing.txt"));
            fileSystem.SetReadOnly(Out("existing.txt"));

            Assert.Throws<IOException>(() => journal.Restore());
            Assert.True(fileSystem.DirectoryExists(journal.BackupDirectory));
        }

        [Theory(Timeout = 5000)]
        [InlineData(false)]
        [InlineData(true)]
        [Trait("Category", "RewritingRemediation")]
        public void TestCaptureCanRetryAfterManifestSaveFailure(bool failAfterReplace)
        {
            var inner = new InMemoryFileSystem()
                .WithFile(Out("existing.txt"), "before")
                .WithDirectory(Out());
            var fileSystem = new InterruptingManifestReplaceFileSystem(inner);
            var journal = new Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal(
                fileSystem, Out());
            fileSystem.InterruptNextJournalReplace(failAfterReplace);

            Assert.Throws<IOException>(() => journal.Capture(Out("existing.txt")));
            journal.Capture(Out("existing.txt"));
            fileSystem.WriteAllText(Out("existing.txt"), "after");
            journal.Restore();

            Assert.Equal("before", fileSystem.ReadAllText(Out("existing.txt")));
            journal.Complete();
        }

        [Fact(Timeout = 5000)]
        public void TestTrailingOutputSeparatorPlacesBackupBesideOutput()
        {
            var fileSystem = new InMemoryFileSystem().WithDirectory(Out());
            string outputWithSeparator = Out() + Path.DirectorySeparatorChar;
            var journal = new Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal(
                fileSystem, outputWithSeparator);

            Assert.Equal(Path.GetDirectoryName(Out()), Path.GetDirectoryName(journal.BackupDirectory));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestUnfinishedJournalIsRecoveredAfterRestart()
        {
            var fileSystem = new InMemoryFileSystem()
                .WithFile(Out("existing.txt"), "before")
                .WithDirectory(Out());
            var journal = new Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal(
                fileSystem, Out());
            journal.Capture(Out("existing.txt"));
            fileSystem.WriteAllText(Out("existing.txt"), "interrupted");
            journal.Capture(Out("new.txt"));
            fileSystem.WithFile(Out("new.txt"), "created");

            Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.RecoverAll(fileSystem, Out());

            Assert.Equal("before", fileSystem.GetContents(Out("existing.txt")));
            Assert.False(fileSystem.FileExists(Out("new.txt")));
            Assert.Empty(Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.FindJournals(
                fileSystem, Out()));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestRecoveryCanResumeAfterFilesWereAlreadyRestored()
        {
            var fileSystem = new InMemoryFileSystem()
                .WithFile(Out("existing.txt"), "before")
                .WithDirectory(Out());
            var journal = new Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal(
                fileSystem, Out());
            journal.Capture(Out("existing.txt"));
            fileSystem.WriteAllText(Out("existing.txt"), "interrupted");
            journal.Restore();

            // Simulate interruption during cleanup, after the restored state reached disk but after
            // one backup was already removed. Recovery must finish deletion rather than need the
            // backup for a second restore.
            fileSystem.DeleteFile(Path.Combine(journal.BackupDirectory, "existing.txt"));

            Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.RecoverAll(fileSystem, Out());

            Assert.Equal("before", fileSystem.GetContents(Out("existing.txt")));
            Assert.Empty(Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.FindJournals(
                fileSystem, Out()));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestRetryMutationRearmsRestoredJournalBeforeRecovery()
        {
            var fileSystem = new InMemoryFileSystem()
                .WithFile(Out("existing.txt"), "before")
                .WithDirectory(Out());
            var journal = new Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal(
                fileSystem, Out());
            journal.Capture(Out("existing.txt"));
            fileSystem.WriteAllText(Out("existing.txt"), "first attempt");
            journal.Restore();

            journal.Capture(Out("existing.txt"));
            fileSystem.WriteAllText(Out("existing.txt"), "retry interrupted");

            Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.RecoverAll(fileSystem, Out());

            Assert.Equal("before", fileSystem.GetContents(Out("existing.txt")));
            Assert.Empty(Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.FindJournals(
                fileSystem, Out()));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestRestoreRemovesEveryDirectoryCreatedForNestedOutput()
        {
            var fileSystem = new InMemoryFileSystem().WithDirectory(Out());
            var journal = new Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal(
                fileSystem, Out());
            string child = Out("new", "child");

            journal.CaptureDirectory(child);
            fileSystem.CreateDirectory(child);
            journal.Restore();

            Assert.False(fileSystem.DirectoryExists(child));
            Assert.False(fileSystem.DirectoryExists(Out("new")));
            Assert.True(fileSystem.DirectoryExists(Out()));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestMultipleJournalsRecoverNewestFirst()
        {
            var fileSystem = new InMemoryFileSystem()
                .WithFile(Out("existing.txt"), "original")
                .WithDirectory(Out());
            var first = new Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal(fileSystem, Out());
            first.Capture(Out("existing.txt"));
            fileSystem.WriteAllText(Out("existing.txt"), "first");
            var second = new Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal(fileSystem, Out());
            second.Capture(Out("existing.txt"));
            fileSystem.WriteAllText(Out("existing.txt"), "second");

            Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.RecoverAll(fileSystem, Out());

            Assert.Equal("original", fileSystem.GetContents(Out("existing.txt")));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestLegacyJournalIsRejectedWithoutDeletingIt()
        {
            var fileSystem = new InMemoryFileSystem().WithDirectory(Out());
            string legacy = Out() + ".mirror-backup-legacy";
            fileSystem.WithDirectory(legacy).WithFile(Path.Combine(legacy, "old.txt"), "backup");

            IOException error = Assert.Throws<IOException>(() =>
                Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.RecoverAll(fileSystem, Out()));

            Assert.Contains("cannot be recovered", error.Message);
            Assert.True(fileSystem.DirectoryExists(legacy));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestCompletedJournalCleanupResumesWithoutRollingBackCommittedOutput()
        {
            var inner = new InMemoryFileSystem()
                .WithFile(Out("existing.txt"), "before")
                .WithDirectory(Out());
            var fileSystem = new InterruptingCleanupFileSystem(inner);
            var journal = new Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal(
                fileSystem, Out());
            journal.Capture(Out("existing.txt"));
            inner.WriteAllText(Out("existing.txt"), "committed");
            fileSystem.InterruptJournal(journal.BackupDirectory);

            Assert.Throws<IOException>(() => journal.Complete());

            Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.RecoverAll(fileSystem, Out());

            Assert.Equal("committed", inner.GetContents(Out("existing.txt")));
            Assert.Empty(Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.FindJournals(
                fileSystem, Out()));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestEmptyManifestlessJournalIsDiscardedAfterInterruptedCleanup()
        {
            var fileSystem = new InMemoryFileSystem().WithDirectory(Out());
            string remnant = Out() + ".mirror-backup-empty";
            fileSystem.WithDirectory(remnant);

            Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.RecoverAll(fileSystem, Out());

            Assert.False(fileSystem.DirectoryExists(remnant));
            Assert.Empty(Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.FindJournals(
                fileSystem, Out()));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestInterruptedEmptyJournalInitializationIsDiscarded()
        {
            var inner = new InMemoryFileSystem().WithDirectory(Out());
            var fileSystem = new InterruptingInitialManifestFileSystem(inner);

            Assert.Throws<IOException>(() =>
                new Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal(fileSystem, Out()));
            string remnant = Assert.Single(
                Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.FindJournals(fileSystem, Out()));
            Assert.True(fileSystem.FileExists(Path.Combine(remnant, "journal.json.tmp")));

            Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.RecoverAll(fileSystem, Out());

            Assert.Empty(Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.FindJournals(
                fileSystem, Out()));
        }

        private sealed class InterruptingInitialManifestFileSystem : IFileSystem
        {
            private readonly IFileSystem Inner;

            internal InterruptingInitialManifestFileSystem(IFileSystem inner) => this.Inner = inner;

            public bool FileExists(string path) => this.Inner.FileExists(path);

            public bool DirectoryExists(string path) => this.Inner.DirectoryExists(path);

            public IFileEntry GetFile(string path) => this.Inner.GetFile(path);

            public string ReadAllText(string path) => this.Inner.ReadAllText(path);

            public void WriteAllText(string path, string contents) => this.Inner.WriteAllText(path, contents);

            public Stream OpenRead(string path, FileReadSharing sharing) => this.Inner.OpenRead(path, sharing);

            public void CopyFile(string sourcePath, string targetPath, bool overwrite) =>
                this.Inner.CopyFile(sourcePath, targetPath, overwrite);

            public void MoveFile(string sourcePath, string targetPath)
            {
                if (IsJournalManifest(targetPath))
                {
                    throw new IOException("Simulated journal move interruption.");
                }

                this.Inner.MoveFile(sourcePath, targetPath);
            }

            public void ReplaceFile(string sourcePath, string targetPath, string backupPath) =>
                this.Inner.ReplaceFile(sourcePath, targetPath, backupPath);

            public void DeleteFile(string path) => this.Inner.DeleteFile(path);

            public void CreateDirectory(string path) => this.Inner.CreateDirectory(path);

            public void DeleteDirectory(string path, bool recursive) =>
                this.Inner.DeleteDirectory(path, recursive);

            public string[] GetFiles(string directory, string searchPattern) =>
                this.Inner.GetFiles(directory, searchPattern);

            public IReadOnlyList<IFileEntry> GetFileEntries(string directory, string searchPattern) =>
                this.Inner.GetFileEntries(directory, searchPattern);

            public string[] GetDirectories(string directory, string searchPattern, bool recursive) =>
                this.Inner.GetDirectories(directory, searchPattern, recursive);

            public bool IsCaseInsensitive(string directory) => this.Inner.IsCaseInsensitive(directory);

            private static bool IsJournalManifest(string path) => string.Equals(
                Path.GetFileName(path), "journal.json", StringComparison.Ordinal);
        }

        private sealed class InterruptingCleanupFileSystem : IFileSystem
        {
            private readonly IFileSystem Inner;

            private string JournalDirectory;

            private bool IsInterruptionArmed;

            internal InterruptingCleanupFileSystem(IFileSystem inner) => this.Inner = inner;

            internal void InterruptJournal(string journalDirectory)
            {
                this.JournalDirectory = Path.GetFullPath(journalDirectory);
                this.IsInterruptionArmed = true;
            }

            public bool FileExists(string path) => this.Inner.FileExists(path);

            public bool DirectoryExists(string path) => this.Inner.DirectoryExists(path);

            public IFileEntry GetFile(string path) => this.Inner.GetFile(path);

            public string ReadAllText(string path) => this.Inner.ReadAllText(path);

            public void WriteAllText(string path, string contents) => this.Inner.WriteAllText(path, contents);

            public Stream OpenRead(string path, FileReadSharing sharing) => this.Inner.OpenRead(path, sharing);

            public void CopyFile(string sourcePath, string targetPath, bool overwrite) =>
                this.Inner.CopyFile(sourcePath, targetPath, overwrite);

            public void MoveFile(string sourcePath, string targetPath) =>
                this.Inner.MoveFile(sourcePath, targetPath);

            public void ReplaceFile(string sourcePath, string targetPath, string backupPath) =>
                this.Inner.ReplaceFile(sourcePath, targetPath, backupPath);

            public void DeleteFile(string path)
            {
                this.Inner.DeleteFile(path);
                if (this.ShouldInterrupt(path))
                {
                    this.ThrowInterruption();
                }
            }

            public void CreateDirectory(string path) => this.Inner.CreateDirectory(path);

            public void DeleteDirectory(string path, bool recursive)
            {
                if (recursive && this.IsInterruptionArmed && this.IsJournalRoot(path))
                {
                    string backup = this.Inner.GetFiles(path, "*")
                        .First(file => !string.Equals(Path.GetFileName(file), "journal.json",
                            StringComparison.Ordinal));
                    this.Inner.DeleteFile(backup);
                    this.ThrowInterruption();
                }

                this.Inner.DeleteDirectory(path, recursive);
            }

            public string[] GetFiles(string directory, string searchPattern) =>
                this.Inner.GetFiles(directory, searchPattern);

            public IReadOnlyList<IFileEntry> GetFileEntries(string directory, string searchPattern) =>
                this.Inner.GetFileEntries(directory, searchPattern);

            public string[] GetDirectories(string directory, string searchPattern, bool recursive) =>
                this.Inner.GetDirectories(directory, searchPattern, recursive);

            public bool IsCaseInsensitive(string directory) => this.Inner.IsCaseInsensitive(directory);

            private bool ShouldInterrupt(string path) => this.IsInterruptionArmed &&
                Path.GetFullPath(path).StartsWith(this.JournalDirectory + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetFileName(path), "journal.json", StringComparison.Ordinal);

            private bool IsJournalRoot(string path) => string.Equals(
                Path.GetFullPath(path), this.JournalDirectory, StringComparison.OrdinalIgnoreCase);

            private void ThrowInterruption()
            {
                this.IsInterruptionArmed = false;
                throw new IOException("Simulated cleanup interruption.");
            }
        }

        private sealed class InterruptingManifestReplaceFileSystem : IFileSystem
        {
            private readonly IFileSystem Inner;
            private bool IsReplaceInterrupted;
            private bool FailAfterReplace;

            internal InterruptingManifestReplaceFileSystem(IFileSystem inner) => this.Inner = inner;

            internal void InterruptNextJournalReplace(bool failAfterReplace)
            {
                this.IsReplaceInterrupted = true;
                this.FailAfterReplace = failAfterReplace;
            }

            public bool FileExists(string path) => this.Inner.FileExists(path);
            public bool DirectoryExists(string path) => this.Inner.DirectoryExists(path);
            public IFileEntry GetFile(string path) => this.Inner.GetFile(path);
            public string ReadAllText(string path) => this.Inner.ReadAllText(path);
            public void WriteAllText(string path, string contents) => this.Inner.WriteAllText(path, contents);
            public Stream OpenRead(string path, FileReadSharing sharing) => this.Inner.OpenRead(path, sharing);
            public void CopyFile(string sourcePath, string targetPath, bool overwrite) =>
                this.Inner.CopyFile(sourcePath, targetPath, overwrite);
            public void MoveFile(string sourcePath, string targetPath) =>
                this.Inner.MoveFile(sourcePath, targetPath);

            public void ReplaceFile(string sourcePath, string targetPath, string backupPath)
            {
                if (this.IsReplaceInterrupted && string.Equals(
                    Path.GetFileName(targetPath), "journal.json", StringComparison.Ordinal))
                {
                    this.IsReplaceInterrupted = false;
                    if (this.FailAfterReplace)
                    {
                        this.Inner.ReplaceFile(sourcePath, targetPath, backupPath);
                    }

                    throw new IOException("Simulated journal replace interruption.");
                }

                this.Inner.ReplaceFile(sourcePath, targetPath, backupPath);
            }

            public void DeleteFile(string path) => this.Inner.DeleteFile(path);
            public void CreateDirectory(string path) => this.Inner.CreateDirectory(path);
            public void DeleteDirectory(string path, bool recursive) =>
                this.Inner.DeleteDirectory(path, recursive);
            public string[] GetFiles(string directory, string searchPattern) =>
                this.Inner.GetFiles(directory, searchPattern);
            public IReadOnlyList<IFileEntry> GetFileEntries(string directory, string searchPattern) =>
                this.Inner.GetFileEntries(directory, searchPattern);
            public string[] GetDirectories(string directory, string searchPattern, bool recursive) =>
                this.Inner.GetDirectories(directory, searchPattern, recursive);
            public bool IsCaseInsensitive(string directory) => this.Inner.IsCaseInsensitive(directory);
        }
    }
}

// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.IO;
using System.Linq;
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
    }
}

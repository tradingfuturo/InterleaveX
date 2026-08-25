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
            fileSystem.WriteAllText(Out("existing.txt"), "after");
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
        public void TestMultipleJournalsAreRejectedWithoutMutation()
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

            IOException error = Assert.Throws<IOException>(() =>
                Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.RecoverAll(fileSystem, Out()));

            Assert.Contains("multiple", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("second", fileSystem.GetContents(Out("existing.txt")));
            Assert.Equal(2, Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.FindJournals(
                fileSystem, Out()).Count);
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

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestMissingTargetPublicationRacePreservesExternalFile()
        {
            string staged = Out("staged.txt");
            string target = Out("target.txt");
            var fileSystem = new InMemoryFileSystem()
                .WithDirectory(Out())
                .WithFile(staged, "staged");
            var journal = new Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal(
                fileSystem, Out());
            fileSystem.BeforeMoveFile = (_, destination) =>
            {
                if (string.Equals(destination, target, StringComparison.Ordinal))
                {
                    fileSystem.WithFile(target, "external");
                }
            };

            Assert.Throws<IOException>(() => journal.Publish(staged, target, null));
            journal.Restore();
            journal.Complete();

            Assert.Equal("external", fileSystem.ReadAllText(target));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestMissingTargetPublicationRacePreservesByteIdenticalExternalFile()
        {
            string staged = Out("staged.txt");
            string target = Out("target.txt");
            var fileSystem = new InMemoryFileSystem()
                .WithDirectory(Out())
                .WithFile(staged, "staged");
            var journal = new Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal(
                fileSystem, Out());
            fileSystem.BeforeMoveFile = (_, destination) =>
            {
                if (string.Equals(destination, target, StringComparison.Ordinal))
                {
                    fileSystem.WithFile(target, "staged");
                }
            };

            Assert.Throws<IOException>(() => journal.Publish(staged, target, null));
            journal.Restore();
            journal.Complete();

            Assert.Equal("staged", fileSystem.ReadAllText(target));
            Assert.Equal("staged", fileSystem.ReadAllText(staged));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestRecoveryRetainsInterruptedMissingTargetPublicationWithUnknownMoveResult()
        {
            string staged = Out("staged.txt");
            string target = Out("target.txt");
            var fileSystem = new InMemoryFileSystem()
                .WithDirectory(Out())
                .WithFile(staged, "staged");
            var journal = new Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal(
                fileSystem, Out());
            int replacements = 0;
            fileSystem.BeforeReplaceFile = (source, destination, backup) =>
            {
                if (++replacements is 2)
                {
                    throw new IOException("Simulated conversion interruption.");
                }
            };

            Assert.Throws<IOException>(() => journal.Publish(staged, target, null));
            fileSystem.BeforeReplaceFile = null;
            IOException error = Assert.Throws<IOException>(() =>
                Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.RecoverAll(fileSystem, Out()));

            Assert.Contains("unknown", error.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("staged", fileSystem.ReadAllText(target));
            Assert.Single(Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.FindJournals(
                fileSystem, Out()));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestLegacyPendingPublicationWithNoMoveResultRemainsAmbiguous()
        {
            string staged = Out("staged.txt");
            string target = Out("target.txt");
            var fileSystem = new InMemoryFileSystem()
                .WithDirectory(Out())
                .WithFile(staged, "staged");
            var journal = new Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal(
                fileSystem, Out());
            int replacements = 0;
            fileSystem.BeforeReplaceFile = (_, _, _) =>
            {
                if (++replacements is 2)
                {
                    throw new IOException("Simulated result-record interruption.");
                }
            };

            Assert.Throws<IOException>(() => journal.Publish(staged, target, null));
            fileSystem.BeforeReplaceFile = null;
            string manifestPath = Path.Combine(journal.BackupDirectory, "journal.json");
            fileSystem.WriteAllText(manifestPath, fileSystem.ReadAllText(manifestPath)
                .Replace("\"Version\": 6", "\"Version\": 4", StringComparison.Ordinal));

            IOException error = Assert.Throws<IOException>(() =>
                Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.RecoverAll(fileSystem, Out()));

            Assert.Contains("legacy", error.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("staged", fileSystem.ReadAllText(target));
            Assert.Single(Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.FindJournals(
                fileSystem, Out()));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestRecoveryPreservesChangedInterruptedPublicationTarget()
        {
            string staged = Out("staged.txt");
            string target = Out("target.txt");
            var fileSystem = new InMemoryFileSystem()
                .WithDirectory(Out())
                .WithFile(staged, "staged");
            var journal = new Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal(
                fileSystem, Out());
            int replacements = 0;
            fileSystem.BeforeReplaceFile = (source, destination, backup) =>
            {
                if (++replacements is 2)
                {
                    throw new IOException("Simulated conversion interruption.");
                }
            };

            Assert.Throws<IOException>(() => journal.Publish(staged, target, null));
            fileSystem.BeforeReplaceFile = null;
            fileSystem.WithFile(target, "external");

            IOException error = Assert.Throws<IOException>(() =>
                Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.RecoverAll(fileSystem, Out()));
            Assert.Contains("unknown", error.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("external", fileSystem.ReadAllText(target));
            Assert.Single(Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.FindJournals(
                fileSystem, Out()));
        }

        [Theory(Timeout = 5000)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [Trait("Category", "RewritingRemediation")]
        public void TestLegacySchemaJournalRemainsRecoverable(int version)
        {
            string target = Out("existing.txt");
            var fileSystem = new InMemoryFileSystem()
                .WithDirectory(Out())
                .WithFile(target, "before");
            var journal = new Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal(
                fileSystem, Out());
            journal.Capture(target);
            fileSystem.WriteAllText(target, "after");
            string manifestPath = Path.Combine(journal.BackupDirectory, "journal.json");
            fileSystem.WriteAllText(manifestPath, fileSystem.ReadAllText(manifestPath)
                .Replace("\"Version\": 6", $"\"Version\": {version}", StringComparison.Ordinal)
                .Replace(",\n  \"PendingPublications\": []", string.Empty,
                    StringComparison.Ordinal));

            Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.RecoverAll(fileSystem, Out());

            Assert.Equal("before", fileSystem.ReadAllText(target));
            Assert.Empty(Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.FindJournals(
                fileSystem, Out()));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestExistingTargetPublicationDoesNotDestructivelyWriteTheTargetStream()
        {
            string staged = Out("staged.txt");
            string target = Out("target.txt");
            var inner = new InMemoryFileSystem()
                .WithDirectory(Out())
                .WithFile(staged, "replacement")
                .WithFile(target, "original");
            var fileSystem = new RejectingTargetWriteFileSystem(inner, target);
            var journal = new Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal(fileSystem, Out());
            IFileEntry targetEntry = inner.GetFile(target);
            var expected = new Microsoft.Coyote.Rewriting.MirroredFile(
                targetEntry.Length, targetEntry.LastWriteTimeUtc,
                Microsoft.Coyote.Rewriting.RewritingCacheValidator.ComputeFileFingerprint(inner, target));

            journal.Publish(staged, target, expected);
            Assert.Equal("replacement", inner.ReadAllText(target));

            journal.Restore();
            Assert.Equal("original", inner.ReadAllText(target));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestExistingTargetPublicationReconcilesPostEffectReplaceFailure()
        {
            string staged = Out("staged.txt");
            string target = Out("target.txt");
            var inner = new InMemoryFileSystem()
                .WithDirectory(Out())
                .WithFile(staged, "replacement")
                .WithFile(target, "original");
            var fileSystem = new RejectingTargetWriteFileSystem(inner, target)
            {
                ThrowAfterTargetReplace = true
            };
            var journal = new Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal(fileSystem, Out());
            IFileEntry targetEntry = inner.GetFile(target);
            var expected = new Microsoft.Coyote.Rewriting.MirroredFile(
                targetEntry.Length, targetEntry.LastWriteTimeUtc,
                Microsoft.Coyote.Rewriting.RewritingCacheValidator.ComputeFileFingerprint(inner, target));

            journal.Publish(staged, target, expected);
            Assert.Equal("replacement", inner.ReadAllText(target));
            journal.Restore();
            Assert.Equal("original", inner.ReadAllText(target));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestExistingTargetPublicationRestoresAtomicPreReplaceRace()
        {
            string staged = Out("staged.txt");
            string target = Out("target.txt");
            var inner = new InMemoryFileSystem()
                .WithDirectory(Out())
                .WithFile(staged, "replacement")
                .WithFile(target, "original");
            var fileSystem = new RejectingTargetWriteFileSystem(inner, target)
            {
                BeforeTargetReplace = () => inner.WriteAllText(target, "external")
            };
            var journal = new Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal(fileSystem, Out());
            IFileEntry targetEntry = inner.GetFile(target);
            var expected = new Microsoft.Coyote.Rewriting.MirroredFile(
                targetEntry.Length, targetEntry.LastWriteTimeUtc,
                Microsoft.Coyote.Rewriting.RewritingCacheValidator.ComputeFileFingerprint(inner, target));

            Assert.Throws<IOException>(() => journal.Publish(staged, target, expected));
            Assert.Equal("external", inner.ReadAllText(target));
            journal.Restore();
            journal.Complete();
            Assert.Equal("external", inner.ReadAllText(target));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestRecoveryRestoresCapturedPreReplaceRaceAfterInspectionCrash()
        {
            string staged = Out("staged.txt");
            string target = Out("target.txt");
            var inner = new InMemoryFileSystem()
                .WithDirectory(Out())
                .WithFile(staged, "replacement")
                .WithFile(target, "original");
            var fileSystem = new RejectingTargetWriteFileSystem(inner, target)
            {
                BeforeTargetReplace = () => inner.WriteAllText(target, "external"),
                ThrowAfterPublicationReplaceInspection = true
            };
            var journal = new Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal(fileSystem, Out());
            IFileEntry targetEntry = inner.GetFile(target);
            var expected = new Microsoft.Coyote.Rewriting.MirroredFile(
                targetEntry.Length, targetEntry.LastWriteTimeUtc,
                Microsoft.Coyote.Rewriting.RewritingCacheValidator.ComputeFileFingerprint(inner, target));

            Assert.Throws<IOException>(() => journal.Publish(staged, target, expected));

            Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.RecoverAll(fileSystem, Out());

            Assert.Equal("external", inner.ReadAllText(target));
            Assert.Empty(Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.FindJournals(
                fileSystem, Out()));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestNewTargetPublicationRetainsJournalWhenMoveResultIsUnknown()
        {
            string staged = Out("staged.txt");
            string target = Out("target.txt");
            var inner = new InMemoryFileSystem()
                .WithDirectory(Out())
                .WithFile(staged, "replacement");
            var fileSystem = new PostEffectTransferFileSystem(inner, target)
            {
                ThrowAfterMove = true
            };
            var journal = new Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal(fileSystem, Out());

            Assert.Throws<IOException>(() => journal.Publish(staged, target, null));

            // A failure after a move can leave a byte-identical target while offering no durable
            // ownership proof. Recovery must retain the journal instead of deleting that target.
            IOException error = Assert.Throws<IOException>(() => journal.Restore());

            Assert.Contains("unknown", error.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("replacement", inner.ReadAllText(target));
            Assert.True(inner.DirectoryExists(journal.BackupDirectory));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestRecoveryRetriesRestoreAfterPostEffectReplaceFailure()
        {
            string target = Out("existing.txt");
            var inner = new InMemoryFileSystem()
                .WithDirectory(Out())
                .WithFile(target, "before");
            var fileSystem = new PostEffectTransferFileSystem(inner, target)
            {
                ThrowAfterReplace = true
            };
            var journal = new Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal(fileSystem, Out());
            journal.Capture(target);
            inner.WriteAllText(target, "after");

            Assert.Throws<IOException>(() => journal.Restore());
            Assert.Equal("before", inner.ReadAllText(target));

            // Restore consumed the backup before reporting failure. A fresh process must treat the
            // already-restored target as success and finish the retained restoring journal.
            Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.RecoverAll(fileSystem, Out());

            Assert.Equal("before", inner.ReadAllText(target));
            Assert.Empty(Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.FindJournals(
                fileSystem, Out()));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestRecoveryRetriesRestoreAfterPostEffectMoveFailure()
        {
            string target = Out("existing.txt");
            var inner = new InMemoryFileSystem()
                .WithDirectory(Out())
                .WithFile(target, "before");
            var fileSystem = new PostEffectTransferFileSystem(inner, target)
            {
                ThrowAfterMove = true
            };
            var journal = new Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal(fileSystem, Out());
            journal.Capture(target);
            inner.WriteAllText(target, "after");
            inner.DeleteFile(target);

            Assert.Throws<IOException>(() => journal.Restore());
            Assert.Equal("before", inner.ReadAllText(target));

            Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.RecoverAll(fileSystem, Out());

            Assert.Equal("before", inner.ReadAllText(target));
            Assert.Empty(Microsoft.Coyote.Rewriting.RewritingOutputChangeJournal.FindJournals(
                fileSystem, Out()));
        }

        private sealed class PostEffectTransferFileSystem : IFileSystem
        {
            private readonly IFileSystem Inner;
            private readonly string Target;

            internal bool ThrowAfterMove { get; set; }

            internal bool ThrowAfterReplace { get; set; }

            internal PostEffectTransferFileSystem(IFileSystem inner, string target)
            {
                this.Inner = inner;
                this.Target = Path.GetFullPath(target);
            }

            public bool FileExists(string path) => this.Inner.FileExists(path);
            public bool DirectoryExists(string path) => this.Inner.DirectoryExists(path);
            public IFileEntry GetFile(string path) => this.Inner.GetFile(path);
            public string ReadAllText(string path) => this.Inner.ReadAllText(path);
            public void WriteAllText(string path, string contents) => this.Inner.WriteAllText(path, contents);
            public Stream OpenRead(string path, FileReadSharing sharing) => this.Inner.OpenRead(path, sharing);
            public Stream OpenWriteExclusive(string path) => this.Inner.OpenWriteExclusive(path);
            public Stream OpenWriteNewExclusive(string path) => this.Inner.OpenWriteNewExclusive(path);
            public void FlushWrite(Stream stream) => this.Inner.FlushWrite(stream);
            public void CopyFile(string sourcePath, string targetPath, bool overwrite) =>
                this.Inner.CopyFile(sourcePath, targetPath, overwrite);

            public void MoveFile(string sourcePath, string targetPath)
            {
                this.Inner.MoveFile(sourcePath, targetPath);
                if (this.ThrowAfterMove && string.Equals(
                    Path.GetFullPath(targetPath), this.Target, StringComparison.OrdinalIgnoreCase))
                {
                    this.ThrowAfterMove = false;
                    throw new IOException("Simulated move failure after the transfer took effect.");
                }
            }

            public MoveFileNoReplaceResult MoveFileNoReplace(string sourcePath, string targetPath)
            {
                if (this.ThrowAfterMove && string.Equals(
                    Path.GetFullPath(targetPath), this.Target, StringComparison.OrdinalIgnoreCase))
                {
                    this.ThrowAfterMove = false;
                    this.Inner.MoveFile(sourcePath, targetPath);
                    return new MoveFileNoReplaceResult(MoveFileNoReplaceState.Unknown,
                        new IOException("Simulated move failure after the transfer took effect."));
                }

                return this.Inner.MoveFileNoReplace(sourcePath, targetPath);
            }

            public void ReplaceFile(string sourcePath, string targetPath, string backupPath)
            {
                this.Inner.ReplaceFile(sourcePath, targetPath, backupPath);
                if (this.ThrowAfterReplace && string.IsNullOrEmpty(backupPath) && string.Equals(
                    Path.GetFullPath(targetPath), this.Target, StringComparison.OrdinalIgnoreCase))
                {
                    this.ThrowAfterReplace = false;
                    throw new IOException("Simulated replacement failure after the transfer took effect.");
                }
            }

            public void DeleteFile(string path) => this.Inner.DeleteFile(path);
            public void CreateDirectory(string path) => this.Inner.CreateDirectory(path);
            public void DeleteDirectory(string path, bool recursive) => this.Inner.DeleteDirectory(path, recursive);
            public string[] GetFiles(string directory, string searchPattern) =>
                this.Inner.GetFiles(directory, searchPattern);
            public IReadOnlyList<IFileEntry> GetFileEntries(string directory, string searchPattern) =>
                this.Inner.GetFileEntries(directory, searchPattern);
            public string[] GetDirectories(string directory, string searchPattern, bool recursive) =>
                this.Inner.GetDirectories(directory, searchPattern, recursive);
            public bool IsCaseInsensitive(string directory) => this.Inner.IsCaseInsensitive(directory);
        }

        private sealed class RejectingTargetWriteFileSystem : IFileSystem
        {
            private readonly IFileSystem Inner;
            private readonly string Target;

            internal bool ThrowAfterTargetReplace { get; set; }

            internal bool ThrowAfterPublicationReplaceInspection { get; set; }

            private bool IsPublicationReplaceComplete { get; set; }

            internal Action BeforeTargetReplace { get; set; }

            internal RejectingTargetWriteFileSystem(IFileSystem inner, string target)
            {
                this.Inner = inner;
                this.Target = Path.GetFullPath(target);
            }

            public bool FileExists(string path) => this.Inner.FileExists(path);
            public bool DirectoryExists(string path) => this.Inner.DirectoryExists(path);
            public IFileEntry GetFile(string path)
            {
                if (this.IsPublicationReplaceComplete)
                {
                    this.IsPublicationReplaceComplete = false;
                    throw new IOException("Simulated crash while inspecting the publication backup.");
                }

                return this.Inner.GetFile(path);
            }

            public string ReadAllText(string path) => this.Inner.ReadAllText(path);
            public void WriteAllText(string path, string contents) => this.Inner.WriteAllText(path, contents);
            public Stream OpenRead(string path, FileReadSharing sharing) => this.Inner.OpenRead(path, sharing);
            public Stream OpenWriteExclusive(string path) => string.Equals(
                Path.GetFullPath(path), this.Target, StringComparison.OrdinalIgnoreCase) ?
                throw new IOException("Destructive target writes are forbidden by this regression test.") :
                this.Inner.OpenWriteExclusive(path);
            public Stream OpenWriteNewExclusive(string path) => this.Inner.OpenWriteNewExclusive(path);
            public void FlushWrite(Stream stream) => this.Inner.FlushWrite(stream);
            public void CopyFile(string sourcePath, string targetPath, bool overwrite) =>
                this.Inner.CopyFile(sourcePath, targetPath, overwrite);
            public void MoveFile(string sourcePath, string targetPath) => this.Inner.MoveFile(sourcePath, targetPath);
            public MoveFileNoReplaceResult MoveFileNoReplace(string sourcePath, string targetPath) =>
                this.Inner.MoveFileNoReplace(sourcePath, targetPath);
            public void ReplaceFile(string sourcePath, string targetPath, string backupPath)
            {
                bool isTarget = string.Equals(
                    Path.GetFullPath(targetPath), this.Target, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(backupPath);
                if (isTarget)
                {
                    Action before = this.BeforeTargetReplace;
                    this.BeforeTargetReplace = null;
                    before?.Invoke();
                }

                this.Inner.ReplaceFile(sourcePath, targetPath, backupPath);
                if (isTarget && this.ThrowAfterTargetReplace)
                {
                    this.ThrowAfterTargetReplace = false;
                    throw new IOException("Simulated failure after atomic replacement took effect.");
                }

                if (isTarget && this.ThrowAfterPublicationReplaceInspection)
                {
                    this.ThrowAfterPublicationReplaceInspection = false;
                    this.IsPublicationReplaceComplete = true;
                }
            }

            public void DeleteFile(string path) => this.Inner.DeleteFile(path);
            public void CreateDirectory(string path) => this.Inner.CreateDirectory(path);
            public void DeleteDirectory(string path, bool recursive) => this.Inner.DeleteDirectory(path, recursive);
            public string[] GetFiles(string directory, string searchPattern) =>
                this.Inner.GetFiles(directory, searchPattern);
            public IReadOnlyList<IFileEntry> GetFileEntries(string directory, string searchPattern) =>
                this.Inner.GetFileEntries(directory, searchPattern);
            public string[] GetDirectories(string directory, string searchPattern, bool recursive) =>
                this.Inner.GetDirectories(directory, searchPattern, recursive);
            public bool IsCaseInsensitive(string directory) => this.Inner.IsCaseInsensitive(directory);
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

            public Stream OpenWriteExclusive(string path) => this.Inner.OpenWriteExclusive(path);
            public Stream OpenWriteNewExclusive(string path) => this.Inner.OpenWriteNewExclusive(path);

            public void FlushWrite(Stream stream) => this.Inner.FlushWrite(stream);

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

            public MoveFileNoReplaceResult MoveFileNoReplace(string sourcePath, string targetPath) =>
                this.Inner.MoveFileNoReplace(sourcePath, targetPath);

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

            public Stream OpenWriteExclusive(string path) => this.Inner.OpenWriteExclusive(path);
            public Stream OpenWriteNewExclusive(string path) => this.Inner.OpenWriteNewExclusive(path);

            public void FlushWrite(Stream stream) => this.Inner.FlushWrite(stream);

            public void CopyFile(string sourcePath, string targetPath, bool overwrite) =>
                this.Inner.CopyFile(sourcePath, targetPath, overwrite);

            public void MoveFile(string sourcePath, string targetPath) =>
                this.Inner.MoveFile(sourcePath, targetPath);
            public MoveFileNoReplaceResult MoveFileNoReplace(string sourcePath, string targetPath) =>
                this.Inner.MoveFileNoReplace(sourcePath, targetPath);

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

            public Stream OpenWriteExclusive(string path) => this.Inner.OpenWriteExclusive(path);
            public Stream OpenWriteNewExclusive(string path) => this.Inner.OpenWriteNewExclusive(path);
            public void FlushWrite(Stream stream) => this.Inner.FlushWrite(stream);
            public void CopyFile(string sourcePath, string targetPath, bool overwrite) =>
                this.Inner.CopyFile(sourcePath, targetPath, overwrite);
            public void MoveFile(string sourcePath, string targetPath) =>
                this.Inner.MoveFile(sourcePath, targetPath);
            public MoveFileNoReplaceResult MoveFileNoReplace(string sourcePath, string targetPath) =>
                this.Inner.MoveFileNoReplace(sourcePath, targetPath);

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

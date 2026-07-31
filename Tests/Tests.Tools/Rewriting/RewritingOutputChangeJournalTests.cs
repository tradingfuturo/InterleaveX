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
    }
}

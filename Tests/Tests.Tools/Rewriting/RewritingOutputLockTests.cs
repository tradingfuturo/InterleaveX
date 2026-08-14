// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Coyote.Rewriting;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Tools.Tests
{
    public sealed class RewritingOutputLockTests : BaseToolsTest
    {
        public RewritingOutputLockTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestLockTimeoutReportsOwnerAndOutput()
        {
            string root = Path.Combine(Path.GetTempPath(), "coyote-lock-" + Guid.NewGuid().ToString("N"));
            string output = Path.Combine(root, "output");
            Directory.CreateDirectory(root);
            try
            {
                using var owner = RewritingOutputLock.Acquire(output, TimeSpan.FromSeconds(1));
                IOException error = Assert.Throws<IOException>(() =>
                    RewritingOutputLock.Acquire(output, TimeSpan.FromMilliseconds(250)));

                Assert.Contains(output, error.Message);
                Assert.Contains("pid=" + Process.GetCurrentProcess().Id, error.Message);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact(Timeout = 10000)]
        [Trait("Category", "RewritingRemediation")]
        public async Task TestLockWaitSucceedsWhenOwnerReleasesWithinBound()
        {
            string root = Path.Combine(Path.GetTempPath(), "coyote-lock-" + Guid.NewGuid().ToString("N"));
            string output = Path.Combine(root, "output");
            Directory.CreateDirectory(root);
            try
            {
                var owner = RewritingOutputLock.Acquire(output, TimeSpan.FromSeconds(1));
                Task release = Task.Run(async () =>
                {
                    await Task.Delay(250);
                    owner.Dispose();
                });

                using var successor = RewritingOutputLock.Acquire(output, TimeSpan.FromSeconds(3));
                await release;
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}

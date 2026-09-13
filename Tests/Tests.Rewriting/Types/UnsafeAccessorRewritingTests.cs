// Copyright (c) 2026 pipflow.com. Licensed under the GNU General Public License v3.0 or later.
#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Rewriting.Tests
{
    public class UnsafeAccessorRewritingTests : BaseRewritingTest
    {
        public UnsafeAccessorRewritingTests(ITestOutputHelper output)
            : base(output)
        {
        }

        private sealed class Entry
        {
            private int Value = 7;
            public int Read() => this.Value;
        }

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "Value")]
        private static extern ref int ValueRef(Entry entry);

        [Fact(Timeout = 5000)]
        public void TestRuntimeImplementedAccessorRemainsValidAfterRewriting()
        {
            var entry = new Entry();
            ValueRef(entry) = 19;
            Assert.Equal(19, entry.Read());
        }
    }
}
#endif

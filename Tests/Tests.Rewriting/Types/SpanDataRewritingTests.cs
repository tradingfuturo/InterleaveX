// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Rewriting.Tests
{
    /// <summary>
    /// Constant <see cref="ReadOnlySpan{T}"/> data must survive rewriting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A constant collection expression compiles to <c>RuntimeHelpers.CreateSpan&lt;T&gt;</c> over an
    /// RVA field in <c>&lt;PrivateImplementationDetails&gt;</c> — a field with no body, whose bytes sit
    /// raw in a PE section. CreateSpan hands out a pointer straight into those bytes, so the blob has
    /// to be naturally aligned for T or the runtime refuses it with "The field is invalid for
    /// initializing array or span".
    /// </para>
    /// <para>
    /// Mono.Cecil 0.11.4 broke that alignment when writing the rewritten assembly: it placed the field
    /// data in a section that was only 4-aligned AND packed the blobs back-to-back with no padding, so
    /// a single odd-sized blob misaligned every blob after it. The failure reached ANY assembly this
    /// tool rewrites, production ones included, and it was silent until the span was first touched.
    /// </para>
    /// <para>
    /// <see cref="Odd"/> is what makes this a regression test rather than a coincidence: with only
    /// blobs whose sizes are multiples of eight, tight packing preserves alignment by accident and the
    /// bug does not reproduce. The three-byte blob is what pushes the ones after it off alignment.
    /// Byte spans are included as the control — they need alignment 1, so they survived even when
    /// broken.
    /// </para>
    /// </remarks>
    public class SpanDataRewritingTests : BaseRewritingTest
    {
        public SpanDataRewritingTests(ITestOutputHelper output)
            : base(output)
        {
        }

        /// <summary>Declared first and deliberately NOT a multiple of eight. See the class remarks.</summary>
        private static ReadOnlySpan<byte> Odd => new byte[] { 1, 2, 3 };

        private static ReadOnlySpan<byte> Bytes => new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        private static ReadOnlySpan<short> Shorts => new short[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        private static ReadOnlySpan<int> Ints => new int[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        private static ReadOnlySpan<long> Longs => new long[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        private static ReadOnlySpan<double> Doubles => new double[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        [Fact(Timeout = 5000)]
        public void TestRewritingOddSizedByteSpanData()
        {
            Assert.Equal(3, Odd.Length);
            Assert.Equal(3, Odd[2]);
        }

        [Fact(Timeout = 5000)]
        public void TestRewritingByteSpanData()
        {
            Assert.Equal(8, Bytes.Length);
            Assert.Equal(8, Bytes[7]);
        }

        [Fact(Timeout = 5000)]
        public void TestRewritingShortSpanData()
        {
            Assert.Equal(8, Shorts.Length);
            Assert.Equal(8, Shorts[7]);
        }

        [Fact(Timeout = 5000)]
        public void TestRewritingIntSpanData()
        {
            Assert.Equal(8, Ints.Length);
            Assert.Equal(8, Ints[7]);
        }

        [Fact(Timeout = 5000)]
        public void TestRewritingLongSpanData()
        {
            Assert.Equal(8, Longs.Length);
            Assert.Equal(8L, Longs[7]);
        }

        [Fact(Timeout = 5000)]
        public void TestRewritingDoubleSpanData()
        {
            Assert.Equal(8, Doubles.Length);
            Assert.Equal(8d, Doubles[7]);
        }
    }
}

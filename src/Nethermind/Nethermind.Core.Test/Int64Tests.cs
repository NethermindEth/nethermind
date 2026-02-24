// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class Int64Tests
    {
        [Test]
        public void ToLongFromBytes()
        {
            byte[] bytes = Bytes.FromHexString("7fffffffffffffff");
            long number = bytes.ToLongFromBigEndianByteArrayWithoutLeadingZeros();
            number.Should().Be(long.MaxValue);
        }

        [TestCase("0000", 0L)]
        [TestCase("0001234", 0x1234L)]
        [TestCase("1234", 0x1234L)]
        [TestCase("1", 1L)]
        [TestCase("10", 16L)]
        [TestCase("7fffffffffffffff", long.MaxValue)]
        [TestCase("8000000000000000", long.MinValue)]
        [TestCase("ffffffffffffffff", -1L)]
        public void ToLongFromBytes_Vectors_match_for_array_and_span(string hexBytes, long expected)
        {
            byte[] bytes = Bytes.FromHexString(hexBytes);
            long viaArray = bytes.ToLongFromBigEndianByteArrayWithoutLeadingZeros();
            long viaSpan = bytes.AsSpan().ToLongFromBigEndianByteArrayWithoutLeadingZeros();
            viaArray.Should().Be(expected);
            viaSpan.Should().Be(expected);
        }

        [Test]
        public void ToLongFromBytes_Exact_sequence_0102030405060708()
        {
            byte[] bytes = Bytes.FromHexString("0102030405060708");
            long expected = unchecked((long)0x0102030405060708UL);
            bytes.ToLongFromBigEndianByteArrayWithoutLeadingZeros().Should().Be(expected);
            bytes.AsSpan().ToLongFromBigEndianByteArrayWithoutLeadingZeros().Should().Be(expected);
        }

        [TestCase("01ffffffffffffffff", -1L)]
        [TestCase("010000000000000000", 0L)]
        public void ToLongFromBytes_Oversized_inputs_keep_last_8_bytes(string hexBytes, long expected)
        {
            byte[] bytes = Bytes.FromHexString(hexBytes);
            bytes.ToLongFromBigEndianByteArrayWithoutLeadingZeros().Should().Be(expected);
            bytes.AsSpan().ToLongFromBigEndianByteArrayWithoutLeadingZeros().Should().Be(expected);
        }

        [Test]
        public void ToLongFromBytes_Empty_span_is_zero()
        {
            ReadOnlySpan<byte> span = ReadOnlySpan<byte>.Empty;
            long number = span.ToLongFromBigEndianByteArrayWithoutLeadingZeros();
            number.Should().Be(0L);
        }

        [Test]
        public void ToLongFromBytes_Null_array_is_zero()
        {
            byte[]? bytes = null;
            long number = bytes.ToLongFromBigEndianByteArrayWithoutLeadingZeros();
            number.Should().Be(0L);
        }
    }
}

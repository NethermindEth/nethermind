// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;
using Nethermind.Blockchain.Tracing.GethStyle;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Tracing.GethStyle;

[TestFixture]
public class GethLikeTxDirectStreamingTracerHexTests
{
    [TestCase(new byte[] { 0x00, 0x00, 0x00, 0x00 }, true, true, "0x0", TestName = "AllZeros_PrefixedTrimmed_EmitsZeroSentinel")]
    [TestCase(new byte[] { 0x00, 0x00, 0x00, 0x00 }, false, false, "00000000", TestName = "AllZeros_RawUntrimmed_EmitsAllZeroHex")]
    [TestCase(new byte[] { 0x00, 0x00, 0x00, 0x05 }, true, true, "0x5", TestName = "SingleNibble_PrefixedTrimmed_StripsLeadingZerosKeepsNibble")]
    [TestCase(new byte[] { 0x00, 0x00, 0x00, 0xab }, true, true, "0xab", TestName = "ByteValue_PrefixedTrimmed_StripsLeadingZeros")]
    [TestCase(new byte[] { 0x12, 0x34, 0x56, 0x78 }, true, true, "0x12345678", TestName = "MultiByte_PrefixedTrimmed_NoLeadingZeros")]
    [TestCase(new byte[] { 0x01, 0x23, 0x45, 0x67 }, true, true, "0x1234567", TestName = "HighNibbleZero_PrefixedTrimmed_StripsHighNibble")]
    [TestCase(new byte[] { 0xff, 0xff, 0xff, 0xff }, true, true, "0xffffffff", TestName = "AllOnes_PrefixedTrimmed_NoTrimming")]
    [TestCase(new byte[] { 0xff, 0xff, 0xff, 0xff }, false, false, "ffffffff", TestName = "AllOnes_RawUntrimmed_NoPrefix")]
    public void FormatHexAscii_FormatsCorrectly(byte[] input, bool withPrefix, bool trimLeadingZeros, string expected)
    {
        Span<byte> output = stackalloc byte[64];
        int written = GethLikeTxDirectStreamingTracer.FormatHexAscii(input, output, withPrefix, trimLeadingZeros);
        string actual = Encoding.ASCII.GetString(output[..written]);

        Assert.That(actual, Is.EqualTo(expected), "FormatHexAscii must produce Geth-compatible hex output");
    }

    [Test]
    public void FormatHexAscii_FullEvmWord_EmitsExactly64HexChars()
    {
        byte[] input = new byte[32];
        for (int i = 0; i < 32; i++) input[i] = (byte)(i * 7);

        Span<byte> output = stackalloc byte[64];
        int written = GethLikeTxDirectStreamingTracer.FormatHexAscii(input, output, withPrefix: false, trimLeadingZeros: false);

        Assert.That(written, Is.EqualTo(64), "a 32-byte word must produce exactly 64 hex chars when untrimmed");
        Assert.That(Encoding.ASCII.GetString(output[..written]), Is.EqualTo("00070e151c232a31383f464d545b626970777e858c939aa1a8afb6bdc4cbd2d9"), "byte-by-byte hex encoding must match the reference output");
    }
}

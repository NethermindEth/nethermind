// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Optimism.CL.Decoding;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.CL;

public class BlobDecoderTests
{
    private static IEnumerable<TestCaseData> HexEncodedBlobs()
    {
        yield return new TestCaseData(
            "0x24000000287468697320697320612074657374206F6620626C6F6220656E636F10696E672F6465636F64696E67" + new string('0', 262054),
            "0x7468697320697320612074657374206F6620626C6F6220656E636F64696E672F6465636F64696E67").SetName("Long string");

        yield return new TestCaseData(
            "0x000000000573686f7274" + new string('0', 262124),
            "0x73686f7274").SetName("Short string");

        yield return new TestCaseData(
            "0x00000000030001" + new string('0', 262130),
            "0x000100").SetName("0x00_01_00");

        yield return new TestCaseData(
            "0x" + new string('0', 262144),
            "0x").SetName("Empty blob");

        yield return new TestCaseData(
            "0x0000000001" + new string('0', 262134),
            new byte[1].ToHexString(withZeroX: true)).SetName("Length 1");

        yield return new TestCaseData(
            "0x000000001B" + new string('0', 262134),
            new byte[27].ToHexString(withZeroX: true)).SetName("Length 27");

        yield return new TestCaseData(
            "0x000000001A" + new string('0', 262134),
            new byte[26].ToHexString(withZeroX: true)).SetName("Length 26");

        yield return new TestCaseData(
            "0x0000000019" + new string('0', 262134),
            new byte[25].ToHexString(withZeroX: true)).SetName("Length 25");

        yield return new TestCaseData(
            "0x000001FBFC" + new string('0', 262134),
            new byte[0x01FBFC].ToHexString(withZeroX: true)).SetName("Max length");

        yield return new TestCaseData(
            "0x3F00000020FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF30FFFFFFFF" + new string('0', 262070),
            Enumerable.Repeat((byte)0xFF, 32).ToArray().ToHexString(withZeroX: true)).SetName("32x 0xFF");
    }

    [TestCaseSource(nameof(HexEncodedBlobs))]
    public void DecodeBlob(string hexEncodedBlob, string hexExpected)
    {
        var hexEncoded = Bytes.FromHexString(hexEncodedBlob);
        var expected = Bytes.FromHexString(hexExpected);

        var decoded = DecodeBlob(hexEncoded);
        decoded.Should().BeEquivalentTo(expected);
    }

    private static IEnumerable<TestCaseData> InvalidEncodedBlobs()
    {
        byte[] ValidEncodedBlob()
        {
            var hexBlob = "0x2c000000277468697320697320612074657374206f6620696e76616c69642062106f62206465636f64696e67" + new string('0', 262056);
            return Bytes.FromHexString(hexBlob);
        }

        {
            var bytes = ValidEncodedBlob();
            bytes[32] = 0b10000000;
            yield return new TestCaseData(bytes).SetName("Highest order bit set");
        }
        {
            var bytes = ValidEncodedBlob();
            bytes[32] = 0b010000000;
            yield return new TestCaseData(bytes).SetName("Second highest order bit set");
        }
        {
            var bytes = ValidEncodedBlob();
            bytes[1] = 0x01;
            yield return new TestCaseData(bytes).SetName("Invalid encoding version");
        }
        {
            var bytes = ValidEncodedBlob();
            bytes[2] = 0xFF;
            yield return new TestCaseData(bytes).SetName("Too long length prefix");
        }
        {
            var bytes = ValidEncodedBlob();
            bytes[2] = 0x01;
            bytes[3] = 0xFB;
            bytes[3] = 0xFD;
            yield return new TestCaseData(bytes).SetName("Greater than blob capacity");
        }
    }

    [TestCaseSource(nameof(InvalidEncodedBlobs))]
    public void DecodeBlob_InvalidEncodedBlob(byte[] encoded)
    {
        var tryDecode = () => DecodeBlob(encoded);
        tryDecode.Should().Throw<FormatException>();
    }

    /// <remarks>
    /// Wrapper intended to be easily used in tests
    /// </remarks>
    private static byte[] DecodeBlob(byte[] blob)
    {
        byte[] buffer = new byte[BlobDecoder.MaxBlobDataSize];
        int length = BlobDecoder.DecodeBlob(blob, buffer);
        return buffer[..length];
    }
}

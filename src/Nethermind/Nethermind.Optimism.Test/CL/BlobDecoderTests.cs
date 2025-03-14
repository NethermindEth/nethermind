// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
            "0x24000000287468697320697320612074657374206f6620626c6f6220656e636f10696e672f6465636f64696e67" + new string('0', 262054),
            "0x7468697320697320612074657374206f6620626c6f6220656e636f64696e672f6465636f64696e67").SetName("Long string");

        yield return new TestCaseData(
            "0x000000000573686f7274" + new string('0', 262124),
            "0x73686f7274").SetName("Short string");

        yield return new TestCaseData(
            "0x0000000001" + new string('0', 262134),
            "0x00").SetName("0x00");

        yield return new TestCaseData(
            "0x00000000030001" + new string('0', 262130),
            "0x000100").SetName("0x000100");

        yield return new TestCaseData(
            "0x000000001b" + new string('0', 262134),
            new byte[27].ToHexString(withZeroX: true)).SetName("27x 0x00");

        yield return new TestCaseData(
            "0x000000001a" + new string('0', 262134),
            new byte[26].ToHexString(withZeroX: true)).SetName("26x 0x00");

        yield return new TestCaseData(
            "0x0000000019" + new string('0', 262134),
            new byte[25].ToHexString(withZeroX: true)).SetName("25x 0x00");
    }

    [TestCaseSource(nameof(HexEncodedBlobs))]
    public void DecodeBlob(string hexEncodedBlob, string hexExpected)
    {
        var hexEncoded = Bytes.FromHexString(hexEncodedBlob);
        var expected = Bytes.FromHexString(hexExpected);

        var decoded = BlobDecoder.DecodeBlob(hexEncoded);
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
    }

    [TestCaseSource(nameof(InvalidEncodedBlobs))]
    public void DecodeBlob_InvalidEncodedBlob(byte[] encoded)
    {
        var tryDecode = () => BlobDecoder.DecodeBlob(encoded);
        tryDecode.Should().Throw<FormatException>();
    }
}

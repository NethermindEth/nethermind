// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

[TestFixture]
public class BlockBodyDecoderTests
{
    [TestCaseSource(nameof(ValidBodies))]
    public void Roundtrip(BlockBody body)
    {
        int length = BlockBodyDecoder.Instance.GetLength(body, RlpBehaviors.None);
        byte[] bytes = new byte[length];
        RlpWriter writer = new(bytes);
        BlockBodyDecoder.Instance.Encode(ref writer, body);
        RlpReader ctx = new(bytes);
        BlockBody decodedBody = BlockBodyDecoder.Instance.Decode(ref ctx);

        Assert.That(decodedBody, Is.EqualTo(body).UsingBlockBodyComparer());
    }

    // check for RlpLimitException specifically, which should fire before decoding, so 0xC0 placeholders are fine here.
    [TestCase(60_000, 0, null, TestName = "transactions")]
    [TestCase(0, 3, null, TestName = "uncles")]
    [TestCase(0, 0, 64_001, TestName = "withdrawals")]
    public void Decode_count_over_limit_throws(int txCount, int uncleCount, int? withdrawalCount) =>
        Assert.Throws<RlpLimitException>(() => DecodeBody(BuildBodyStream(txCount, uncleCount, withdrawalCount)));

    // array of 0xC0's (interpreted as null) within the count limit
    [TestCase(10, 0, null, TestName = "transaction")]
    [TestCase(0, 1, null, TestName = "uncle")]
    [TestCase(0, 0, 6, TestName = "withdrawal")]
    public void Decode_null_element_throws(int txCount, int uncleCount, int? withdrawalCount) =>
        Assert.Throws<RlpException>(() => DecodeBody(BuildBodyStream(txCount, uncleCount, withdrawalCount)));

    private static IEnumerable<TestCaseData> ValidBodies()
    {
        yield return new TestCaseData(new BlockBody(
            [Build.A.Transaction.Signed().TestObject, Build.A.Transaction.Signed().TestObject], [])
        ).SetName("transactions");

        yield return new TestCaseData(new BlockBody(
            [], [Build.A.BlockHeader.TestObject, Build.A.BlockHeader.TestObject])
        ).SetName("uncles");

        yield return new TestCaseData(new BlockBody(
            [], [], [Build.A.Withdrawal.TestObject, Build.A.Withdrawal.TestObject])
        ).SetName("withdrawals");

        yield return new TestCaseData(new BlockBody(
            [Build.A.Transaction.Signed().TestObject, Build.A.Transaction.Signed().TestObject], [],
            [Build.A.Withdrawal.TestObject, Build.A.Withdrawal.TestObject])
        ).SetName("transactions + withdrawals");
    }

    private static void DecodeBody(byte[] bytes)
    {
        RlpReader ctx = new(bytes);
        BlockBodyDecoder.Instance.DecodeUnwrapped(ref ctx, bytes.Length);
    }

    private static byte[] BuildBodyStream(int txCount, int uncleCount, int? withdrawalCount)
    {
        int totalLength = Rlp.LengthOfSequence(txCount)
                        + Rlp.LengthOfSequence(uncleCount)
                        + (withdrawalCount.HasValue ? Rlp.LengthOfSequence(withdrawalCount.Value) : 0);

        byte[] bytes = new byte[totalLength];
        RlpWriter writer = new(bytes);
        WriteEmptyItems(ref writer, txCount);
        WriteEmptyItems(ref writer, uncleCount);
        if (withdrawalCount.HasValue)
            WriteEmptyItems(ref writer, withdrawalCount.Value);

        return bytes;

        static void WriteEmptyItems(ref RlpWriter writer, int count)
        {
            writer.StartSequence(count);
            for (int i = 0; i < count; i++)
                writer.StartSequence(0); // 0xC0 - empty-list placeholder (decodes as null)
        }
    }
}

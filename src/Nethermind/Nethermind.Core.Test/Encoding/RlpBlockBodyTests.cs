// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public class RlpBlockBodyTests
{
    private static IEnumerable<TestCaseData> BlockCases()
    {
        yield return new TestCaseData(Build.A.Block.TestObject).SetName("Empty body");
        yield return new TestCaseData(Build.A.Block
            .WithTransactions(
                Build.A.Transaction.SignedAndResolved().TestObject,
                Build.A.Transaction.WithNonce(1).SignedAndResolved().TestObject)
            .WithUncles(Build.A.BlockHeader.TestObject)
            .TestObject).SetName("Transactions and uncles, no withdrawals");
        yield return new TestCaseData(Build.A.Block
            .WithTransactions(
                Build.A.Transaction.WithType(TxType.EIP1559).WithMaxFeePerGas(2).SignedAndResolved().TestObject,
                Build.A.Transaction.SignedAndResolved().TestObject)
            .WithWithdrawals(2)
            .TestObject).SetName("Typed transaction and withdrawals");
    }

    [TestCaseSource(nameof(BlockCases))]
    public void From_stored_block_slices_body_that_matches_encoder_output(Block block)
    {
        byte[] blockRlp = new BlockDecoder().Encode(block).Bytes;
        byte[] expectedBodyItem = new byte[BlockBodyDecoder.Instance.GetLength(block.Body, RlpBehaviors.None)];
        RlpWriter expectedWriter = new(expectedBodyItem);
        BlockBodyDecoder.Instance.Encode(ref expectedWriter, block.Body);

        using RlpBlockBody rawBody = RlpBlockBody.FromStoredBlock(MemoryPool<byte>.Shared.Rent(0), blockRlp);

        Assert.That(rawBody.RlpLength, Is.EqualTo(expectedBodyItem.Length));
        byte[] written = new byte[rawBody.RlpLength];
        RlpWriter writer = new(written);
        rawBody.Write(ref writer);
        Assert.That(written, Is.EqualTo(expectedBodyItem));

        BlockBody decoded = rawBody.Decode();
        Assert.That(decoded.Transactions, Has.Length.EqualTo(block.Transactions.Length));
        Assert.That(decoded.Uncles, Has.Length.EqualTo(block.Uncles.Length));
        Assert.That(decoded.Withdrawals?.Length, Is.EqualTo(block.Withdrawals?.Length));
        Assert.That(rawBody.WithdrawalsSequence.HasValue, Is.EqualTo(block.Withdrawals is not null));
    }

    [TestCase("0xc0", TestName = "Empty list")]
    [TestCase("0xc1c0", TestName = "Single sub-list")]
    [TestCase("0xc4c0c0c0c0", TestName = "Four sub-lists")]
    [TestCase("0xc28080", TestName = "Non-list sub-items")]
    [TestCase("0xc3c0c001", TestName = "Trailing non-list byte")]
    [TestCase("0xc3c0c0", TestName = "Declared length past the end")]
    public void Malformed_body_item_throws(string hex) =>
        Assert.That(
            () => RlpBlockBody.FromBodyItem(MemoryPool<byte>.Shared.Rent(0), Bytes.FromHexString(hex)),
            Throws.InstanceOf<RlpException>());
}

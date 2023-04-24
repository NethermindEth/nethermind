// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Receipts;

public class ReceiptsIteratorTests
{
    ReceiptArrayStorageDecoder _decoder = ReceiptArrayStorageDecoder.Instance;

    [Test]
    public void SmokeTestWithRecovery()
    {
        Block block = Build.A
            .Block
            .WithTransactions(3, MainnetSpecProvider.Instance)
            .TestObject;
        TxReceipt[] receipts = new[]
        {
            Build.A.Receipt.WithAllFieldsFilled.WithSender(TestItem.AddressA).TestObject,
            Build.A.Receipt.WithAllFieldsFilled.WithSender(TestItem.AddressB).TestObject,
            Build.A.Receipt.WithAllFieldsFilled.WithSender(TestItem.AddressC).TestObject,
        };

        ReceiptsIterator iterator = CreateIterator(receipts, block);

        iterator.TryGetNext(out TxReceiptStructRef receipt).Should().BeTrue();
        iterator.RecoverIfNeeded(ref receipt);
        receipt.Sender.Bytes.ToArray().Should().BeEquivalentTo(TestItem.AddressA.Bytes);
        receipt.TxHash.Bytes.ToArray().Should().BeEquivalentTo(block.Transactions[0].Hash.Bytes);
        iterator.TryGetNext(out receipt).Should().BeTrue();
        iterator.RecoverIfNeeded(ref receipt);
        receipt.Sender.Bytes.ToArray().Should().BeEquivalentTo(TestItem.AddressB.Bytes);
        receipt.TxHash.Bytes.ToArray().Should().BeEquivalentTo(block.Transactions[1].Hash.Bytes);
        iterator.TryGetNext(out receipt).Should().BeTrue();
        iterator.RecoverIfNeeded(ref receipt);
        receipt.Sender.Bytes.ToArray().Should().BeEquivalentTo(TestItem.AddressC.Bytes);
        receipt.TxHash.Bytes.ToArray().Should().BeEquivalentTo(block.Transactions[1].Hash.Bytes);
    }

    [Test]
    public void SmokeTestWithDelayedRecovery()
    {
        Block block = Build.A
            .Block
            .WithTransactions(3, MainnetSpecProvider.Instance)
            .TestObject;
        TxReceipt[] receipts = new[]
        {
            Build.A.Receipt.WithAllFieldsFilled.WithSender(TestItem.AddressA).TestObject,
            Build.A.Receipt.WithAllFieldsFilled.WithSender(TestItem.AddressB).TestObject,
            Build.A.Receipt.WithAllFieldsFilled.WithSender(TestItem.AddressC).TestObject,
        };

        ReceiptsIterator iterator = CreateIterator(receipts, block);

        iterator.TryGetNext(out TxReceiptStructRef receipt).Should().BeTrue();
        receipt.Sender.Bytes.ToArray().Should().BeEquivalentTo(TestItem.AddressA.Bytes);
        receipt.TxHash.Bytes.Length.Should().Be(0);
        iterator.TryGetNext(out receipt).Should().BeTrue();
        receipt.Sender.Bytes.ToArray().Should().BeEquivalentTo(TestItem.AddressB.Bytes);
        receipt.TxHash.Bytes.Length.Should().Be(0);
        iterator.TryGetNext(out receipt).Should().BeTrue();
        iterator.RecoverIfNeeded(ref receipt);
        receipt.Sender.Bytes.ToArray().Should().BeEquivalentTo(TestItem.AddressC.Bytes);
        receipt.TxHash.Bytes.ToArray().Should().BeEquivalentTo(block.Transactions[1].Hash.Bytes);
    }

    [Test]
    public void SmokeTest()
    {
        Block block = Build.A
            .Block
            .WithTransactions(3, MainnetSpecProvider.Instance)
            .TestObject;
        TxReceipt[] receipts = new[]
        {
            Build.A.Receipt.WithAllFieldsFilled.WithSender(TestItem.AddressA).TestObject,
            Build.A.Receipt.WithAllFieldsFilled.WithSender(TestItem.AddressB).TestObject,
            Build.A.Receipt.WithAllFieldsFilled.WithSender(TestItem.AddressC).TestObject,
        };

        ReceiptsIterator iterator = CreateIterator(receipts, block);

        iterator.TryGetNext(out TxReceiptStructRef receipt).Should().BeTrue();
        receipt.Sender.Bytes.ToArray().Should().BeEquivalentTo(TestItem.AddressA.Bytes);
        iterator.TryGetNext(out receipt).Should().BeTrue();
        receipt.Sender.Bytes.ToArray().Should().BeEquivalentTo(TestItem.AddressB.Bytes);
        iterator.TryGetNext(out receipt).Should().BeTrue();
        receipt.Sender.Bytes.ToArray().Should().BeEquivalentTo(TestItem.AddressC.Bytes);
    }

    private ReceiptsIterator CreateIterator(TxReceipt[] receipts, Block block)
    {
        using NettyRlpStream stream = _decoder.EncodeToNewNettyStream(receipts, RlpBehaviors.Storage);
        Span<byte> span = stream.AsSpan();
        TestMemDb blockDb = new TestMemDb();
        ReceiptsRecovery recovery = new ReceiptsRecovery(
            new EthereumEcdsa(MainnetSpecProvider.Instance.ChainId, LimboLogs.Instance),
            MainnetSpecProvider.Instance,
            false
        );

        ReceiptsIterator iterator = new ReceiptsIterator(span, blockDb, () => recovery.CreateRecoveryContext(block), _decoder.GetRefDecoder(span));
        return iterator;
    }
}

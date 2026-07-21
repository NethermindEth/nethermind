// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Receipts;

[Parallelizable(ParallelScope.All)]
public class ReceiptsIteratorTests
{
    private readonly ReceiptArrayStorageDecoder _decoder = ReceiptArrayStorageDecoder.Instance;

    [Test]
    public void SmokeTestWithRecovery()
    {
        Block block = Build.A
            .Block
            .WithTransactions(3, MainnetSpecProvider.Instance)
            .TestObject;
        TxReceipt[] receipts =
        [
            Build.A.Receipt.WithAllFieldsFilled.WithSender(TestItem.AddressA).TestObject,
            Build.A.Receipt.WithAllFieldsFilled.WithSender(TestItem.AddressB).TestObject,
            Build.A.Receipt.WithAllFieldsFilled.WithSender(TestItem.AddressC).TestObject
        ];

        ReceiptsIterator iterator = CreateIterator(receipts, block);

        Assert.That(iterator.TryGetNext(out TxReceiptStructRef receipt), Is.True);
        iterator.RecoverIfNeeded(ref receipt);
        Assert.That(receipt.Sender.Bytes.ToArray(), Is.EqualTo(TestItem.AddressA.Bytes.ToArray()));
        Assert.That(receipt.TxHash.Bytes.ToArray(), Is.EqualTo(block.Transactions[0].Hash!.BytesToArray()));
        Assert.That(iterator.TryGetNext(out receipt), Is.True);
        iterator.RecoverIfNeeded(ref receipt);
        Assert.That(receipt.Sender.Bytes.ToArray(), Is.EqualTo(TestItem.AddressB.Bytes.ToArray()));
        Assert.That(receipt.TxHash.Bytes.ToArray(), Is.EqualTo(block.Transactions[1].Hash!.BytesToArray()));
        Assert.That(iterator.TryGetNext(out receipt), Is.True);
        iterator.RecoverIfNeeded(ref receipt);
        Assert.That(receipt.Sender.Bytes.ToArray(), Is.EqualTo(TestItem.AddressC.Bytes.ToArray()));
        Assert.That(receipt.TxHash.Bytes.ToArray(), Is.EqualTo(block.Transactions[1].Hash!.BytesToArray()));
    }

    [Test]
    public void SmokeTestWithDelayedRecovery()
    {
        Block block = Build.A
            .Block
            .WithTransactions(3, MainnetSpecProvider.Instance)
            .TestObject;
        TxReceipt[] receipts =
        {
            Build.A.Receipt.WithAllFieldsFilled.WithSender(TestItem.AddressA).TestObject,
            Build.A.Receipt.WithAllFieldsFilled.WithSender(TestItem.AddressB).TestObject,
            Build.A.Receipt.WithAllFieldsFilled.WithSender(TestItem.AddressC).TestObject,
        };

        ReceiptsIterator iterator = CreateIterator(receipts, block);

        Assert.That(iterator.TryGetNext(out TxReceiptStructRef receipt), Is.True);
        Assert.That(receipt.Sender.Bytes.ToArray(), Is.EqualTo(TestItem.AddressA.Bytes.ToArray()));
        Assert.That(receipt.TxHash.Bytes.Length, Is.EqualTo(0));
        Assert.That(iterator.TryGetNext(out receipt), Is.True);
        Assert.That(receipt.Sender.Bytes.ToArray(), Is.EqualTo(TestItem.AddressB.Bytes.ToArray()));
        Assert.That(receipt.TxHash.Bytes.Length, Is.EqualTo(0));
        Assert.That(iterator.TryGetNext(out receipt), Is.True);
        iterator.RecoverIfNeeded(ref receipt);
        Assert.That(receipt.Sender.Bytes.ToArray(), Is.EqualTo(TestItem.AddressC.Bytes.ToArray()));
        Assert.That(receipt.TxHash.Bytes.ToArray(), Is.EqualTo(block.Transactions[1].Hash!.BytesToArray()));
    }

    [Test]
    public void SmokeTest()
    {
        Block block = Build.A
            .Block
            .WithTransactions(3, MainnetSpecProvider.Instance)
            .TestObject;
        TxReceipt[] receipts =
        [
            Build.A.Receipt.WithAllFieldsFilled.WithSender(TestItem.AddressA).TestObject,
            Build.A.Receipt.WithAllFieldsFilled.WithSender(TestItem.AddressB).TestObject,
            Build.A.Receipt.WithAllFieldsFilled.WithSender(TestItem.AddressC).TestObject
        ];

        ReceiptsIterator iterator = CreateIterator(receipts, block);

        Assert.That(iterator.TryGetNext(out TxReceiptStructRef receipt), Is.True);
        Assert.That(receipt.Sender.Bytes.ToArray(), Is.EqualTo(TestItem.AddressA.Bytes.ToArray()));
        Assert.That(iterator.TryGetNext(out receipt), Is.True);
        Assert.That(receipt.Sender.Bytes.ToArray(), Is.EqualTo(TestItem.AddressB.Bytes.ToArray()));
        Assert.That(iterator.TryGetNext(out receipt), Is.True);
        Assert.That(receipt.Sender.Bytes.ToArray(), Is.EqualTo(TestItem.AddressC.Bytes.ToArray()));
    }

    private ReceiptsIterator CreateIterator(TxReceipt[] receipts, Block block)
    {
        using ArrayPoolSpan<byte> stream = _decoder.EncodeToArrayPoolSpan(receipts, RlpBehaviors.Storage);
        Span<byte> span = stream;
        TestMemDb blockDb = new();
        ReceiptsRecovery recovery = new(
            new EthereumEcdsa(MainnetSpecProvider.Instance.ChainId),
            MainnetSpecProvider.Instance,
            false
        );

        ReceiptsIterator iterator = new(span, blockDb, () => recovery.CreateRecoveryContext(new ReceiptRecoveryBlock(block)), _decoder.GetRefDecoder(span));
        return iterator;
    }
}

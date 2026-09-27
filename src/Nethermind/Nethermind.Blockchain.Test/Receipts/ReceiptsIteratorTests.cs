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

    [Test]
    public void Legacy_missing_log_entries_are_skipped()
    {
        LogEntry first = Build.A.LogEntry.WithAddress(TestItem.AddressA).TestObject;
        LogEntry second = Build.A.LogEntry.WithAddress(TestItem.AddressB).TestObject;
        Rlp encoded = Rlp.Encode(Rlp.Encode(first), Rlp.OfEmptyList, Rlp.Encode(second));
        LogEntriesIterator iterator = new(encoded.Bytes, new ReceiptStorageDecoder());

        Assert.That(iterator.TryGetNext(out LogEntryStructRef log), Is.True);
        Assert.That(log.Address == TestItem.AddressA, Is.True);
        Assert.That(iterator.Index, Is.Zero);
        Assert.That(iterator.TryGetNext(out log), Is.True);
        Assert.That(log.Address == TestItem.AddressB, Is.True);
        Assert.That(iterator.Index, Is.EqualTo(1));
        Assert.That(iterator.TryGetNext(out _), Is.False);

        iterator.Reset();
        Assert.That(iterator.TrySkipNext(), Is.True);
        Assert.That(iterator.Index, Is.Zero);
        Assert.That(iterator.TrySkipNext(), Is.True);
        Assert.That(iterator.Index, Is.EqualTo(1));
        Assert.That(iterator.TrySkipNext(), Is.False);
    }

    // Recovery starts at the second receipt, so the first one is replayed through the log context too.
    [Test]
    public void RecoverLogFieldsIfNeeded_WithEncodedTransactions_RecoversBlockAndTransactionIdentity()
    {
        Transaction[] transactions =
        [
            Build.A.Transaction.WithNonce(1).WithType(TxType.Legacy).Signed().TestObject,
            Build.A.Transaction.WithNonce(2).WithType(TxType.AccessList).Signed().TestObject,
            Build.A.Transaction.WithNonce(3).WithType(TxType.EIP1559).Signed().TestObject,
        ];
        Block block = Build.A.Block.WithNumber(7).WithBaseFeePerGas(1).WithTransactions(transactions).TestObject;
        block.Header.Hash = block.Header.CalculateHash();
        TxReceipt[] receipts = [Build.A.Receipt.TestObject, Build.A.Receipt.TestObject, Build.A.Receipt.TestObject];
        using ArrayPoolSpan<byte> stream = _decoder.EncodeToArrayPoolSpan(receipts, RlpBehaviors.Storage);
        Span<byte> span = stream;
        BlockDecoder blockDecoder = new();
        byte[] encodedBlock = blockDecoder.Encode(block).Bytes;
        ReceiptsRecovery recovery = new(new EthereumEcdsa(MainnetSpecProvider.Instance.ChainId), MainnetSpecProvider.Instance, false);
        ReceiptsIterator iterator = new(span, new TestMemDb(), static () => null, _decoder.GetRefDecoder(span),
            () => recovery.CreateLogRecoveryContext(blockDecoder.DecodeToReceiptRecoveryBlock(null, encodedBlock, RlpBehaviors.None)!.Value));

        Assert.That(iterator.TryGetNext(out _), Is.True, "precondition: the first receipt is read before recovery");
        for (int i = 1; i < transactions.Length; i++)
        {
            Assert.That(iterator.TryGetNext(out TxReceiptStructRef receipt), Is.True, $"receipt {i} must be read");
            iterator.RecoverLogFieldsIfNeeded(ref receipt);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(receipt.TxHash.ToCommitment(), Is.EqualTo(transactions[i].Hash), $"receipt {i} must carry its transaction's hash");
                Assert.That(receipt.BlockHash.ToCommitment(), Is.EqualTo(block.Hash), $"receipt {i} must carry the block hash");
                Assert.That(receipt.BlockNumber, Is.EqualTo(block.Number), $"receipt {i} must carry the block number");
                Assert.That(receipt.Index, Is.EqualTo(i), $"receipt {i} must carry its transaction index");
            }
        }
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

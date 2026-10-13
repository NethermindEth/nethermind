// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Autofac;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
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
        Assert.That(receipt.Sender.Bytes, Is.SequenceEqualTo(TestItem.AddressA.Bytes));
        Assert.That(receipt.TxHash.Bytes, Is.SequenceEqualTo(block.Transactions[0].Hash!.BytesToArray()));
        Assert.That(iterator.TryGetNext(out receipt), Is.True);
        iterator.RecoverIfNeeded(ref receipt);
        Assert.That(receipt.Sender.Bytes, Is.SequenceEqualTo(TestItem.AddressB.Bytes));
        Assert.That(receipt.TxHash.Bytes, Is.SequenceEqualTo(block.Transactions[1].Hash!.BytesToArray()));
        Assert.That(iterator.TryGetNext(out receipt), Is.True);
        iterator.RecoverIfNeeded(ref receipt);
        Assert.That(receipt.Sender.Bytes, Is.SequenceEqualTo(TestItem.AddressC.Bytes));
        Assert.That(receipt.TxHash.Bytes, Is.SequenceEqualTo(block.Transactions[1].Hash!.BytesToArray()));
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
        Assert.That(receipt.Sender.Bytes, Is.SequenceEqualTo(TestItem.AddressA.Bytes));
        Assert.That(receipt.TxHash.Bytes.Length, Is.EqualTo(0));
        Assert.That(iterator.TryGetNext(out receipt), Is.True);
        Assert.That(receipt.Sender.Bytes, Is.SequenceEqualTo(TestItem.AddressB.Bytes));
        Assert.That(receipt.TxHash.Bytes.Length, Is.EqualTo(0));
        Assert.That(iterator.TryGetNext(out receipt), Is.True);
        iterator.RecoverIfNeeded(ref receipt);
        Assert.That(receipt.Sender.Bytes, Is.SequenceEqualTo(TestItem.AddressC.Bytes));
        Assert.That(receipt.TxHash.Bytes, Is.SequenceEqualTo(block.Transactions[1].Hash!.BytesToArray()));
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
        Assert.That(receipt.Sender.Bytes, Is.SequenceEqualTo(TestItem.AddressA.Bytes));
        Assert.That(iterator.TryGetNext(out receipt), Is.True);
        Assert.That(receipt.Sender.Bytes, Is.SequenceEqualTo(TestItem.AddressB.Bytes));
        Assert.That(iterator.TryGetNext(out receipt), Is.True);
        Assert.That(receipt.Sender.Bytes, Is.SequenceEqualTo(TestItem.AddressC.Bytes));
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

    [Test]
    public void RecoverLogFieldsIfNeeded_PreservesTransactionIdentityWithDeferredRecovery(
        [Values] bool fromEncoded, [Values] bool useLogContext, [Values(0, 1)] int firstMatch, [Values(1, 2)] int matchInterval)
    {
        Transaction[] transactions =
        [
            Build.A.Transaction.WithNonce(1).WithType(TxType.Legacy).Signed().TestObject,
            Build.A.Transaction.WithNonce(2).WithType(TxType.AccessList).Signed().TestObject,
            Build.A.Transaction.WithNonce(3).WithType(TxType.EIP1559).Signed().TestObject,
            Build.A.Transaction.WithNonce(4).WithType(TxType.Legacy).Signed().TestObject,
            Build.A.Transaction.WithNonce(5).WithType(TxType.AccessList).Signed().TestObject,
        ];
        Block block = Build.A.Block.WithNumber(7).WithBaseFeePerGas(1).WithTransactions(transactions).TestObject;
        block.Header.Hash = block.Header.CalculateHash();
        Hash256[] expectedHashes = new Hash256[transactions.Length];
        TxReceipt[] receipts = new TxReceipt[transactions.Length];
        for (int i = 0; i < transactions.Length; i++)
        {
            expectedHashes[i] = transactions[i].Hash!;
            receipts[i] = Build.A.Receipt.TestObject;
        }
        using ArrayPoolSpan<byte> stream = _decoder.EncodeToArrayPoolSpan(receipts, RlpBehaviors.Storage);
        Span<byte> span = stream;
        BlockDecoder blockDecoder = new();
        byte[] encodedBlock = blockDecoder.Encode(block).Bytes;
        CountingMemoryManager? blockMemory = fromEncoded ? new(encodedBlock) : null;
        if (!fromEncoded && useLogContext)
        {
            foreach (Transaction transaction in transactions) transaction.Hash = null;
        }
        using IContainer container = CreateRecoveryContainer();
        IReceiptsRecovery recovery = container.Resolve<IReceiptsRecovery>();
        using TestMemDb receiptDb = new();
        ReceiptsIterator iterator = new(span, receiptDb, static () => null, _decoder.GetRefDecoder(span),
            CreateLogRecoveryContext);

        IReceiptsRecovery.IRecoveryContext CreateLogRecoveryContext()
        {
            ReceiptRecoveryBlock recoveryBlock = fromEncoded
                ? blockDecoder.DecodeToReceiptRecoveryBlock(blockMemory, blockMemory!.Memory, RlpBehaviors.None)!.Value
                : new ReceiptRecoveryBlock(block);
            return useLogContext
                ? recovery.CreateLogRecoveryContext(recoveryBlock)
                : recovery.CreateRecoveryContext(recoveryBlock);
        }

        try
        {
            for (int i = 0; i < transactions.Length; i++)
            {
                Assert.That(iterator.TryGetNext(out TxReceiptStructRef receipt), Is.True, $"receipt {i} must be read");
                bool matches = i >= firstMatch && (i - firstMatch) % matchInterval == 0;
                if (!matches)
                {
                    if (useLogContext || i < firstMatch)
                    {
                        Assert.That(receipt.TxHash.Bytes.Length, Is.Zero, $"receipt {i} must not be recovered without a match");
                    }
                    continue;
                }

                iterator.RecoverLogFieldsIfNeeded(ref receipt, out Hash256? recoveredTransactionHash);
                iterator.RecoverLogFieldsIfNeeded(ref receipt, out Hash256? repeatedTransactionHash);
                if (useLogContext)
                {
                    Assert.That(receipt.TxHash.Bytes.Length, Is.Zero, "direct recovery must not construct a hash span");
                }
                iterator.RecoverLogFieldsIfNeeded(ref receipt);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(recoveredTransactionHash, Is.EqualTo(useLogContext ? expectedHashes[i] : null), "only selective recovery supplies a direct hash");
                    Assert.That(repeatedTransactionHash, Is.SameAs(recoveredTransactionHash), "repeated recovery reuses the hash object");
                    Assert.That(receipt.TxHash.ToCommitment(), Is.EqualTo(expectedHashes[i]), $"receipt {i} must carry its transaction's hash");
                    Assert.That(receipt.BlockHash.ToCommitment(), Is.EqualTo(block.Hash), $"receipt {i} must carry the block hash");
                    Assert.That(receipt.BlockNumber, Is.EqualTo(block.Number), $"receipt {i} must carry the block number");
                    Assert.That(receipt.Index, Is.EqualTo(i), $"receipt {i} must carry its transaction index");
                }
            }

            Assert.That(iterator.TryGetNext(out _), Is.False);
            if (!fromEncoded && useLogContext)
            {
                for (int i = 0; i < transactions.Length; i++)
                {
                    bool matches = i >= firstMatch && (i - firstMatch) % matchInterval == 0;
                    Assert.That(transactions[i].IsHashCalculated, Is.EqualTo(matches), $"transaction {i}");
                }
            }
        }
        finally
        {
            iterator.Dispose();
        }

        if (fromEncoded) Assert.That(blockMemory!.DisposeCount, Is.EqualTo(1));
    }

    [Test]
    public void LogRecoveryRejectsReceiptPastLastTransaction([Values] bool fromEncoded, [Values(1, 2)] int transactionIndex)
    {
        Block block = Build.A.Block.WithTransactions(Build.A.Transaction.Signed().TestObject).TestObject;
        BlockDecoder blockDecoder = new();
        byte[] encodedBlock = blockDecoder.Encode(block).Bytes;
        ReceiptRecoveryBlock recoveryBlock = fromEncoded
            ? blockDecoder.DecodeToReceiptRecoveryBlock(null, encodedBlock, RlpBehaviors.None)!.Value
            : new ReceiptRecoveryBlock(block);
        using IContainer container = CreateRecoveryContainer();
        IReceiptsRecovery recovery = container.Resolve<IReceiptsRecovery>();
        using IReceiptsRecovery.ISelectiveLogRecoveryContext context =
            (IReceiptsRecovery.ISelectiveLogRecoveryContext)recovery.CreateLogRecoveryContext(recoveryBlock);

        Assert.Throws<InvalidOperationException>(() =>
        {
            TxReceiptStructRef receipt = default;
            context.RecoverReceiptData(ref receipt, transactionIndex);
        });
    }

    private static IContainer CreateRecoveryContainer() => new ContainerBuilder()
        .AddModule(new TestNethermindModule(new ReceiptConfig { CompactReceiptStore = true }))
        .AddSingleton<ISpecProvider>(MainnetSpecProvider.Instance)
        .Build();

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

    private sealed class CountingMemoryManager(byte[] bytes) : MemoryManager<byte>
    {
        public int DisposeCount { get; private set; }
        public override Span<byte> GetSpan() => bytes;
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) => DisposeCount++;
    }
}

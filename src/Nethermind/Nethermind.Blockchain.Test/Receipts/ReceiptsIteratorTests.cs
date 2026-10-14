// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using Autofac;
using Nethermind.Blockchain.Blocks;
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
        [Values] bool fromEncoded, [Values(RecoveryMode.Full, RecoveryMode.Log, RecoveryMode.DeferredLog)] RecoveryMode recoveryMode,
        [Values(0, 1)] int firstMatch, [Values(1, 2)] int matchInterval)
    {
        bool useLogContext = recoveryMode != RecoveryMode.Full;
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
        ReceiptsIterator iterator = recoveryMode == RecoveryMode.DeferredLog
            ? new(span, receiptDb, _decoder.GetRefDecoder(span),
                ((ReceiptsRecovery)recovery).CreateDeferredLogRecoveryContext(new TestBlockSource(GetRecoveryBlock)))
            : new(span, receiptDb, static () => null, _decoder.GetRefDecoder(span), CreateLogRecoveryContext);

        ReceiptRecoveryBlock GetRecoveryBlock() => fromEncoded
            ? blockDecoder.DecodeToReceiptRecoveryBlock(blockMemory, blockMemory!.Memory, RlpBehaviors.None)!.Value
            : new ReceiptRecoveryBlock(block);

        IReceiptsRecovery.IRecoveryContext CreateLogRecoveryContext()
        {
            ReceiptRecoveryBlock recoveryBlock = GetRecoveryBlock();
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

    [Test]
    public void DeferredRecovery_LoadsOnlyRequiredContextsAndDisposesTheirOwners(
        [Values] bool fromEncoded, [Values] RecoverySequence sequence)
    {
        Transaction[] transactions =
        [
            Build.A.Transaction.WithNonce(1).Signed().TestObject,
            Build.A.Transaction.WithNonce(2).WithType(TxType.EIP1559).Signed().TestObject,
            Build.A.Transaction.WithNonce(3).Signed().TestObject
        ];
        Block block = Build.A.Block.WithNumber(7).WithBaseFeePerGas(1).WithTransactions(transactions).TestObject;
        TxReceipt[] receipts =
        [
            Build.A.Receipt.WithGasUsedTotal(10).TestObject,
            Build.A.Receipt.WithGasUsedTotal(20).TestObject,
            Build.A.Receipt.WithGasUsedTotal(30).TestObject
        ];
        using ArrayPoolSpan<byte> stream = _decoder.EncodeToArrayPoolSpan(receipts, RlpBehaviors.Storage);
        Span<byte> span = stream;
        CountingBlockSource source = new(block, fromEncoded);
        using IContainer container = CreateRecoveryContainer();
        ReceiptsRecovery recovery = (ReceiptsRecovery)container.Resolve<IReceiptsRecovery>();
        using TestMemDb receiptDb = new();
        ReceiptsIterator iterator = new(span, receiptDb, _decoder.GetRefDecoder(span),
            recovery.CreateDeferredLogRecoveryContext(new TestBlockSource(source.GetBlock)));

        try
        {
            Assert.That(iterator.TryGetNext(out _), Is.True);
            Assert.That(iterator.TryGetNext(out TxReceiptStructRef receipt), Is.True);
            Assert.That(source.Reads, Is.Zero, "reading compact receipts must not read the block");
            if (sequence is RecoverySequence.FullOnly or RecoverySequence.FullThenLog)
            {
                iterator.RecoverIfNeeded(ref receipt);
            }
            if (sequence is RecoverySequence.LogOnly or RecoverySequence.LogThenFull or RecoverySequence.FullThenLog)
            {
                iterator.RecoverLogFieldsIfNeeded(ref receipt, out Hash256? hash);
                iterator.RecoverLogFieldsIfNeeded(ref receipt, out Hash256? repeatedHash);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(hash, Is.EqualTo(sequence == RecoverySequence.FullThenLog ? null : transactions[1].Hash));
                    Assert.That(repeatedHash, Is.SameAs(hash));
                }
            }
            if (sequence == RecoverySequence.LogThenFull)
            {
                iterator.RecoverIfNeeded(ref receipt);
            }
            if (sequence is RecoverySequence.FullOnly or RecoverySequence.FullThenLog or RecoverySequence.LogThenFull)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(receipt.TxHash.ToCommitment(), Is.EqualTo(transactions[1].Hash));
                    Assert.That(receipt.Index, Is.EqualTo(1));
                    Assert.That(receipt.GasUsed, Is.EqualTo(10));
                }
            }
            Assert.That(iterator.TryGetNext(out receipt), Is.True);
            if (sequence is RecoverySequence.FullOnly or RecoverySequence.FullThenLog or RecoverySequence.LogThenFull)
            {
                Assert.That(receipt.TxHash.ToCommitment(), Is.EqualTo(transactions[2].Hash));
            }
            Assert.That(iterator.TryGetNext(out _), Is.False);
        }
        finally
        {
            iterator.Dispose();
        }

        int expectedReads = sequence == RecoverySequence.NoMatches ? 0 : sequence == RecoverySequence.LogThenFull ? 2 : 1;
        Assert.That(source.Reads, Is.EqualTo(expectedReads), "full replay must obtain its own block owner");
        foreach (CountingMemoryManager owner in source.Owners)
        {
            Assert.That(owner.DisposeCount, Is.EqualTo(1), "each block owner must be released once");
        }
    }

    [Test]
    public void StorageIterator_PreservesRegisteredRecoveryOverrides([Values] RecoveryOverride recoveryOverride)
    {
        ContainerBuilder builder = CreateRecoveryContainerBuilder();
        if (recoveryOverride == RecoveryOverride.Decorator)
        {
            builder.AddDecorator<IReceiptsRecovery>((_, recovery) => new CustomRecoveryDecorator(recovery));
        }
        else if (recoveryOverride == RecoveryOverride.Derived)
        {
            builder.AddSingleton<IReceiptsRecovery, CustomDerivedRecovery>();
        }
        using IContainer container = builder.Build();
        IReceiptsRecovery recovery = container.Resolve<IReceiptsRecovery>();
        PersistentReceiptStorage storage = (PersistentReceiptStorage)container.Resolve<IReceiptStorage>();
        Block block = Build.A.Block.WithNumber(7).WithBaseFeePerGas(1).WithTransactions(Build.A.Transaction.Signed().TestObject).TestObject;
        block.Header.Hash = block.Header.CalculateHash();
        container.Resolve<IBlockStore>().Insert(block);
        storage.Insert(block, [Build.A.Receipt.WithGasUsedTotal(10).TestObject]);
        storage.ClearCache();

        Assert.That(storage.TryGetReceiptsIterator(block.Number, block.Hash!, out ReceiptsIterator iterator), Is.True);
        try
        {
            Assert.That(iterator.TryGetNext(out TxReceiptStructRef receipt), Is.True);
            iterator.RecoverLogFieldsIfNeeded(ref receipt, out Hash256? hash);
            Assert.That(hash ?? receipt.TxHash.ToCommitment(), Is.EqualTo(block.Transactions[0].Hash));
            if (recoveryOverride != RecoveryOverride.Default)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(hash, Is.Null, "the custom full context must retain its fallback behavior");
                    Assert.That(((ICustomRecovery)recovery).LogContextCalls, Is.EqualTo(1), "custom log factories must not be bypassed");
                }
            }
            else
            {
                Assert.That(hash, Is.Not.Null, "the default deferred context must return its hash directly");
            }
        }
        finally
        {
            iterator.Dispose();
        }
    }

    private static IContainer CreateRecoveryContainer() => CreateRecoveryContainerBuilder().Build();

    private static ContainerBuilder CreateRecoveryContainerBuilder() => new ContainerBuilder()
        .AddModule(new TestNethermindModule(new ReceiptConfig { CompactReceiptStore = true }))
        .AddSingleton<ISpecProvider>(MainnetSpecProvider.Instance);

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

    public enum RecoveryMode { Full, Log, DeferredLog }
    public enum RecoverySequence { NoMatches, LogOnly, FullOnly, LogThenFull, FullThenLog }
    public enum RecoveryOverride { Default, Decorator, Derived }

    private readonly struct TestBlockSource(Func<ReceiptRecoveryBlock> getBlock) : IReceiptsRecovery.IRecoveryBlockSource
    {
        public ReceiptRecoveryBlock GetBlock() => getBlock();
    }

    private sealed class CountingBlockSource(Block block, bool fromEncoded)
    {
        public int Reads { get; private set; }
        public List<CountingMemoryManager> Owners { get; } = [];

        public ReceiptRecoveryBlock GetBlock()
        {
            Reads++;
            if (!fromEncoded) return new ReceiptRecoveryBlock(block);
            BlockDecoder decoder = new();
            CountingMemoryManager owner = new(decoder.Encode(block).Bytes);
            Owners.Add(owner);
            return decoder.DecodeToReceiptRecoveryBlock(owner, owner.Memory, RlpBehaviors.None)!.Value;
        }
    }

    private interface ICustomRecovery
    {
        int LogContextCalls { get; }
    }

    private sealed class CustomRecoveryDecorator(IReceiptsRecovery recovery) : IReceiptsRecovery, ICustomRecovery
    {
        public int LogContextCalls { get; private set; }
        public ReceiptsRecoveryResult TryRecover(ReceiptRecoveryBlock block, TxReceipt[] receipts, bool forceRecoverSender = true) =>
            recovery.TryRecover(block, receipts, forceRecoverSender);
        public bool NeedRecover(TxReceipt[] receipts, bool forceRecoverSender = true, bool recoverSenderOnly = false) =>
            recovery.NeedRecover(receipts, forceRecoverSender, recoverSenderOnly);
        public IReceiptsRecovery.IRecoveryContext CreateRecoveryContext(ReceiptRecoveryBlock block, bool forceRecoverSender = false) =>
            recovery.CreateRecoveryContext(block, forceRecoverSender);
        public IReceiptsRecovery.IRecoveryContext CreateLogRecoveryContext(ReceiptRecoveryBlock block)
        {
            LogContextCalls++;
            return recovery.CreateRecoveryContext(block);
        }
    }

    private sealed class CustomDerivedRecovery(IEthereumEcdsa ecdsa, ISpecProvider specProvider)
        : ReceiptsRecovery(ecdsa, specProvider, false), IReceiptsRecovery, ICustomRecovery
    {
        public int LogContextCalls { get; private set; }
        IReceiptsRecovery.IRecoveryContext IReceiptsRecovery.CreateLogRecoveryContext(ReceiptRecoveryBlock block)
        {
            LogContextCalls++;
            return CreateRecoveryContext(block);
        }
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

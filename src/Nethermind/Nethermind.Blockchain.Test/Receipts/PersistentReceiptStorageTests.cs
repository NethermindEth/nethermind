// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Encoding;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Receipts;


[TestFixture(true)]
[TestFixture(false)]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class PersistentReceiptStorageTests(bool useCompactReceipts)
{
    private readonly TestSpecProvider _specProvider = new(Byzantium.Instance);
    private TestMemColumnsDb<ReceiptsColumns> _receiptsDb = null!;
    private ReceiptsRecovery _receiptsRecovery = null!;
    private IBlockTree _blockTree = null!;
    private IBlockStore _blockStore = null!;
    private ReceiptConfig _receiptConfig = null!;
    private PersistentReceiptStorage _storage = null!;
    private ReceiptArrayStorageDecoder _decoder = null!;

    [SetUp]
    public void SetUp()
    {
        EthereumEcdsa ethereumEcdsa = new(_specProvider.ChainId);
        _receiptConfig = new ReceiptConfig();
        _receiptsRecovery = new(ethereumEcdsa, _specProvider);
        _receiptsDb = new TestMemColumnsDb<ReceiptsColumns>();
        _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks).Set(Keccak.Zero, Array.Empty<byte>());
        _blockTree = Substitute.For<IBlockTree>();
        _blockStore = Substitute.For<IBlockStore>();
        CreateStorage();
    }

    [TearDown]
    public void TearDown() => _receiptsDb.Dispose();

    private void CreateStorage()
    {
        _decoder = new ReceiptArrayStorageDecoder(useCompactReceipts);
        _storage = new PersistentReceiptStorage(
            _receiptsDb,
            _specProvider,
            _receiptsRecovery,
            _blockTree,
            _blockStore,
            _receiptConfig,
            _decoder
        )
        { MigratedBlockNumber = 0 };
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Returns_null_for_missing_tx()
    {
        Hash256 blockHash = _storage.FindBlockHash(Keccak.Zero);
        Assert.That(blockHash, Is.Null);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void ReceiptsIterator_does_not_throw_on_empty_span()
    {
        _storage.TryGetReceiptsIterator(1, Keccak.Zero, out ReceiptsIterator iterator);
        Assert.That(iterator.TryGetNext(out _), Is.False);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void ReceiptsIterator_does_not_throw_on_null()
    {
        _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks).Set(Keccak.Zero, null!);
        _storage.TryGetReceiptsIterator(1, Keccak.Zero, out ReceiptsIterator iterator);
        Assert.That(iterator.TryGetNext(out _), Is.False);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Get_returns_empty_on_empty_span() =>
        Assert.That(_storage.Get(Keccak.Zero), Is.EqualTo(Array.Empty<TxReceipt>()));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Adds_and_retrieves_receipts_for_block()
    {
        (Block? block, TxReceipt[]? receipts) = InsertBlock();

        _storage.ClearCache();
        _storage.Get(block).AssertEquivalentTo(receipts, nameof(TxReceipt.Error));
        // second should be from cache
        _storage.Get(block).AssertEquivalentTo(receipts, nameof(TxReceipt.Error));
    }

    [Test]
    public void Adds_should_prefix_key_with_blockNumber()
    {
        (Block block, _) = InsertBlock();

        Span<byte> blockNumPrefixed = stackalloc byte[40];
        block.Number.ToBigEndianByteArray().CopyTo(blockNumPrefixed); // TODO: We don't need to create an array here...
        block.Hash!.Bytes.CopyTo(blockNumPrefixed[8..]);

        Assert.That(_receiptsDb.GetColumnDb(ReceiptsColumns.Blocks)[blockNumPrefixed], Is.Not.Null);
    }

    [Test]
    public void Adds_should_forward_write_flags()
    {
        (Block block, _) = InsertBlock(writeFlags: WriteFlags.DisableWAL);

        Span<byte> blockNumPrefixed = stackalloc byte[40];
        block.Number.ToBigEndianByteArray().CopyTo(blockNumPrefixed); // TODO: We don't need to create an array here...
        block.Hash!.Bytes.CopyTo(blockNumPrefixed[8..]);

        TestMemDb blockDb = (TestMemDb)_receiptsDb.GetColumnDb(ReceiptsColumns.Blocks);

        blockDb.KeyWasWrittenWithFlags(blockNumPrefixed.ToArray(), WriteFlags.DisableWAL);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Get_receipts_for_block_without_recovering_sender()
    {
        (Block? block, TxReceipt[]? receipts) = InsertBlock();
        foreach (Transaction tx in block.Transactions)
        {
            tx.SenderAddress = null;
        }

        _storage.ClearCache();
        _storage.Get(block, recoverSender: false).AssertEquivalentTo(receipts, nameof(TxReceipt.Error));

        foreach (Transaction tx in block.Transactions)
        {
            Assert.That(tx.SenderAddress, Is.Null);
        }
    }

    [Test]
    public void Adds_should_attempt_hash_key_first_if_inserted_with_hashkey()
    {
        (Block block, TxReceipt[] receipts) = PrepareBlock();

        using NettyRlpStream rlpStream = _decoder.EncodeToNewNettyStream(receipts, RlpBehaviors.Storage);
        _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks)[block.Hash!.Bytes] = rlpStream.AsSpan().ToArray();

        CreateStorage();
        _storage.Get(block);

        Span<byte> blockNumPrefixed = stackalloc byte[40];
        block.Number.ToBigEndianByteArray().CopyTo(blockNumPrefixed); // TODO: We don't need to create an array here...
        block.Hash!.Bytes.CopyTo(blockNumPrefixed[8..]);

        TestMemDb blocksDb = (TestMemDb)_receiptsDb.GetColumnDb(ReceiptsColumns.Blocks);
        blocksDb.KeyWasRead(blockNumPrefixed.ToArray(), times: 0);
        blocksDb.KeyWasRead(block.Hash.BytesToArray(), times: 1);
    }

    [Test]
    public void Should_be_able_to_get_block_with_hash_address()
    {
        (Block block, TxReceipt[] receipts) = PrepareBlock();

        Span<byte> blockNumPrefixed = stackalloc byte[40];
        block.Number.ToBigEndianByteArray().CopyTo(blockNumPrefixed); // TODO: We don't need to create an array here...
        block.Hash!.Bytes.CopyTo(blockNumPrefixed[8..]);

        using NettyRlpStream rlpStream = _decoder.EncodeToNewNettyStream(receipts, RlpBehaviors.Storage);
        _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks)[block.Hash.Bytes] = rlpStream.AsSpan().ToArray();

        Assert.That(_storage.Get(block).Length, Is.EqualTo(receipts.Length));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Should_not_cache_empty_non_processed_blocks()
    {
        Block block = Build.A.Block
            .WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject)
            .WithReceiptsRoot(TestItem.KeccakA)
            .TestObject;

        TxReceipt[] emptyReceipts = [];
        Assert.That(_storage.Get(block), Is.EqualTo(emptyReceipts));
        // can be from cache:
        Assert.That(_storage.Get(block), Is.EqualTo(emptyReceipts));
        (_, TxReceipt[] receipts) = InsertBlock(block);
        // before should not be cached
        Assert.That(_storage.Get(block), Is.EqualTo(receipts));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Adds_and_retrieves_receipts_for_block_with_iterator_from_cache_after_insert()
    {
        (Block? block, TxReceipt[]? receipts) = InsertBlock();

        Assert.That(_storage.TryGetReceiptsIterator(0, block.Hash!, out ReceiptsIterator iterator), Is.True);
        Assert.That(iterator.TryGetNext(out TxReceiptStructRef receiptStructRef), Is.True);
        Assert.That(receiptStructRef.LogsRlp.ToArray(), Is.Empty);
        receiptStructRef.Logs.AssertEquivalentTo(receipts.First().Logs);
        Assert.That(iterator.TryGetNext(out _), Is.False);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Adds_and_retrieves_receipts_for_block_with_iterator()
    {
        (Block? block, TxReceipt[] _) = InsertBlock();

        _storage.ClearCache();
        Assert.That(_storage.TryGetReceiptsIterator(block.Number, block.Hash!, out ReceiptsIterator iterator), Is.True);
        Assert.That(iterator.TryGetNext(out TxReceiptStructRef receiptStructRef), Is.True);
        Assert.That(receiptStructRef.LogsRlp.ToArray(), Is.Not.Empty);
        Assert.That(receiptStructRef.Logs, Is.Null.Or.Empty);

        Assert.That(iterator.TryGetNext(out _), Is.False);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Adds_and_retrieves_receipts_for_block_with_iterator_from_cache_after_get()
    {
        (Block? block, TxReceipt[]? receipts) = InsertBlock();

        _storage.ClearCache();
        _storage.Get(block);
        Assert.That(_storage.TryGetReceiptsIterator(0, block.Hash!, out ReceiptsIterator iterator), Is.True);
        Assert.That(iterator.TryGetNext(out TxReceiptStructRef receiptStructRef), Is.True);
        Assert.That(receiptStructRef.LogsRlp.ToArray(), Is.Empty);
        receiptStructRef.Logs.AssertEquivalentTo(receipts.First().Logs);
        Assert.That(iterator.TryGetNext(out _), Is.False);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Should_handle_inserting_null_receipts()
    {
        Block block = Build.A.Block.WithReceiptsRoot(TestItem.KeccakA).TestObject;
        _storage.Insert(block, null);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void HasBlock_should_returnFalseForMissingHash() =>
        Assert.That(_storage.HasBlock(0, Keccak.Compute("missing-value")), Is.False);

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void HasBlock_should_returnTrueForKnownHash()
    {
        (Block? block, TxReceipt[] _) = InsertBlock();
        Assert.That(_storage.HasBlock(block.Number, block.Hash!), Is.True);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void EnsureCanonical_should_change_tx_blockhash(
        [Values(false, true)] bool ensureCanonical,
        [Values(false, true)] bool isFinalized)
    {
        (Block block, TxReceipt[] receipts) = InsertBlock(isFinalized: isFinalized);
        Assert.That(_storage.FindBlockHash(receipts[0].TxHash!), Is.EqualTo(block.Hash!));

        Block anotherBlock = Build.A.Block
            .WithTransactions(block.Transactions)
            .WithReceiptsRoot(TestItem.KeccakA)
            .WithExtraData(new byte[] { 1 })
            .TestObject;

        Assert.That(anotherBlock.Hash, Is.Not.EqualTo(block.Hash!));
        _storage.Insert(anotherBlock, new[] { Build.A.Receipt.TestObject }, ensureCanonical);
        _blockTree.FindBlockHash(anotherBlock.Number).Returns(anotherBlock.Hash);

        Hash256 findBlockHash = _storage.FindBlockHash(receipts[0].TxHash!);
        if (ensureCanonical)
        {
            Assert.That(findBlockHash, Is.EqualTo(anotherBlock.Hash!));
        }
        else
        {
            Assert.That(findBlockHash, Is.Not.EqualTo(anotherBlock.Hash!));
        }
    }

    [Test]
    public void EnsureCanonical_should_use_blockNumber_if_finalized()
    {
        (Block block, TxReceipt[] receipts) = InsertBlock(isFinalized: true);
        Span<byte> txHashBytes = receipts[0].TxHash!.Bytes;
        if (_receiptConfig.CompactTxIndex)
        {
            Assert.That(_receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[txHashBytes], Is.EqualTo(Rlp.Encode(block.Number).Bytes));
        }
        else
        {
            Assert.That(_receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[txHashBytes], Is.Not.Null);
        }
    }

    [Test]
    public void When_TxLookupLimitIs_MaxValue_DoNotIndexTxHash()
    {
        _receiptConfig.TxLookupLimit = ulong.MaxValue;
        CreateStorage();
        (Block block, TxReceipt[] receipts) = InsertBlock(isFinalized: true);
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block));
        Assert.That(() => _receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[receipts[0].TxHash!.Bytes], Is.Null.After(100, 10));
    }

    [TestCase(1ul, false)]
    [TestCase(10ul, false)]
    [TestCase(11ul, true)]
    public void Should_only_prune_index_tx_hashes_if_blockNumber_is_bigger_than_lookupLimit(ulong blockNumber, bool willPruneOldIndices)
    {
        _receiptConfig.TxLookupLimit = 10ul;
        CreateStorage();
        _blockTree.BlockAddedToMain +=
            Raise.EventWith(new BlockReplacementEventArgs(Build.A.Block.WithNumber(blockNumber).TestObject));
        Assert.That(() => _blockTree.ReceivedCalls()
            .Where(static call => call.GetMethodInfo().Name.EndsWith(nameof(_blockTree.FindBlock))),
            willPruneOldIndices ? Is.Not.Empty.After(100, 10) : Is.Empty.After(100, 10));
    }

    [Test]
    public void When_HeadBlockIsFarAhead_DoNotIndexTxHash()
    {
        _receiptConfig.TxLookupLimit = 1000ul;
        CreateStorage();
        (Block block, TxReceipt[] receipts) = InsertBlock(isFinalized: true, headNumber: 1001ul);
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block));
        Assert.That(() => _receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[receipts[0].TxHash!.Bytes], Is.Null.After(100, 10));
    }

    [Test]
    public void When_NewHeadBlock_DoNotRemove_TxIndex_WhenTxIsInOtherBlockNumber()
    {
        CreateStorage();

        Transaction tx = Build.A.Transaction.SignedAndResolved().TestObject;

        Block b1a = Build.A.Block.WithNumber(1).TestObject;
        Block b1b = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;
        Block b2a = Build.A.Block.WithNumber(2).WithParent(b1a).WithTransactions(tx).TestObject;
        Block b2b = Build.A.Block.WithNumber(2).WithParent(b1b).TestObject;

        InsertBlock(b1a);
        InsertBlock(b1b);
        InsertBlock(b2a);
        InsertBlock(b2b);

        // b1a
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(b1a, null));

        // b1b
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(b1b, b1a));

        // b2a
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(b1a, b1b));
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(b2a, null));

        // b2b
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(b1b, b1a));
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(b2b, b2a));

        Assert.That(_storage.FindBlockHash(tx.Hash!), Is.EqualTo(b1b.Hash!));
    }

    [Test]
    public async Task When_NewHeadBlock_Remove_TxIndex_OfRemovedBlock_Unless_ItsAlsoInNewBlock()
    {
        _receiptConfig.CompactTxIndex = useCompactReceipts;
        CreateStorage();
        (Block block, _) = InsertBlock();
        Block block2 = Build.A.Block
            .WithParent(block)
            .WithNumber(2)
            .WithTransactions(Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyC).TestObject)
            .TestObject;
        _blockTree.FindBestSuggestedHeader().Returns(block2.Header);
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block2));

        if (_receiptConfig.CompactTxIndex)
        {
            Assert.That(_receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[block.Transactions[0].Hash!.Bytes], Is.EqualTo(Rlp.Encode(block.Number).Bytes));
        }
        else
        {
            Assert.That(_receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[block.Transactions[0].Hash!.Bytes], Is.EqualTo(block.Hash!.Bytes.ToArray()));
        }

        Block block3 = Build.A.Block
            .WithNumber(1)
            .WithTransactions(block2.Transactions)
            .WithExtraData(new byte[1])
            .TestObject;
        Block block4 = Build.A.Block
            .WithNumber(2)
            .WithTransactions(block.Transactions)
            .WithExtraData(new byte[1])
            .TestObject;
        _blockTree.FindBestSuggestedHeader().Returns(block4.Header);
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block3, block));
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block4, block2));

        await Task.Delay(100);
        if (_receiptConfig.CompactTxIndex)
        {
            Assert.That(_receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[block4.Transactions[0].Hash!.Bytes], Is.EqualTo(Rlp.Encode(block4.Number).Bytes));
        }
        else
        {
            Assert.That(_receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[block4.Transactions[0].Hash!.Bytes], Is.EqualTo(block4.Hash!.Bytes.ToArray()));
        }
    }

    [Test]
    public void When_NewHeadBlock_ClearOldTxIndex_And_KeepsReceipts()
    {
        _receiptConfig.TxLookupLimit = 1000ul;
        CreateStorage();
        (Block block, TxReceipt[] receipts) = InsertBlock();

        Span<byte> txHashBytes = receipts[0].TxHash!.Bytes;
        if (_receiptConfig.CompactTxIndex)
        {
            Assert.That(_receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[txHashBytes], Is.EqualTo(Rlp.Encode(block.Number).Bytes));
        }
        else
        {
            Assert.That(_receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[txHashBytes], Is.Not.Null);
        }

        Block newHead = Build.A.Block.WithNumber(_receiptConfig.TxLookupLimit.Value + 1ul).TestObject;
        _blockTree.FindBestSuggestedHeader().Returns(newHead.Header);
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(newHead));

        Assert.That(
            () => _receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[receipts[0].TxHash!.Bytes],
            Is.Null.After(1000, 100)
            );
        Assert.That(_storage.HasBlock(receipts[0].BlockNumber, receipts[0].BlockHash!));
    }

    [TestCase(false, 5ul, TestName = "Insert tracks the lowest inserted block")]
    [TestCase(true, ulong.MaxValue, TestName = "InsertForMigration leaves the pointer to the migration")]
    public void Migration_pointer_is_advanced_only_by_the_normal_insert_path(bool viaMigration, ulong expectedMigratedBlockNumber)
    {
        const ulong blockNumber = 5;
        _storage.MigratedBlockNumber = ulong.MaxValue;

        (Block block, TxReceipt[] receipts) = PrepareBlock(Build.A.Block
            .WithNumber(blockNumber)
            .WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject)
            .WithReceiptsRoot(TestItem.KeccakA)
            .TestObject);

        if (viaMigration)
        {
            ((IReceiptMigrationStore)_storage).InsertForMigration(block, receipts);
        }
        else
        {
            _storage.Insert(block, receipts);
        }

        Assert.That(_storage.MigratedBlockNumber, Is.EqualTo(expectedMigratedBlockNumber),
            "the migration owns the pointer under parallel out-of-order inserts, so only the normal Insert path may advance it");
    }

    private (Block block, TxReceipt[] receipts) PrepareBlock(Block? block = null, bool isFinalized = false, ulong? headNumber = null)
    {
        block ??= Build.A.Block
            .WithNumber(1)
            .WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject)
            .WithReceiptsRoot(TestItem.KeccakA)
            .TestObject;

        _blockTree.FindBlock(block.Hash!).Returns(block);
        _blockTree.FindBlock(block.Number).Returns(block);
        _blockTree.FindHeader(block.Number).Returns(block.Header);
        _blockTree.FindBlockHash(block.Number).Returns(block.Hash);
        if (isFinalized)
        {
            BlockHeader farHead = Build.A.BlockHeader
                .WithNumber(Reorganization.MaxDepth + 5)
                .TestObject;
            _blockTree.FindBestSuggestedHeader().Returns(farHead);
        }

        if (headNumber is not null)
        {
            BlockHeader farHead = Build.A.BlockHeader
                .WithNumber(headNumber.Value)
                .TestObject;
            _blockTree.FindBestSuggestedHeader().Returns(farHead);
        }

        TxReceipt[] receipts = Array.Empty<TxReceipt>();
        if (block.Transactions.Length == 1)
        {
            receipts = [Build.A.Receipt.WithCalculatedBloom().TestObject];
        }
        return (block, receipts);
    }

    private (Block block, TxReceipt[] receipts) InsertBlock(Block? block = null, bool isFinalized = false, ulong? headNumber = null, WriteFlags writeFlags = WriteFlags.None)
    {
        (block, TxReceipt[] receipts) = PrepareBlock(block, isFinalized, headNumber);
        _storage.Insert(block, receipts, writeFlags: writeFlags);
        _receiptsRecovery.TryRecover(new ReceiptRecoveryBlock(block), receipts);

        return (block, receipts);
    }

}

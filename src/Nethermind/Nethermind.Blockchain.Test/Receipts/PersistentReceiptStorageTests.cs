// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Receipts
{

    [TestFixture(true)]
    [TestFixture(false)]
    public class PersistentReceiptStorageTests
    {
        private MemColumnsDb<ReceiptsColumns> _receiptsDb = null!;
        private PersistentReceiptStorage _storage = null!;
        private ReceiptsRecovery _receiptsRecovery;
        private IBlockFinder _blockTree;
        private readonly bool _useCompactReceipts;

        public PersistentReceiptStorageTests(bool useCompactReceipts)
        {
            _useCompactReceipts = useCompactReceipts;
        }

        [SetUp]
        public void SetUp()
        {
            RopstenSpecProvider specProvider = RopstenSpecProvider.Instance;
            EthereumEcdsa ethereumEcdsa = new(specProvider.ChainId, LimboLogs.Instance);
            _receiptsRecovery = new(ethereumEcdsa, specProvider);
            _receiptsDb = new MemColumnsDb<ReceiptsColumns>();
            _blockTree = Substitute.For<IBlockFinder>();
            _storage = new PersistentReceiptStorage(_receiptsDb, MainnetSpecProvider.Instance, _receiptsRecovery, _blockTree,
                new ReceiptArrayStorageDecoder(_useCompactReceipts)
                )
            { MigratedBlockNumber = 0 };
            _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks).Set(Keccak.Zero, Array.Empty<byte>());
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Returns_null_for_missing_tx()
        {
            Keccak blockHash = _storage.FindBlockHash(Keccak.Zero);
            blockHash.Should().BeNull();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void ReceiptsIterator_doesnt_throw_on_empty_span()
        {
            _storage.TryGetReceiptsIterator(1, Keccak.Zero, out var iterator);
            iterator.TryGetNext(out _).Should().BeFalse();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void ReceiptsIterator_doesnt_throw_on_null()
        {
            _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks).Set(Keccak.Zero, null!);
            _storage.TryGetReceiptsIterator(1, Keccak.Zero, out var iterator);
            iterator.TryGetNext(out _).Should().BeFalse();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Get_returns_empty_on_empty_span()
        {
            _storage.Get(Keccak.Zero).Should().BeEquivalentTo(Array.Empty<TxReceipt>());
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Adds_and_retrieves_receipts_for_block()
        {
            var (block, receipts) = InsertBlock();

            _storage.Get(block).Should().BeEquivalentTo(receipts);
            // second should be from cache
            _storage.Get(block).Should().BeEquivalentTo(receipts);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Should_not_cache_empty_non_processed_blocks()
        {
            var block = Build.A.Block
                .WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject)
                .WithReceiptsRoot(TestItem.KeccakA)
                .TestObject;

            var emptyReceipts = Array.Empty<TxReceipt>();
            _storage.Get(block).Should().BeEquivalentTo(emptyReceipts);
            // can be from cache:
            _storage.Get(block).Should().BeEquivalentTo(emptyReceipts);
            var (_, receipts) = InsertBlock(block);
            // before should not be cached
            _storage.Get(block).Should().BeEquivalentTo(receipts);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Adds_and_retrieves_receipts_for_block_with_iterator_from_cache_after_insert()
        {
            var (block, receipts) = InsertBlock();

            _storage.TryGetReceiptsIterator(0, block.Hash!, out ReceiptsIterator iterator).Should().BeTrue();
            iterator.TryGetNext(out var receiptStructRef).Should().BeTrue();
            receiptStructRef.LogsRlp.ToArray().Should().BeEmpty();
            receiptStructRef.Logs.Should().BeEquivalentTo(receipts.First().Logs);
            iterator.TryGetNext(out receiptStructRef).Should().BeFalse();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Adds_and_retrieves_receipts_for_block_with_iterator()
        {
            var (block, _) = InsertBlock();

            _storage.ClearCache();
            _storage.TryGetReceiptsIterator(block.Number, block.Hash!, out ReceiptsIterator iterator).Should().BeTrue();
            iterator.TryGetNext(out TxReceiptStructRef receiptStructRef).Should().BeTrue();
            if (_useCompactReceipts)
            {
                receiptStructRef.LogsRlp.IsNullOrEmpty().Should().BeTrue();
                receiptStructRef.Logs.Should().NotBeNullOrEmpty();
            }
            else
            {
                receiptStructRef.LogsRlp.ToArray().Should().NotBeEmpty();
                receiptStructRef.Logs.Should().BeNullOrEmpty();
            }

            iterator.TryGetNext(out receiptStructRef).Should().BeFalse();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Adds_and_retrieves_receipts_for_block_with_iterator_from_cache_after_get()
        {
            var (block, receipts) = InsertBlock();

            _storage.ClearCache();
            _storage.Get(block);
            _storage.TryGetReceiptsIterator(0, block.Hash!, out ReceiptsIterator iterator).Should().BeTrue();
            iterator.TryGetNext(out TxReceiptStructRef receiptStructRef).Should().BeTrue();
            receiptStructRef.LogsRlp.ToArray().Should().BeEmpty();
            receiptStructRef.Logs.Should().BeEquivalentTo(receipts.First().Logs);
            iterator.TryGetNext(out receiptStructRef).Should().BeFalse();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Should_handle_inserting_null_receipts()
        {
            Block block = Build.A.Block.WithReceiptsRoot(TestItem.KeccakA).TestObject;
            _storage.Insert(block, null);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void HasBlock_should_returnFalseForMissingHash()
        {
            _storage.HasBlock(Keccak.Compute("missing-value")).Should().BeFalse();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void HasBlock_should_returnTrueForKnownHash()
        {
            var (block, _) = InsertBlock();
            _storage.HasBlock(block.Hash!).Should().BeTrue();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void EnsureCanonical_should_change_tx_blockhash(
            [Values(false, true)] bool ensureCanonical,
            [Values(false, true)] bool isFinalized)
        {
            (Block block, TxReceipt[] receipts) = InsertBlock(isFinalized: isFinalized);
            _storage.FindBlockHash(receipts[0].TxHash!).Should().Be(block.Hash!);

            Block anotherBlock = Build.A.Block
                .WithTransactions(block.Transactions)
                .WithReceiptsRoot(TestItem.KeccakA)
                .WithExtraData(new byte[] { 1 })
                .TestObject;

            anotherBlock.Hash.Should().NotBe(block.Hash!);
            _storage.Insert(anotherBlock, new[] { Build.A.Receipt.TestObject }, ensureCanonical);

            Keccak findBlockHash = _storage.FindBlockHash(receipts[0].TxHash!);
            if (ensureCanonical)
            {
                findBlockHash.Should().Be(anotherBlock.Hash!);
            }
            else
            {
                findBlockHash.Should().NotBe(anotherBlock.Hash!);
            }
        }

        private (Block block, TxReceipt[] receipts) InsertBlock(Block? block = null, bool isFinalized = false)
        {
            block ??= Build.A.Block
                .WithNumber(1)
                .WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject)
                .WithReceiptsRoot(TestItem.KeccakA)
                .TestObject;

            _blockTree.FindBlock(block.Hash).Returns(block);
            _blockTree.IsFinalized(block.Header).Returns(isFinalized);
            var receipts = new[] { Build.A.Receipt.WithCalculatedBloom().TestObject };
            _storage.Insert(block, receipts);
            _receiptsRecovery.TryRecover(block, receipts);

            return (block, receipts);
        }
    }
}

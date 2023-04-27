// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Configuration;
using System.Linq;
using System.Threading;
using DotNetty.Transport.Channels;
using FluentAssertions;
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
        private ReceiptsRecovery _receiptsRecovery;
        private IBlockTree _blockTree;
        private readonly bool _useCompactReceipts;
        private ReceiptConfig _receiptConfig;
        private PersistentReceiptStorage _storage;

        public PersistentReceiptStorageTests(bool useCompactReceipts)
        {
            _useCompactReceipts = useCompactReceipts;
        }

        [SetUp]
        public void SetUp()
        {
            RopstenSpecProvider specProvider = RopstenSpecProvider.Instance;
            EthereumEcdsa ethereumEcdsa = new(specProvider.ChainId, LimboLogs.Instance);
            _receiptConfig = new ReceiptConfig();
            _receiptsRecovery = new(ethereumEcdsa, specProvider);
            _receiptsDb = new MemColumnsDb<ReceiptsColumns>();
            _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks).Set(Keccak.Zero, Array.Empty<byte>());
            _blockTree = Substitute.For<IBlockTree>();
            CreateStorage();
        }

        private void CreateStorage()
        {
            _storage = new PersistentReceiptStorage(
                _receiptsDb,
                MainnetSpecProvider.Instance,
                _receiptsRecovery,
                _blockTree,
                _receiptConfig,
                new ReceiptArrayStorageDecoder(_useCompactReceipts)
            )
            { MigratedBlockNumber = 0 };
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
            _blockTree.FindBlockHash(anotherBlock.Number).Returns(anotherBlock.Hash);

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

        [Test]
        public void EnsureCanonical_should_use_blocknumber_if_finalized()
        {
            (Block block, TxReceipt[] receipts) = InsertBlock(isFinalized: true);
            _receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[receipts[0].TxHash.Bytes].Should()
                .BeEquivalentTo(Rlp.Encode(block.Number).Bytes);
        }

        [Test]
        [Ignore("Needs to be fixed, details https://github.com/NethermindEth/nethermind/pull/5621")]
        public void When_TxLookupLimitIs_NegativeOne_DoNotIndexTxHash()
        {
            _receiptConfig.TxLookupLimit = -1;
            CreateStorage();
            (Block block, TxReceipt[] receipts) = InsertBlock(isFinalized: true);
            _receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[receipts[0].TxHash.Bytes].Should().BeNull();
        }

        [Test]
        public void Should_not_index_tx_hash_if_blockNumber_is_negative()
        {
            _receiptConfig.TxLookupLimit = 10;
            CreateStorage();
            _blockTree.BlockAddedToMain +=
                Raise.EventWith(new BlockReplacementEventArgs(Build.A.Block.WithNumber(1).TestObject));
            Thread.Sleep(100);
            var calls = _blockTree.ReceivedCalls()
                .Where(call => !call.GetMethodInfo().Name.EndsWith(nameof(_blockTree.BlockAddedToMain)));
            calls.Should().BeEmpty();
        }

        [Test]
        [Ignore("Needs to be fixed, details https://github.com/NethermindEth/nethermind/pull/5621")]
        public void When_HeadBlockIsFarAhead_DoNotIndexTxHash()
        {
            _receiptConfig.TxLookupLimit = 1000;
            CreateStorage();
            (Block block, TxReceipt[] receipts) = InsertBlock(isFinalized: true, headNumber: 1001);
            _receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[receipts[0].TxHash.Bytes].Should().BeNull();
        }

        [Test]
        [Ignore("Needs to be fixed, details https://github.com/NethermindEth/nethermind/pull/5621")]
        public void When_NewHeadBlock_Remove_TxIndex_OfRemovedBlock()
        {
            CreateStorage();
            (Block block, TxReceipt[] receipts) = InsertBlock();
            _receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[receipts[0].TxHash.Bytes].Should().BeEquivalentTo(Rlp.Encode(block.Number).Bytes);

            Block newHead = Build.A.Block.WithNumber(1).TestObject;
            _blockTree.FindBestSuggestedHeader().Returns(newHead.Header);
            _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(newHead, block));

            Assert.That(
                () => _receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[receipts[0].TxHash.Bytes],
                Is.Null.After(1000, 100)
                );
        }

        [Test]
        public void When_NewHeadBlock_ClearOldTxIndex()
        {
            _receiptConfig.TxLookupLimit = 1000;
            CreateStorage();
            (Block block, TxReceipt[] receipts) = InsertBlock();
            _receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[receipts[0].TxHash.Bytes].Should().BeEquivalentTo(Rlp.Encode(block.Number).Bytes);

            Block newHead = Build.A.Block.WithNumber(_receiptConfig.TxLookupLimit.Value + 1).TestObject;
            _blockTree.FindBestSuggestedHeader().Returns(newHead.Header);
            _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(newHead));

            Assert.That(
                () => _receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[receipts[0].TxHash.Bytes],
                Is.Null.After(1000, 100)
                );
        }

        private (Block block, TxReceipt[] receipts) InsertBlock(Block? block = null, bool isFinalized = false, long? headNumber = null)
        {
            block ??= Build.A.Block
                .WithNumber(1)
                .WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject)
                .WithReceiptsRoot(TestItem.KeccakA)
                .TestObject;

            _blockTree.FindBlock(block.Hash).Returns(block);
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

            if (headNumber != null)
            {
                BlockHeader farHead = Build.A.BlockHeader
                    .WithNumber(headNumber.Value)
                    .TestObject;
                _blockTree.FindBestSuggestedHeader().Returns(farHead);
            }
            var receipts = new[] { Build.A.Receipt.WithCalculatedBloom().TestObject };
            _storage.Insert(block, receipts);
            _receiptsRecovery.TryRecover(block, receipts);

            return (block, receipts);
        }
    }
}

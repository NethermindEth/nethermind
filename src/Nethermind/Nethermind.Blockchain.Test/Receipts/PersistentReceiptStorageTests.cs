//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Receipts
{
    public class PersistentReceiptStorageTests
    {
        private MemColumnsDb<ReceiptsColumns> _receiptsDb = null!;
        private PersistentReceiptStorage _storage = null!;

        [SetUp]
        public void SetUp()
        {
            RopstenSpecProvider specProvider = RopstenSpecProvider.Instance;
            EthereumEcdsa ethereumEcdsa = new(specProvider.ChainId, LimboLogs.Instance);
            ReceiptsRecovery receiptsRecovery = new(ethereumEcdsa, specProvider);
            _receiptsDb = new MemColumnsDb<ReceiptsColumns>();
            _storage = new PersistentReceiptStorage(_receiptsDb, MainnetSpecProvider.Instance, receiptsRecovery) {MigratedBlockNumber = 0};
            _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks).Set(Keccak.Zero, Array.Empty<byte>());
        }

        [Test]
        public void Returns_null_for_missing_tx()
        {
            Keccak blockHash = _storage.FindBlockHash(Keccak.Zero);
            blockHash.Should().BeNull();
        }

        [Test]
        public void ReceiptsIterator_doesnt_throw_on_empty_span()
        {
            _storage.TryGetReceiptsIterator(1, Keccak.Zero, out var iterator);
            iterator.TryGetNext(out _).Should().BeFalse();
        }
        
        [Test]
        public void ReceiptsIterator_doesnt_throw_on_null()
        {
            _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks).Set(Keccak.Zero, null!);
            _storage.TryGetReceiptsIterator(1, Keccak.Zero, out var iterator);
            iterator.TryGetNext(out _).Should().BeFalse();
        }
        
        [Test]
        public void Get_returns_empty_on_empty_span()
        {
            _storage.Get(Keccak.Zero).Should().BeEquivalentTo(Array.Empty<TxReceipt>());
        }
        
        [Test]
        public void Adds_and_retrieves_receipts_for_block()
        {
            var (block, receipts) = InsertBlock();
            
            _storage.Get(block).Should().BeEquivalentTo(receipts);
            // second should be from cache
            _storage.Get(block).Should().BeEquivalentTo(receipts);
        }
        
        [Test]
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
        
        [Test]
        public void Adds_and_retrieves_receipts_for_block_with_iterator_from_cache_after_insert()
        {
            var (block, receipts) = InsertBlock();

            _storage.TryGetReceiptsIterator(0, block.Hash!, out ReceiptsIterator iterator).Should().BeTrue();
            iterator.TryGetNext(out var receiptStructRef).Should().BeTrue();
            receiptStructRef.LogsRlp.ToArray().Should().BeEmpty();
            receiptStructRef.Logs.Should().BeEquivalentTo(receipts.First().Logs);
            iterator.TryGetNext(out receiptStructRef).Should().BeFalse();
        }

        [Test]
        public void Adds_and_retrieves_receipts_for_block_with_iterator()
        {
            var (block, _) = InsertBlock();

            _storage.ClearCache();
            _storage.TryGetReceiptsIterator(0, block.Hash!, out ReceiptsIterator iterator).Should().BeTrue();
            iterator.TryGetNext(out TxReceiptStructRef receiptStructRef).Should().BeTrue();
            receiptStructRef.LogsRlp.ToArray().Should().NotBeEmpty();
            receiptStructRef.Logs.Should().BeNullOrEmpty();
            iterator.TryGetNext(out receiptStructRef).Should().BeFalse();
        }
        
        [Test]
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
        
        [Test]
        public void Should_handle_inserting_null_receipts()
        {
            Block block = Build.A.Block.WithReceiptsRoot(TestItem.KeccakA).TestObject;
            _storage.Insert(block, null);
        }
        
        [Test]
        public void HasBlock_should_returnFalseForMissingHash()
        {
            _storage.HasBlock(Keccak.Compute("missing-value")).Should().BeFalse();
        }
        
        [Test]
        public void HasBlock_should_returnTrueForKnownHash()
        {
            var (block, _) = InsertBlock();
            _storage.HasBlock(block.Hash!).Should().BeTrue();
        }
        
        [Test]
        public void EnsureCanonical_should_change_tx_blockhash([Values(false, true)] bool ensureCanonical)
        {
            (Block block, TxReceipt[] receipts) = InsertBlock();
            _storage.FindBlockHash(receipts[0].TxHash!).Should().Be(block.Hash!);
            
            Block anotherBlock = Build.A.Block
                .WithTransactions(block.Transactions)
                .WithReceiptsRoot(TestItem.KeccakA)
                .WithExtraData(new byte[]{1})
                .TestObject;

            anotherBlock.Hash.Should().NotBe(block.Hash!);
            _storage.Insert(anotherBlock, new []{ Build.A.Receipt.TestObject }, ensureCanonical);

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

        private (Block block, TxReceipt[] receipts) InsertBlock(Block? block = null)
        {
            block ??= Build.A.Block
                .WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject)
                .WithReceiptsRoot(TestItem.KeccakA)
                .TestObject;

            var receipts = new[] {Build.A.Receipt.TestObject};
            _storage.Insert(block, receipts);
            return (block, receipts);
        }
    }
}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Init.Steps.Migrations;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Ethereum.Steps.Migrations
{
    [TestFixture]
    public class ReceiptMigrationTests
    {
        [TestCase(null, 0, false, false, false, false)] // No change to migrate
        [TestCase(5, 5, false, false, false, true)] // Explicit command and partially migrated
        [TestCase(null, 5, true, false, false, true)] // Partially migrated
        [TestCase(5, 0, false, false, false, true)] // Explicit command
        [TestCase(null, 0, true, false, false, true)] // Force reset
        [TestCase(null, 0, false, false, true, true)] // Encoding mismatch
        [TestCase(null, 0, false, true, false, true)] // Encoding mismatch
        [TestCase(null, 0, false, true, true, false)] // Encoding match
        public void RunMigration(int? commandStartBlockNumber, long currentMigratedBlockNumber, bool forceReset, bool receiptIsCompact, bool useCompactEncoding, bool wasMigrated)
        {
            int chainLength = 10;
            IReceiptConfig receiptConfig = new ReceiptConfig()
            {
                ForceReceiptsMigration = forceReset,
                StoreReceipts = true,
                ReceiptsMigration = true,
                CompactReceiptStore = useCompactEncoding
            };

            BlockTreeBuilder blockTreeBuilder = Core.Test.Builders.Build.A.BlockTree().OfChainLength(chainLength);
            IBlockTree blockTree = blockTreeBuilder.TestObject;
            IChainLevelInfoRepository chainLevelInfoRepository = blockTreeBuilder.ChainLevelInfoRepository;

            InMemoryReceiptStorage inMemoryReceiptStorage = new(true) { MigratedBlockNumber = currentMigratedBlockNumber };
            InMemoryReceiptStorage outMemoryReceiptStorage = new(true) { MigratedBlockNumber = currentMigratedBlockNumber };
            TestReceiptStorage receiptStorage = new(inMemoryReceiptStorage, outMemoryReceiptStorage);
            ReceiptArrayStorageDecoder receiptArrayStorageDecoder = new(receiptIsCompact);

            ISyncModeSelector syncModeSelector = Substitute.For<ISyncModeSelector>();
            syncModeSelector.Current.Returns(SyncMode.WaitingForBlock);

            // Insert the blocks
            int txIndex = 0;
            for (int i = 1; i < chainLength; i++)
            {
                Block block = blockTree.FindBlock(i);
                inMemoryReceiptStorage.Insert(block, new[] {
                    Core.Test.Builders.Build.A.Receipt.WithTransactionHash(TestItem.Keccaks[txIndex++]).TestObject,
                    Core.Test.Builders.Build.A.Receipt.WithTransactionHash(TestItem.Keccaks[txIndex++]).TestObject
                });
            }

            TestMemColumnsDb<ReceiptsColumns> receiptColumnDb = new();

            // Put the last block receipt encoding
            Block lastBlock = blockTree.FindBlock(chainLength - 1);
            TxReceipt[] receipts = inMemoryReceiptStorage.Get(lastBlock);
            using (NettyRlpStream nettyStream = receiptArrayStorageDecoder.EncodeToNewNettyStream(receipts, RlpBehaviors.Storage))
            {
                receiptColumnDb.GetColumnDb(ReceiptsColumns.Blocks)
                    .Set(Bytes.Concat(lastBlock.Number.ToBigEndianByteArray(), lastBlock.Hash.BytesToArray()), nettyStream.AsSpan());
            }

            ManualResetEvent guard = new(false);
            ReceiptMigration migration = new(
                receiptStorage,
                new DisposableStack(),
                blockTree,
                syncModeSelector,
                chainLevelInfoRepository,
                receiptConfig,
                receiptColumnDb,
                Substitute.For<IReceiptsRecovery>(),
                LimboLogs.Instance
            );

            if (commandStartBlockNumber.HasValue)
            {
                _ = migration.Run(commandStartBlockNumber.Value);
            }
            else
            {
                migration.Run();
            }

            migration._migrationTask?.Wait();

            Assert.That(() => outMemoryReceiptStorage.MigratedBlockNumber, Is.InRange(0, 1).After(1000, 10));

            if (wasMigrated)
            {
                int blockNum = (commandStartBlockNumber ?? chainLength) - 1 - 1;
                int txCount = blockNum * 2;
                receiptColumnDb
                    .KeyWasWritten((item => item.Item2 == null), txCount);
                ((TestMemDb)receiptColumnDb.GetColumnDb(ReceiptsColumns.Blocks)).KeyWasRemoved((_ => true), blockNum);
                outMemoryReceiptStorage.Count.Should().Be(txCount);
            }
            else
            {
                receiptColumnDb.KeyWasWritten((item => item.Item2 == null), 0);
            }
        }

        private class TestReceiptStorage : IReceiptStorage
        {
            private readonly IReceiptStorage _inStorage;
            private readonly IReceiptStorage _outStorage;

            public TestReceiptStorage(IReceiptStorage inStorage, IReceiptStorage outStorage)
            {
                _inStorage = inStorage;
                _outStorage = outStorage;
            }

            public Keccak FindBlockHash(Keccak txHash) => _inStorage.FindBlockHash(txHash);

            public TxReceipt[] Get(Block block) => _inStorage.Get(block);

            public TxReceipt[] Get(Keccak blockHash) => _inStorage.Get(blockHash);

            public bool CanGetReceiptsByHash(long blockNumber) => _inStorage.CanGetReceiptsByHash(blockNumber);
            public bool TryGetReceiptsIterator(long blockNumber, Keccak blockHash, out ReceiptsIterator iterator) => _inStorage.TryGetReceiptsIterator(blockNumber, blockHash, out iterator);

            public void Insert(Block block, TxReceipt[] txReceipts, bool ensureCanonical) => _outStorage.Insert(block, txReceipts);

            public long? LowestInsertedReceiptBlockNumber
            {
                get => _outStorage.LowestInsertedReceiptBlockNumber;
                set => _outStorage.LowestInsertedReceiptBlockNumber = value;
            }
            public long MigratedBlockNumber
            {
                get => _outStorage.MigratedBlockNumber;
                set => _outStorage.MigratedBlockNumber = value;
            }

            public bool HasBlock(long blockNumber, Keccak hash)
            {
                return _outStorage.HasBlock(blockNumber, hash);
            }

            public void EnsureCanonical(Block block)
            {
            }

            public event EventHandler<ReceiptsEventArgs> ReceiptsInserted { add { } remove { } }
        }
    }
}

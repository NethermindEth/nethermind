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
using Nethermind.State.Repositories;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Ethereum.Steps.Migrations
{
    [TestFixture]
    public class ReceiptMigrationTests
    {
        [TestCase(null, false, false, false)]
        [TestCase(5, false, false, false)]
        [TestCase(5, true, false, true)]
        [TestCase(5, false, true, true)]
        public void RunMigration(int? migratedBlockNumber, bool forceReset, bool useCompactEncoding, bool didCompleteMigration)
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
            ChainLevelInfoRepository chainLevelInfoRepository = blockTreeBuilder.ChainLevelInfoRepository;

            InMemoryReceiptStorage inMemoryReceiptStorage = new(true) { MigratedBlockNumber = migratedBlockNumber is not null ? 0 : long.MaxValue };
            InMemoryReceiptStorage outMemoryReceiptStorage = new(true) { MigratedBlockNumber = migratedBlockNumber is not null ? 0 : long.MaxValue };
            TestReceiptStorage receiptStorage = new(inMemoryReceiptStorage, outMemoryReceiptStorage);

            ISyncModeSelector syncModeSelector = Substitute.For<ISyncModeSelector>();
            syncModeSelector.Current.Returns(SyncMode.WaitingForBlock);

            IReceiptsRecovery receiptsRecovery = Substitute.For<IReceiptsRecovery>();

            int txIndex = 0;
            for (int i = 1; i < chainLength; i++)
            {
                Block block = blockTree.FindBlock(i);
                inMemoryReceiptStorage.Insert(block, new[] {
                    Core.Test.Builders.Build.A.Receipt.WithTransactionHash(TestItem.Keccaks[txIndex++]).TestObject,
                    Core.Test.Builders.Build.A.Receipt.WithTransactionHash(TestItem.Keccaks[txIndex++]).TestObject
                });
            }

            ManualResetEvent guard = new(false);
            Keccak lastTransaction = TestItem.Keccaks[txIndex - 1];

            TestMemColumnsDb<ReceiptsColumns> receiptColumnDb = new();
            receiptColumnDb.RemoveFunc = (key) =>
            {
                if (Bytes.AreEqual(key, lastTransaction.Bytes)) guard.Set();
            };

            ReceiptMigration migration = new(
                receiptStorage,
                new DisposableStack(),
                blockTree,
                syncModeSelector,
                chainLevelInfoRepository,
                receiptConfig,
                receiptColumnDb,
                receiptsRecovery,
                LimboLogs.Instance
            );
            if (migratedBlockNumber.HasValue)
            {
                _ = migration.Run(migratedBlockNumber.Value);
            }
            else
            {
                migration.Run();
            }

            guard.WaitOne(TimeSpan.FromSeconds(1));

            int blockNum = (migratedBlockNumber ?? chainLength) - 1 - 1;
            if (didCompleteMigration)
            {
                blockNum = chainLength - 1 - 1;
            }

            int txCount = blockNum * 2;

            receiptColumnDb.KeyWasRemoved(_ => true, txCount);
            ((TestMemDb) receiptColumnDb.GetColumnDb(ReceiptsColumns.Blocks)).KeyWasRemoved(_ => true, blockNum);
            outMemoryReceiptStorage.Count.Should().Be(txCount);
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

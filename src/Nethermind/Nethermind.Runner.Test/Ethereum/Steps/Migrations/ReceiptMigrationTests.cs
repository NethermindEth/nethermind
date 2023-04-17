// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using FluentAssertions;
using Nethermind.Api;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Init.Steps.Migrations;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Ethereum.Steps.Migrations
{
    [TestFixture]
    public class ReceiptMigrationTests
    {
        [TestCase(null)]
        [TestCase(5)]
        public void RunMigration(int? migratedBlockNumber)
        {
            int chainLength = 10;
            IConfigProvider configProvider = Substitute.For<IConfigProvider>();
            BlockTreeBuilder blockTreeBuilder = Core.Test.Builders.Build.A.BlockTree().OfChainLength(chainLength);
            InMemoryReceiptStorage inMemoryReceiptStorage = new() { MigratedBlockNumber = migratedBlockNumber is not null ? 0 : long.MaxValue };
            InMemoryReceiptStorage outMemoryReceiptStorage = new() { MigratedBlockNumber = migratedBlockNumber is not null ? 0 : long.MaxValue };
            NethermindApi context = new()
            {
                ConfigProvider = configProvider,
                EthereumJsonSerializer = new EthereumJsonSerializer(),
                LogManager = LimboLogs.Instance,
                ReceiptStorage = new TestReceiptStorage(inMemoryReceiptStorage, outMemoryReceiptStorage),
                DbProvider = Substitute.For<IDbProvider>(),
                BlockTree = blockTreeBuilder.TestObject,
                Synchronizer = Substitute.For<ISynchronizer>(),
                ChainLevelInfoRepository = blockTreeBuilder.ChainLevelInfoRepository,
                SyncModeSelector = Substitute.For<ISyncModeSelector>()
            };

            configProvider.GetConfig<IReceiptConfig>().StoreReceipts.Returns(true);
            configProvider.GetConfig<IReceiptConfig>().ReceiptsMigration.Returns(true);
            context.SyncModeSelector.Current.Returns(SyncMode.WaitingForBlock);

            int txIndex = 0;
            for (int i = 1; i < chainLength; i++)
            {
                Block block = context.BlockTree.FindBlock(i);
                inMemoryReceiptStorage.Insert(block, new[] {
                        Core.Test.Builders.Build.A.Receipt.WithTransactionHash(TestItem.Keccaks[txIndex++]).TestObject,
                        Core.Test.Builders.Build.A.Receipt.WithTransactionHash(TestItem.Keccaks[txIndex++]).TestObject
                    });
            }

            ManualResetEvent guard = new(false);
            Keccak lastTransaction = TestItem.Keccaks[txIndex - 1];

            TestReceiptColumenDb receiptColumenDb = new();
            context.DbProvider.ReceiptsDb.Returns(receiptColumenDb);
            receiptColumenDb.RemoveFunc = (key) =>
            {
                if (key.Equals(lastTransaction.Bytes)) guard.Set();
            };

            ReceiptMigration migration = new(context);
            if (migratedBlockNumber.HasValue)
            {
                _ = migration.Run(migratedBlockNumber.Value);
            }
            else
            {
                migration.Run();
            }


            guard.WaitOne(TimeSpan.FromSeconds(1));
            int txCount = ((migratedBlockNumber ?? chainLength) - 1 - 1) * 2;

            receiptColumenDb.KeyWasRemoved(_ => true, txCount);
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
            public bool TryGetReceiptsIterator(long blockNumber, Keccak blockHash, out ReceiptsIterator iterator) => _outStorage.TryGetReceiptsIterator(blockNumber, blockHash, out iterator);

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

            public bool HasBlock(Keccak hash)
            {
                return _outStorage.HasBlock(hash);
            }

            public void EnsureCanonical(Block block)
            {
            }

            public event EventHandler<ReceiptsEventArgs> ReceiptsInserted { add { } remove { } }
        }

        class TestReceiptColumenDb : TestMemDb, IColumnsDb<ReceiptsColumns>
        {
            public IDbWithSpan GetColumnDb(ReceiptsColumns key)
            {
                return this;
            }

            public IEnumerable<ReceiptsColumns> ColumnKeys { get; }
        }
    }
}

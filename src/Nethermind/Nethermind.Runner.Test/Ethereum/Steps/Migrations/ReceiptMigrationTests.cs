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
using System.Threading;
using DotNetty.Transport.Channels;
using FluentAssertions;
using Nethermind.Api;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Init.Steps.Migrations;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Api;
using Nethermind.Serialization.Json;
using Nethermind.State.Repositories;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NSubstitute.ReceivedExtensions;
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
            InMemoryReceiptStorage inMemoryReceiptStorage = new() { MigratedBlockNumber = migratedBlockNumber != null ? 0 : long.MaxValue };
            InMemoryReceiptStorage outMemoryReceiptStorage = new() { MigratedBlockNumber = migratedBlockNumber != null ? 0 : long.MaxValue };
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

            configProvider.GetConfig<IInitConfig>().StoreReceipts.Returns(true);
            configProvider.GetConfig<IInitConfig>().ReceiptsMigration.Returns(true);
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
            context.DbProvider.ReceiptsDb.When(x => x.Remove(lastTransaction.Bytes)).Do(c => guard.Set());
            ReceiptMigration migration = new(context);
            if (migratedBlockNumber.HasValue)
            {
                migration.Run(migratedBlockNumber.Value);
            }
            else
            {
                migration.Run();
            }


            guard.WaitOne(TimeSpan.FromSeconds(1));
            int txCount = ((migratedBlockNumber ?? chainLength) - 1 - 1) * 2;
            context.DbProvider.ReceiptsDb.Received(Quantity.Exactly(txCount)).Remove(Arg.Any<byte[]>());
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
    }
}

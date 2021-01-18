﻿//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Specs;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.State.Repositories;
using Nethermind.Db.Blooms;
using Nethermind.Trie.Pruning;
using Nethermind.Facade;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.JsonRpc.Test.Modules
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class BoundedModulePoolTests
    {
        private BoundedModulePool<IEthModule> _modulePool;

        [SetUp]
        public async Task Initialize()
        {
            ISpecProvider specProvider = MainnetSpecProvider.Instance;
            ITxPool txPool = NullTxPool.Instance;
            IDbProvider dbProvider = await TestMemDbProvider.InitAsync();

            BlockTree blockTree = new BlockTree(
                dbProvider.BlocksDb,
                dbProvider.HeadersDb,
                dbProvider.BlockInfosDb,
                new ChainLevelInfoRepository(dbProvider.BlockInfosDb),
                specProvider,
                new BloomStorage(new BloomConfig(), dbProvider.HeadersDb, new InMemoryDictionaryFileStoreFactory()),
                new SyncConfig(),
                LimboLogs.Instance);
            
            _modulePool = new BoundedModulePool<IEthModule>(new EthModuleFactory(
                txPool,
                Substitute.For<ITxSender>(),
                NullWallet.Instance,
                blockTree,
                new JsonRpcConfig(),
                LimboLogs.Instance,
                Substitute.For<IStateReader>(),
                Substitute.For<IBlockchainBridgeFactory>()), 1, 1000);
        }

        [Test]
        public async Task Ensure_concurrency()
        {
            await _modulePool.GetModule(false);
        }

        [Test]
        public async Task Ensure_limited_exclusive()
        {
            await _modulePool.GetModule(false);
            Assert.ThrowsAsync<TimeoutException>(() => _modulePool.GetModule(false));
        }
        
        [Test]
        public async Task Ensure_returning_shared_does_not_change_concurrency()
        {
            IEthModule shared = await _modulePool.GetModule(true);
            _modulePool.ReturnModule(shared);
            await _modulePool.GetModule(false);
            Assert.ThrowsAsync<TimeoutException>(() => _modulePool.GetModule(false));
        }

        [Test]
        public async Task Ensure_unlimited_shared()
        {
            for (int i = 0; i < 1000; i++)
            {
                await _modulePool.GetModule(true);
            }
        }

        [Test]
        public async Task Ensure_that_shared_is_never_returned_as_exclusive()
        {
            IEthModule sharedModule = await _modulePool.GetModule(true);
            _modulePool.ReturnModule(sharedModule);

            const int iterations = 1000;
            Func<Task> rentReturnShared = async () =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    TestContext.Out.WriteLine($"Rent shared {i}");
                    IEthModule ethModule = await _modulePool.GetModule(true);
                    Assert.AreSame(sharedModule, ethModule);
                    _modulePool.ReturnModule(ethModule);
                    TestContext.Out.WriteLine($"Return shared {i}");
                }
            };

            Func<Task> rentReturnExclusive = async () =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    TestContext.Out.WriteLine($"Rent exclusive {i}");
                    IEthModule ethModule = await _modulePool.GetModule(false);
                    Assert.AreNotSame(sharedModule, ethModule);
                    _modulePool.ReturnModule(ethModule);
                    TestContext.Out.WriteLine($"Return exclusive {i}");
                }
            };

            Task a = Task.Run(rentReturnExclusive);
            Task b = Task.Run(rentReturnExclusive);
            Task c = Task.Run(rentReturnShared);
            Task d = Task.Run(rentReturnShared);

            await Task.WhenAll(a, b, c, d);
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task Can_rent_and_return(bool canBeShared)
        {
            IEthModule ethModule = await _modulePool.GetModule(canBeShared);
            _modulePool.ReturnModule(ethModule);
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task Can_rent_and_return_in_a_loop(bool canBeShared)
        {
            for (int i = 0; i < 1000; i++)
            {
                IEthModule ethModule = await _modulePool.GetModule(canBeShared);
                _modulePool.ReturnModule(ethModule);
            }
        }
    }
}

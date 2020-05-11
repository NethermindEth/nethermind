//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Specs;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.State.Repositories;
using Nethermind.Store;
using Nethermind.Store.Bloom;
using Nethermind.TxPool;
using Nethermind.Wallet;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.JsonRpc.Test.Modules
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class SingletonModulePoolTests
    {
        private SingletonModulePool<IEthModule> _modulePool;
        private EthModuleFactory _factory;

        [SetUp]
        public void Initialize()
        {
            ISpecProvider specProvider = MainnetSpecProvider.Instance;
            ITxPool txPool = NullTxPool.Instance;
            MemDbProvider dbProvider = new MemDbProvider();

            BlockTree blockTree = new BlockTree(dbProvider.BlocksDb, dbProvider.HeadersDb, dbProvider.BlockInfosDb, new ChainLevelInfoRepository(dbProvider.BlockInfosDb), specProvider, txPool, NullBloomStorage.Instance, new SyncConfig(), LimboLogs.Instance);
            _factory = new EthModuleFactory(
                dbProvider,
                txPool,
                NullWallet.Instance,
                blockTree,
                new EthereumEcdsa(MainnetSpecProvider.Instance, LimboLogs.Instance), 
                NullBlockProcessor.Instance, 
                new InMemoryReceiptStorage(), 
                specProvider, 
                new JsonRpcConfig(),
                NullBloomStorage.Instance,
                LimboLogs.Instance, false);
        }

        [Test]
        public void Cannot_return_exclusive_if_not_allowed()
        {
            _modulePool = new SingletonModulePool<IEthModule>(_factory.Create(), false);
            Assert.Throws<InvalidOperationException>(() => _modulePool.GetModule(false));
        }
        
        [Test]
        public void Can_return_exclusive_if_allowed()
        {
            _modulePool = new SingletonModulePool<IEthModule>(_factory.Create(), true);
            _modulePool.GetModule(false);
        }
        
        [Test]
        public void Ensure_unlimited_shared()
        {
            _modulePool = new SingletonModulePool<IEthModule>(_factory.Create(), true);
            _modulePool.GetModule(true);
        }
    }
}
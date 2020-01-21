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
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Specs;
using Nethermind.Facade.Config;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Store;
using Nethermind.Store.Repositories;
using Nethermind.Wallet;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    [TestFixture]
    public class SingletonModulePoolTests
    {
        private SingletonModulePool<IEthModule> _modulePool;
        private EthModuleFactory _factory;

        [SetUp]
        public void Initialize()
        {
            ISpecProvider specProvider = MainNetSpecProvider.Instance;
            ITxPool txPool = NullTxPool.Instance;
            MemDbProvider dbProvider = new MemDbProvider();

            BlockTree blockTree = new BlockTree(dbProvider.BlocksDb, dbProvider.HeadersDb, dbProvider.BlockInfosDb, new ChainLevelInfoRepository(dbProvider.BlockInfosDb), specProvider, txPool, new SyncConfig(), LimboLogs.Instance);
            _factory = new EthModuleFactory(dbProvider, txPool, NullWallet.Instance, blockTree, new EthereumEcdsa(MainNetSpecProvider.Instance, LimboLogs.Instance), NullBlockProcessor.Instance, new InMemoryReceiptStorage(), specProvider, new RpcConfig(), LimboLogs.Instance);
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
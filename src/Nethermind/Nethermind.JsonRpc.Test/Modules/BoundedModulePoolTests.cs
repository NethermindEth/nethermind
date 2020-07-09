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
using Nethermind.TxPool;
using Nethermind.Wallet;
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
        public void Initialize()
        {
            ISpecProvider specProvider = MainnetSpecProvider.Instance;
            ITxPool txPool = NullTxPool.Instance;
            MemDbProvider dbProvider = new MemDbProvider();
            IJsonRpcConfig jsonRpcConfig = new JsonRpcConfig();

            BlockTree blockTree = new BlockTree(
                dbProvider.BlocksDb,
                dbProvider.HeadersDb,
                dbProvider.BlockInfosDb,
                new ChainLevelInfoRepository(dbProvider.BlockInfosDb),
                specProvider,
                txPool,
                new BloomStorage(new BloomConfig(), dbProvider.HeadersDb, new InMemoryDictionaryFileStoreFactory()),
                new SyncConfig(),
                LimboLogs.Instance);
            
            _modulePool = new BoundedModulePool<IEthModule>(
                1, 
                new EthModuleFactory(
                    dbProvider, 
                    txPool, 
                    NullWallet.Instance, 
                    jsonRpcConfig,
                    blockTree, 
                    new EthereumEcdsa(ChainId.Mainnet, LimboLogs.Instance), 
                    NullBlockProcessor.Instance, 
                    new InMemoryReceiptStorage(), 
                    specProvider, 
                    new JsonRpcConfig(),
                    NullBloomStorage.Instance,
                    LimboLogs.Instance,
                    false));
        }

        [Test]
        public void Ensure_concurrency()
        {
            _modulePool.GetModule(false);
        }

        [Test]
        public void Ensure_limited_exclusive()
        {
            _modulePool.GetModule(false);
            Assert.Throws<TimeoutException>(() => _modulePool.GetModule(false));
        }
        
        [Test]
        public void Ensure_returning_shared_does_not_change_concurrency()
        {
            IEthModule shared = _modulePool.GetModule(true);
            _modulePool.ReturnModule(shared);
            _modulePool.GetModule(false);
            Assert.Throws<TimeoutException>(() => _modulePool.GetModule(false));
        }

        [Test]
        public void Ensure_unlimited_shared()
        {
            for (int i = 0; i < 1000; i++)
            {
                _modulePool.GetModule(true);
            }
        }

        [Test]
        public async Task Ensure_that_shared_is_never_returned_as_exclusive()
        {
            IEthModule sharedModule = _modulePool.GetModule(true);
            _modulePool.ReturnModule(sharedModule);

            const int iterations = 1000;
            Action rentReturnShared = () =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    TestContext.Out.WriteLine($"Rent shared {i}");
                    IEthModule ethModule = _modulePool.GetModule(true);
                    Assert.AreSame(sharedModule, ethModule);
                    _modulePool.ReturnModule(ethModule);
                    TestContext.Out.WriteLine($"Return shared {i}");
                }
            };

            Action rentReturnExclusive = () =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    TestContext.Out.WriteLine($"Rent exclusive {i}");
                    IEthModule ethModule = _modulePool.GetModule(false);
                    Assert.AreNotSame(sharedModule, ethModule);
                    _modulePool.ReturnModule(ethModule);
                    TestContext.Out.WriteLine($"Return exclusive {i}");
                }
            };

            Task a = new Task(rentReturnExclusive);
            Task b = new Task(rentReturnExclusive);
            Task c = new Task(rentReturnShared);
            Task d = new Task(rentReturnShared);

            a.Start();
            b.Start();
            c.Start();
            d.Start();

            await Task.WhenAll(a, b, c, d);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Can_rent_and_return(bool canBeShared)
        {
            IEthModule ethModule = _modulePool.GetModule(canBeShared);
            _modulePool.ReturnModule(ethModule);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Can_rent_and_return_in_a_loop(bool canBeShared)
        {
            for (int i = 0; i < 1000; i++)
            {
                IEthModule ethModule = _modulePool.GetModule(canBeShared);
                _modulePool.ReturnModule(ethModule);
            }
        }
    }
}
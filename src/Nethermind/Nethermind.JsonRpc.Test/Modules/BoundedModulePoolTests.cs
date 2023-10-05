// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Specs;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.State.Repositories;
using Nethermind.Db.Blooms;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
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
        private BoundedModulePool<IEthRpcModule> _modulePool = null!;

        [SetUp]
        public async Task Initialize()
        {
            ISpecProvider specProvider = MainnetSpecProvider.Instance;
            ITxPool txPool = NullTxPool.Instance;
            IDbProvider dbProvider = await TestMemDbProvider.InitAsync();

            BlockTree blockTree = new(
                dbProvider,
                new ChainLevelInfoRepository(dbProvider.BlockInfosDb),
                specProvider,
                new BloomStorage(new BloomConfig(), dbProvider.HeadersDb, new InMemoryDictionaryFileStoreFactory()),
                new SyncConfig(),
                LimboLogs.Instance);

            _modulePool = new BoundedModulePool<IEthRpcModule>(new EthModuleFactory(
                txPool,
                Substitute.For<ITxSender>(),
                NullWallet.Instance,
                blockTree,
                new JsonRpcConfig(),
                LimboLogs.Instance,
                Substitute.For<IStateReader>(),
                Substitute.For<IBlockchainBridgeFactory>(),
                Substitute.For<ISpecProvider>(),
                Substitute.For<IReceiptStorage>(),
                Substitute.For<IGasPriceOracle>(),
                Substitute.For<IEthSyncingInfo>()),
                 1, 1000);
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
            Assert.ThrowsAsync<ModuleRentalTimeoutException>(() => _modulePool.GetModule(false));
        }

        [Test]
        public async Task Ensure_returning_shared_does_not_change_concurrency()
        {
            IEthRpcModule shared = await _modulePool.GetModule(true);
            _modulePool.ReturnModule(shared);
            await _modulePool.GetModule(false);
            Assert.ThrowsAsync<ModuleRentalTimeoutException>(() => _modulePool.GetModule(false));
        }

        [Test]
        public async Task Ensure_unlimited_shared()
        {
            for (int i = 0; i < 1000; i++)
            {
                // TestContext.Out.WriteLine($"Rent shared {i}");
                IEthRpcModule ethRpcModule = await _modulePool.GetModule(true);
                Assert.That(ethRpcModule, Is.SameAs(sharedRpcModule));
                _modulePool.ReturnModule(ethRpcModule);
                // TestContext.Out.WriteLine($"Return shared {i}");
            }
        }

        [Test]
        public async Task Ensure_that_shared_is_never_returned_as_exclusive()
        {
            IEthRpcModule sharedRpcModule = await _modulePool.GetModule(true);
            _modulePool.ReturnModule(sharedRpcModule);

            const int iterations = 1000;
            Func<Task> rentReturnShared = async () =>
            {
                // TestContext.Out.WriteLine($"Rent exclusive {i}");
                IEthRpcModule ethRpcModule = await _modulePool.GetModule(false);
                Assert.That(ethRpcModule, Is.Not.SameAs(sharedRpcModule));
                _modulePool.ReturnModule(ethRpcModule);
                // TestContext.Out.WriteLine($"Return exclusive {i}");
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task Can_rent_and_return(bool canBeShared)
        {
            IEthRpcModule ethRpcModule = await _modulePool.GetModule(canBeShared);
            _modulePool.ReturnModule(ethRpcModule);
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task Can_rent_and_return_in_a_loop(bool canBeShared)
        {
            for (int i = 0; i < 1000; i++)
            {
                IEthRpcModule ethRpcModule = await _modulePool.GetModule(canBeShared);
                _modulePool.ReturnModule(ethRpcModule);
            }
        }
    }
}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;
using System.Threading.Tasks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Config;

namespace Nethermind.JsonRpc.Test.Modules
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class SingletonModulePoolTests
    {
        private SingletonModulePool<IEthRpcModule> _modulePool = null!;
        private EthModuleFactory _factory = null!;

        [SetUp]
        public Task Initialize()
        {
            ITxPool txPool = NullTxPool.Instance;

            BlockTree blockTree = Build.A.BlockTree()
                .TestObject;

            _factory = new EthModuleFactory(
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
                Substitute.For<IEthSyncingInfo>(),
                Substitute.For<IFeeHistoryOracle>(),
                new BlocksConfig().SecondsPerSlot);
            return Task.CompletedTask;
        }

        [Test]
        public void Cannot_return_exclusive_if_not_allowed()
        {
            _modulePool = new SingletonModulePool<IEthRpcModule>(_factory.Create(), false);
            Assert.Throws<InvalidOperationException>(() => _modulePool.GetModule(false));
        }

        [Test]
        public void Can_return_exclusive_if_allowed()
        {
            _modulePool = new SingletonModulePool<IEthRpcModule>(_factory.Create(), true);
            _modulePool.GetModule(false);
        }

        [Test]
        public void Ensure_unlimited_shared()
        {
            _modulePool = new SingletonModulePool<IEthRpcModule>(_factory.Create(), true);
            _modulePool.GetModule(true);
        }
    }
}

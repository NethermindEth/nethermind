// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Facade;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Db.Blooms;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using Nethermind.Wallet;
using BlockTree = Nethermind.Blockchain.BlockTree;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Modules;
using Nethermind.Facade.Find;
using Nethermind.Facade.Simulate;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using NSubstitute;

namespace Nethermind.JsonRpc.Benchmark
{
    public class EthModuleBenchmarks
    {
        private EthRpcModule _ethModule;
        private IContainer _container;

        [GlobalSetup]
        public void GlobalSetup()
        {
            _container = new ContainerBuilder()
                .AddModule(new TestNethermindModule())
                .AddSingleton<ISpecProvider>(MainnetSpecProvider.Instance)
                .Build();

            IWorldState stateProvider = _container.Resolve<IWorldStateManager>().GlobalWorldState;
            stateProvider.CreateAccount(Address.Zero, 1000.Ether());
            IReleaseSpec spec = MainnetSpecProvider.Instance.GenesisSpec;
            stateProvider.Commit(spec);
            stateProvider.CommitTree(0);

            Block genesisBlock = Build.A.Block.Genesis.TestObject;
            IBlockTree blockTree = _container.Resolve<IBlockTree>();
            blockTree.SuggestBlock(genesisBlock);

            Block block1 = Build.A.Block.WithParent(genesisBlock).WithNumber(1).TestObject;
            blockTree.SuggestBlock(block1);

            IBlockchainProcessor blockchainProcessor = _container.Resolve<IMainProcessingContext>().BlockchainProcessor;
            blockchainProcessor.Process(genesisBlock, ProcessingOptions.None, NullBlockTracer.Instance);
            blockchainProcessor.Process(block1, ProcessingOptions.None, NullBlockTracer.Instance);

            IBlockchainBridge bridge = _container.Resolve<IBlockchainBridgeFactory>().CreateBlockchainBridge();

            ISpecProvider specProvider = _container.Resolve<ISpecProvider>();
            FeeHistoryOracle feeHistoryOracle = new(blockTree, NullReceiptStorage.Instance, specProvider);

            _ethModule = new EthRpcModule(
                _container.Resolve<IJsonRpcConfig>(),
                bridge,
                blockTree,
                _container.Resolve<IReceiptFinder>(),
                _container.Resolve<IStateReader>(),
                NullTxPool.Instance,
                NullTxSender.Instance,
                NullWallet.Instance,
                LimboLogs.Instance,
                specProvider,
                _container.Resolve<IGasPriceOracle>(),
                _container.Resolve<IEthSyncingInfo>(),
                feeHistoryOracle,
                _container.Resolve<IProtocolsManager>(),
                new BlocksConfig().SecondsPerSlot);
        }

        [GlobalCleanup]
        public void TearDown()
        {
            _container.Dispose();
        }

        [Benchmark]
        public void Current()
        {
            _ethModule.eth_getBalance(Address.Zero, new BlockParameter(1));
            _ethModule.eth_getBlockByNumber(new BlockParameter(1), false);
        }
    }
}

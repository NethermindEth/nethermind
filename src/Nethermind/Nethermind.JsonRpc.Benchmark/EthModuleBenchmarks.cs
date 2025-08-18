// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Facade;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Nethermind.Config;
using Nethermind.Core.Test.Modules;
using Nethermind.Network;

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
                _container.Resolve<IForkInfo>(),
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

        [Benchmark]
        [Arguments(10)]
        [Arguments(100)]
        public void EthCallBatch(int batchSize)
        {
            var transaction = new TransactionForRpc
            {
                To = Address.Zero,
                Data = new byte[] { 0x60, 0x00, 0x60, 0x00, 0xf3 }, // Simple return bytecode
                Gas = 21000
            };

            for (int i = 0; i < batchSize; i++)
            {
                _ethModule.eth_call(transaction, new BlockParameter(1L));
            }
        }

        [Benchmark]
        public void GetLogsHistoricalRange()
        {
            // Simulate getLogs call for a small historical range (common in AI agents)
            var logs = _ethModule.eth_getLogs(new FilterForRpc
            {
                FromBlock = new BlockParameter(0L),
                ToBlock = new BlockParameter(1L),
                Address = Address.Zero
            });
        }
    }
}

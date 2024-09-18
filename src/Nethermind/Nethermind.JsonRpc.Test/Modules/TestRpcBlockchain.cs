// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Facade;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.KeyStore;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using Nethermind.Wallet;

using Nethermind.Config;
using Nethermind.Db;
using Nethermind.Facade.Simulate;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;

namespace Nethermind.JsonRpc.Test.Modules
{
    public class TestRpcBlockchain : TestBlockchain
    {
        public IJsonRpcConfig RpcConfig { get; private set; } = new JsonRpcConfig();
        public IEthRpcModule EthRpcModule { get; private set; } = null!;
        public IBlockchainBridge Bridge { get; private set; } = null!;
        public ITxSealer TxSealer { get; private set; } = null!;
        public ITxSender TxSender { get; private set; } = null!;
        public IReceiptFinder ReceiptFinder { get; private set; } = null!;
        public IGasPriceOracle GasPriceOracle { get; private set; } = null!;

        public IKeyStore KeyStore { get; } = new MemKeyStore(TestItem.PrivateKeys, Path.Combine("testKeyStoreDir", Path.GetRandomFileName()));
        public IWallet TestWallet { get; } =
            new DevKeyStoreWallet(new MemKeyStore(TestItem.PrivateKeys, Path.Combine("testKeyStoreDir", Path.GetRandomFileName())),
                LimboLogs.Instance);

        public IFeeHistoryOracle? FeeHistoryOracle { get; private set; }
        public static Builder<TestRpcBlockchain> ForTest(string sealEngineType) => ForTest<TestRpcBlockchain>(sealEngineType);

        public static Builder<T> ForTest<T>(string sealEngineType) where T : TestRpcBlockchain, new() =>
            new(new T { SealEngineType = sealEngineType });

        public static Builder<T> ForTest<T>(T blockchain) where T : TestRpcBlockchain =>
            new(blockchain);

        public class Builder<T>(T blockchain) where T : TestRpcBlockchain
        {
            private readonly TestRpcBlockchain _blockchain = blockchain;

            public Builder<T> WithBlockchainBridge(IBlockchainBridge blockchainBridge)
            {
                _blockchain.Bridge = blockchainBridge;
                return this;
            }

            public Builder<T> WithBlockFinder(IBlockFinder blockFinder)
            {
                _blockchain.BlockFinder = blockFinder;
                return this;
            }

            public Builder<T> WithReceiptFinder(IReceiptFinder receiptFinder)
            {
                _blockchain.ReceiptFinder = receiptFinder;
                return this;
            }
            public Builder<T> WithTxSender(ITxSender txSender)
            {
                _blockchain.TxSender = txSender;
                return this;
            }

            public Builder<T> WithGenesisBlockBuilder(BlockBuilder blockBuilder)
            {
                _blockchain.GenesisBlockBuilder = blockBuilder;
                return this;
            }

            public Builder<T> WithGasPriceOracle(IGasPriceOracle gasPriceOracle)
            {
                _blockchain.GasPriceOracle = gasPriceOracle;
                return this;
            }

            public Builder<T> WithConfig(IJsonRpcConfig config)
            {
                _blockchain.RpcConfig = config;
                return this;
            }

            public Builder<T> WithEthRpcModule(Func<TestRpcBlockchain, IEthRpcModule> builder)
            {
                _blockchain._ethRpcModuleBuilder = builder;
                return this;
            }

            public async Task<T> Build(
                ISpecProvider? specProvider = null,
                UInt256? initialValues = null) =>
                (T)(await _blockchain.Build(specProvider, initialValues, true));
        }

        private Func<TestRpcBlockchain, IEthRpcModule> _ethRpcModuleBuilder = @this => new EthRpcModule(
            @this.RpcConfig,
            @this.Bridge,
            @this.BlockFinder,
            @this.ReceiptFinder,
            @this.StateReader,
            @this.TxPool,
            @this.TxSender,
            @this.TestWallet,
            LimboLogs.Instance,
            @this.SpecProvider,
            @this.GasPriceOracle,
            new EthSyncingInfo(@this.BlockTree, @this.ReceiptStorage, new SyncConfig(),
                new StaticSelector(SyncMode.All), Substitute.For<ISyncProgressResolver>(), @this.LogManager),
            @this.FeeHistoryOracle ??
            new FeeHistoryOracle(@this.BlockTree, @this.ReceiptStorage, @this.SpecProvider),
            new BlocksConfig().SecondsPerSlot);

        protected override async Task<TestBlockchain> Build(
            ISpecProvider? specProvider = null,
            UInt256? initialValues = null,
            bool addBlockOnStart = true)
        {
            specProvider ??= new TestSpecProvider(Berlin.Instance);
            await base.Build(specProvider, initialValues, addBlockOnStart);
            IFilterStore filterStore = new FilterStore();
            IFilterManager filterManager = new FilterManager(filterStore, BlockProcessor, TxPool, LimboLogs.Instance);
            var dbProvider = new ReadOnlyDbProvider(DbProvider, false);
            IReadOnlyBlockTree? roBlockTree = BlockTree!.AsReadOnly();
            ReadOnlyTxProcessingEnv processingEnv = new(
                WorldStateManager,
                roBlockTree,
                SpecProvider,
                LimboLogs.Instance);
            SimulateReadOnlyBlocksProcessingEnvFactory simulateProcessingEnvFactory = new SimulateReadOnlyBlocksProcessingEnvFactory(
                WorldStateManager.GlobalWorldStateProvider,
                roBlockTree,
                new ReadOnlyDbProvider(dbProvider, true),
                SpecProvider,
                LimboLogs.Instance);

            BlocksConfig blocksConfig = new BlocksConfig();
            ReceiptFinder ??= ReceiptStorage;
            Bridge ??= new BlockchainBridge(processingEnv, simulateProcessingEnvFactory, TxPool, ReceiptFinder, filterStore, filterManager, EthereumEcdsa, Timestamper, LogFinder, SpecProvider, blocksConfig, false);
            BlockFinder ??= BlockTree;
            GasPriceOracle ??= new GasPriceOracle(BlockFinder, SpecProvider, LogManager);

            ITxSigner txSigner = new WalletTxSigner(TestWallet, specProvider.ChainId);
            TxSealer = new TxSealer(txSigner, Timestamper);
            TxSender ??= new TxPoolSender(TxPool, TxSealer, NonceManager, EthereumEcdsa ?? new EthereumEcdsa(specProvider.ChainId));
            GasPriceOracle ??= new GasPriceOracle(BlockFinder, SpecProvider, LogManager);
            FeeHistoryOracle ??= new FeeHistoryOracle(BlockTree, ReceiptStorage, SpecProvider);
            EthRpcModule = _ethRpcModuleBuilder(this);

            return this;
        }

        public Task<string> TestEthRpc(string method, params string[] parameters) =>
            RpcTest.TestSerializedRequest(EthRpcModule, method, parameters);

        public Task<string> TestSerializedRequest<T>(T module, string method, params string[] parameters) where T : class, IRpcModule =>
            RpcTest.TestSerializedRequest(module, method, parameters);
    }
}

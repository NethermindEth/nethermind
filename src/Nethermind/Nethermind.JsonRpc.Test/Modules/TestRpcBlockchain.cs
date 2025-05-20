// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
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
using Nethermind.State;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Consensus.Rewards;
using System.IO.Abstractions;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Timers;
using Nethermind.JsonRpc.Modules.Trace;

namespace Nethermind.JsonRpc.Test.Modules
{
    public class TestRpcBlockchain : TestBlockchain
    {
        public IJsonRpcConfig RpcConfig { get; private set; } = new JsonRpcConfig();
        public IEthRpcModule EthRpcModule { get; private set; } = null!;
        public IDebugRpcModule DebugRpcModule { get; private set; } = null!;
        public ITraceRpcModule TraceRpcModule { get; private set; } = null!;
        public IBlockchainBridge Bridge { get; private set; } = null!;
        public ITxSealer TxSealer { get; private set; } = null!;
        public ITxSender TxSender { get; private set; } = null!;
        public IReceiptFinder ReceiptFinder => Container.Resolve<IReceiptFinder>();
        public IGasPriceOracle GasPriceOracle { get; private set; } = null!;
        public IOverridableWorldScope OverridableWorldStateManager { get; private set; } = null!;

        public IKeyStore KeyStore { get; } = new MemKeyStore(TestItem.PrivateKeys, Path.Combine("testKeyStoreDir", Path.GetRandomFileName()));
        public IWallet TestWallet { get; } =
            new DevKeyStoreWallet(new MemKeyStore(TestItem.PrivateKeys, Path.Combine("testKeyStoreDir", Path.GetRandomFileName())),
                LimboLogs.Instance);

        public IFeeHistoryOracle? FeeHistoryOracle { get; private set; }
        public static Builder<TestRpcBlockchain> ForTest(string sealEngineType, long? testTimeout = null) => ForTest<TestRpcBlockchain>(sealEngineType, testTimeout);

        public static Builder<T> ForTest<T>(string sealEngineType, long? testTimout = null) where T : TestRpcBlockchain, new() =>
            new(new T { SealEngineType = sealEngineType, TestTimout = testTimout ?? DefaultTimeout });

        public static Builder<T> ForTest<T>(T blockchain) where T : TestRpcBlockchain =>
            new(blockchain);

        public class Builder<T>(T blockchain) where T : TestRpcBlockchain
        {
            private readonly TestRpcBlockchain _blockchain = blockchain;

            private IBlockFinder? _blockFinderOverride = null;
            private IReceiptFinder? _receiptFinderOverride = null;

            public Builder<T> WithBlockchainBridge(IBlockchainBridge blockchainBridge)
            {
                _blockchain.Bridge = blockchainBridge;
                return this;
            }

            public Builder<T> WithBlocksConfig(BlocksConfig blocksConfig)
            {
                _blockchain.BlocksConfig = blocksConfig;
                return this;
            }

            public Builder<T> WithBlockFinder(IBlockFinder blockFinder)
            {
                _blockFinderOverride = blockFinder;
                return this;
            }

            public Builder<T> WithReceiptFinder(IReceiptFinder receiptFinder)
            {
                _receiptFinderOverride = receiptFinder;
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

            public Task<T> Build()
            {
                return Build((ISpecProvider?)null);
            }

            public Task<T> Build(ISpecProvider? specProvider)
            {
                return Build((builder) =>
                {
                    if (specProvider is not null) builder.AddSingleton<ISpecProvider>(specProvider);
                });
            }

            public Task<T> Build(UInt256 initialValues)
            {
                return Build((builder) =>
                {
                    builder.ConfigureTestConfiguration(conf =>
                    {
                        conf.AccountInitialValue = initialValues;
                    });
                });
            }

            public async Task<T> Build(Action<ContainerBuilder> configurer)
            {
                return (T)await _blockchain.Build(configurer: (builder) =>
                {
                    configurer?.Invoke(builder);
                    if (_blockFinderOverride is not null) builder.AddSingleton(_blockFinderOverride);
                    if (_receiptFinderOverride is not null) builder.AddSingleton(_receiptFinderOverride);
                });
            }
        }

        private Func<TestRpcBlockchain, IEthRpcModule> _ethRpcModuleBuilder = static @this => new EthRpcModule(
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
            new EthSyncingInfo(@this.BlockTree, Substitute.For<ISyncPointers>(), @this.Container.Resolve<ISyncConfig>(),
            new StaticSelector(SyncMode.All), Substitute.For<ISyncProgressResolver>(), @this.LogManager),
            @this.FeeHistoryOracle ??
            new FeeHistoryOracle(@this.BlockTree, @this.ReceiptStorage, @this.SpecProvider),
            @this.BlocksConfig.SecondsPerSlot);

        private readonly Func<TestRpcBlockchain, IDebugRpcModule> _debugRpcModuleBuilder = static @this => new DebugModuleFactory(
            @this.WorldStateManager,
            @this.DbProvider,
            @this.BlockTree,
            @this.RpcConfig,
            @this.Bridge,
            @this.BlocksConfig.SecondsPerSlot,
            @this.BlockValidator,
            @this.BlockPreprocessorStep,
            new RewardCalculator(@this.SpecProvider),
            @this.ReceiptStorage,
            Substitute.For<IReceiptsMigration>(),
            Substitute.For<IConfigProvider>(),
            @this.SpecProvider,
            Substitute.For<ISyncModeSelector>(),
            new BadBlockStore(@this.BlocksDb, 100),
            new FileSystem(),
            @this.LogManager).Create();


        private readonly Func<TestRpcBlockchain, ITraceRpcModule> _traceRpcModuleBuilder = static @this => new TraceModuleFactory(
            @this.WorldStateManager,
            @this.BlockTree,
            @this.RpcConfig,
            @this.Bridge,
            new BlocksConfig().SecondsPerSlot,
            @this.BlockPreprocessorStep,
            new RewardCalculator(@this.SpecProvider),
            @this.ReceiptStorage,
            @this.SpecProvider,
            @this.PoSSwitcher,
            @this.LogManager
        ).Create();

        protected override async Task<TestBlockchain> Build(Action<ContainerBuilder>? configurer = null)
        {
            await base.Build(builder =>
            {
                builder.AddSingleton<ISpecProvider>(new TestSpecProvider(Berlin.Instance));
                configurer?.Invoke(builder);
            });

            IFilterStore filterStore = new FilterStore(new TimerFactory());
            IFilterManager filterManager = new FilterManager(filterStore, BlockProcessor, TxPool, LimboLogs.Instance);
            var dbProvider = new ReadOnlyDbProvider(DbProvider, false);
            IReadOnlyBlockTree? roBlockTree = BlockTree!.AsReadOnly();
            IOverridableWorldScope overridableWorldStateManager = WorldStateManager.CreateOverridableWorldScope();
            OverridableTxProcessingEnv processingEnv = new(
                WorldStateManager.CreateOverridableWorldScope(),
                roBlockTree,
                SpecProvider,
                LimboLogs.Instance);
            SimulateReadOnlyBlocksProcessingEnvFactory simulateProcessingEnvFactory = new SimulateReadOnlyBlocksProcessingEnvFactory(
                WorldStateManager,
                roBlockTree,
                new ReadOnlyDbProvider(dbProvider, true),
                SpecProvider,
                SimulateTransactionProcessorFactory.Instance,
                LimboLogs.Instance);

            Bridge ??= new BlockchainBridge(processingEnv, simulateProcessingEnvFactory, TxPool, ReceiptFinder, filterStore, filterManager, EthereumEcdsa, Timestamper, LogFinder, SpecProvider, BlocksConfig, false);
            GasPriceOracle ??= new GasPriceOracle(BlockFinder, SpecProvider, LogManager);

            ITxSigner txSigner = new WalletTxSigner(TestWallet, SpecProvider.ChainId);
            TxSealer = new TxSealer(txSigner, Timestamper);
            TxSender ??= new TxPoolSender(TxPool, TxSealer, NonceManager, EthereumEcdsa ?? new EthereumEcdsa(SpecProvider.ChainId));
            GasPriceOracle ??= new GasPriceOracle(BlockFinder, SpecProvider, LogManager);
            FeeHistoryOracle ??= new FeeHistoryOracle(BlockTree, ReceiptStorage, SpecProvider);
            EthRpcModule = _ethRpcModuleBuilder(this);
            TraceRpcModule = _traceRpcModuleBuilder(this);
            DebugRpcModule = _debugRpcModuleBuilder(this);
            OverridableWorldStateManager = overridableWorldStateManager;

            return this;
        }

        public Task<string> TestEthRpc(string method, params object?[]? parameters) =>
            RpcTest.TestSerializedRequest(EthRpcModule, method, parameters);
    }
}

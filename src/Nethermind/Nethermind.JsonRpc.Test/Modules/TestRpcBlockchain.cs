// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading.Tasks;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Facade;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.KeyStore;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Nethermind.Config;
using Nethermind.Synchronization;
using NSubstitute;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Consensus.Rewards;
using Autofac;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Scheduler;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Test.Container;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Network;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Json;
using Nethermind.Stats;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.JsonRpc.Test.Modules
{
    public class TestRpcBlockchain : TestBlockchain
    {
        public IJsonRpcConfig RpcConfig { get; private set; } = new JsonRpcConfig();
        public IEthRpcModule EthRpcModule { get; private set; } = null!;
        public IDebugRpcModule DebugRpcModule => Container.Resolve<IRpcModuleFactory<IDebugRpcModule>>().Create();
        public ITraceRpcModule TraceRpcModule => Container.Resolve<IRpcModuleFactory<ITraceRpcModule>>().Create();
        public IBlockchainBridge Bridge => Container.Resolve<IBlockchainBridge>();
        public ITxSealer TxSealer { get; private set; } = null!;
        public ITxSender TxSender { get; private set; } = null!;
        public IReceiptFinder ReceiptFinder => Container.Resolve<IReceiptFinder>();
        public IGasPriceOracle GasPriceOracle { get; private set; } = null!;
        public IProtocolsManager ProtocolsManager { get; private set; } = null!;

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
            private IBlockchainBridge? _blockchainBridgeOverride = null;
            private IBlocksConfig? _blocksConfigOverride = null;

            public Builder<T> WithBlockchainBridge(IBlockchainBridge blockchainBridge)
            {
                _blockchainBridgeOverride = blockchainBridge;
                return this;
            }

            public Builder<T> WithBlocksConfig(IBlocksConfig blocksConfig)
            {
                _blocksConfigOverride = blocksConfig;
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

                    // So only the rpc module need to have actual reward calculator....
                    // Can't set globally as that would cause block production to fail with invalid stateroot
                    // as the reward is being applied.
                    // TODO: Double check if block production have the same reward calculator
                    builder.UpdateSingleton<IRpcModuleFactory<ITraceRpcModule>>(builder => builder.AddSingleton<IRewardCalculatorSource, RewardCalculator>());

                    if (_blockFinderOverride is not null) builder.AddSingleton(_blockFinderOverride);
                    if (_receiptFinderOverride is not null) builder.AddSingleton(_receiptFinderOverride);
                    if (_blockchainBridgeOverride is not null) builder.AddSingleton(_blockchainBridgeOverride);
                    if (_blocksConfigOverride is not null) builder.AddSingleton(_blocksConfigOverride);

                    builder.AddKeyedSingleton<ITxValidator>(ITxValidator.HeadTxValidatorKey, new HeadTxValidator());
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
            @this.ProtocolsManager,
            @this.ForkInfo,
            @this.BlocksConfig.SecondsPerSlot);

        protected override async Task<TestBlockchain> Build(Action<ContainerBuilder>? configurer = null)
        {
            @EthereumJsonSerializer.FollowStandardizationRules = RpcConfig.FollowStandardizationRules;
            await base.Build(builder =>
            {
                builder.AddSingleton<ISpecProvider>(new TestSpecProvider(Berlin.Instance));
                configurer?.Invoke(builder);
            });

            GasPriceOracle ??= new GasPriceOracle(BlockFinder, SpecProvider, LogManager);

            ITxSigner txSigner = new WalletTxSigner(TestWallet, SpecProvider.ChainId);
            TxSealer = new TxSealer(txSigner, Timestamper);
            TxSender ??= new TxPoolSender(TxPool, TxSealer, NonceManager, EthereumEcdsa ?? new EthereumEcdsa(SpecProvider.ChainId));
            GasPriceOracle ??= new GasPriceOracle(BlockFinder, SpecProvider, LogManager);
            FeeHistoryOracle ??= new FeeHistoryOracle(BlockTree, ReceiptStorage, SpecProvider);

            ProtocolsManager = new ProtocolsManager(
                Substitute.For<ISyncPeerPool>(),
                Substitute.For<ISyncServer>(),
                Substitute.For<IBackgroundTaskScheduler>(),
                TxPool,
                Substitute.For<IPooledTxsRequestor>(),
                Substitute.For<IDiscoveryApp>(),
                Substitute.For<IMessageSerializationService>(),
                Substitute.For<IRlpxHost>(),
                Substitute.For<INodeStatsManager>(),
                Substitute.For<IProtocolValidator>(),
                Substitute.For<INetworkStorage>(),
                Container.Resolve<IForkInfo>(),
                Substitute.For<IGossipPolicy>(),
                WorldStateManager,
                Substitute.For<IBlockFinder>(),
                LimboLogs.Instance,
                Substitute.For<ITxGossipPolicy>()
            );

            EthRpcModule = _ethRpcModuleBuilder(this);

            return this;
        }

        public Task<string> TestEthRpc(string method, params object?[]? parameters) =>
            RpcTest.TestSerializedRequest(EthRpcModule, method, parameters);

        private IBlockchainProcessor? _currentBlockchainProcessor;

        public async Task RestartBlockchainProcessor()
        {
            if (_currentBlockchainProcessor is not null)
            {
                await _currentBlockchainProcessor.StopAsync();
            }
            else
            {
                await BlockchainProcessor.StopAsync();
            }

            // simulating restarts - we stopped the old blockchain processor and create the new one
            _currentBlockchainProcessor = new BlockchainProcessor(BlockTree, BranchProcessor,
                BlockPreprocessorStep, StateReader, LimboLogs.Instance, Nethermind.Consensus.Processing.BlockchainProcessor.Options.Default);
            _currentBlockchainProcessor.Start();
        }
    }
}

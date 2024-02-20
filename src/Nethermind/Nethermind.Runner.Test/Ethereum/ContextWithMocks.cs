// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.IO.Abstractions;
using System.Net;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Services;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Db.Blooms;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Eth;
using Nethermind.Grpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.KeyStore;
using Nethermind.Monitoring;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Nethermind.Sockets;
using Nethermind.Specs;
using NSubstitute;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Runner.Modules;

namespace Nethermind.Runner.Test.Ethereum
{
    public static class Build
    {
        public static ContainerBuilder BasicTestContainerBuilder()
        {
            ContainerBuilder builder = new ContainerBuilder();
            IConfigProvider configProvider = new ConfigProvider();

            builder.RegisterInstance(new EthereumJsonSerializer()).AsImplementedInterfaces();
            builder.RegisterInstance(LimboLogs.Instance).AsImplementedInterfaces();
            builder.RegisterInstance(configProvider);
            builder.RegisterInstance(Substitute.For<IProcessExitSource>());
            builder.RegisterInstance(new ChainSpec());

            builder.RegisterModule(new BaseModule());
            builder.RegisterModule(new CoreModule());
            builder.RegisterModule(new StateModule());
            builder.RegisterModule(new DatabaseModule());
            builder.RegisterModule(new NetworkModule());
            builder.RegisterModule(new KeyStoreModule());

            builder.RegisterInstance(new ProtectedPrivateKey(TestItem.PrivateKeyA, ""))
                .Keyed<ProtectedPrivateKey>(ComponentKey.NodeKey)
                .Keyed<ProtectedPrivateKey>(ComponentKey.SignerKey);

            builder.RegisterInstance(MainnetSpecProvider.Instance)
                .As<ISpecProvider>();
            builder.RegisterInstance(new Enode(TestItem.PublicKeyA, IPAddress.Any, 30303))
                .As<IEnode>();

            return builder;
        }

        public static NethermindApi ContextWithMocksAndBasicContainer(IContainer container = null)
        {
            return ContextWithMocks(BasicTestContainerBuilder().Build());
        }

        public static NethermindApi ContextWithMocks(IContainer container = null)
        {
            if (container == null)
            {
                var containerBuilder = new ContainerBuilder();
                containerBuilder.RegisterInstance(Substitute.For<IConfigProvider>()).As<IConfigProvider>();
                containerBuilder.RegisterInstance(Substitute.For<ILogManager>()).As<ILogManager>();
                containerBuilder.RegisterInstance(Substitute.For<ISpecProvider>()).As<ISpecProvider>();
                containerBuilder.RegisterInstance(Substitute.For<IProcessExitSource>()).As<IProcessExitSource>();
                containerBuilder.RegisterInstance(Substitute.For<IGasLimitCalculator>()).As<IGasLimitCalculator>();
                containerBuilder.RegisterInstance(Substitute.For<INodeStatsManager>()).As<INodeStatsManager>();
                containerBuilder.RegisterInstance(Substitute.For<ITimerFactory>()).As<ITimerFactory>();
                containerBuilder.RegisterInstance(Substitute.For<ITimestamper>()).As<ITimestamper>();
                containerBuilder.RegisterInstance(Substitute.For<IWallet>()).As<IWallet>();
                containerBuilder.RegisterInstance(Substitute.For<IFileSystem>()).As<IFileSystem>();
                containerBuilder.RegisterInstance(Substitute.For<IKeyStore>()).As<IKeyStore>();
                containerBuilder.RegisterInstance(Substitute.For<IEnode>()).As<IEnode>();
                containerBuilder.RegisterInstance(Substitute.For<IDbProvider>()).As<IDbProvider>();
                containerBuilder.RegisterInstance(Substitute.For<IBlockTree>()).As<IBlockTree>();
                containerBuilder.RegisterInstance(Substitute.For<IBloomStorage>()).As<IBloomStorage>();
                containerBuilder.RegisterInstance(Substitute.For<IReceiptStorage>()).As<IReceiptStorage>();
                containerBuilder.RegisterInstance(Substitute.For<IReceiptFinder>()).As<IReceiptFinder>();
                containerBuilder.RegisterInstance(Substitute.For<ISigner>()).As<ISigner>();
                containerBuilder.RegisterInstance(Substitute.For<ILogFinder>()).As<ILogFinder>();
                containerBuilder.RegisterInstance(Substitute.For<ISignerStore>()).As<ISignerStore>();
                containerBuilder.RegisterInstance(Substitute.For<IChainLevelInfoRepository>()).As<IChainLevelInfoRepository>();
                containerBuilder.RegisterInstance(Substitute.For<IBlockStore>()).As<IBlockStore>();
                containerBuilder.RegisterInstance(Substitute.For<IEthereumEcdsa>()).As<IEthereumEcdsa>();
                containerBuilder.RegisterInstance(Substitute.For<IWorldState>()).As<IWorldState>();
                containerBuilder.RegisterInstance(Substitute.For<IStateReader>()).As<IStateReader>();
                containerBuilder.RegisterInstance(Substitute.For<ITrieStore>()).As<ITrieStore>();
                containerBuilder.RegisterInstance(Substitute.For<IWitnessRepository>()).As<IWitnessRepository>();
                containerBuilder.RegisterInstance(Substitute.For<IWorldStateManager>()).As<IWorldStateManager>();
                containerBuilder.RegisterInstance(Substitute.For<IReadOnlyStateProvider>()).As<IReadOnlyStateProvider>();
                containerBuilder.RegisterInstance(new ChainSpec());
                container = containerBuilder.Build();
            }

            var api = new NethermindApi(container)
            {
                TxPool = Substitute.For<ITxPool>(),
                SyncServer = Substitute.For<ISyncServer>(),
                PeerManager = Substitute.For<IPeerManager>(),
                PeerPool = Substitute.For<IPeerPool>(),
                MainBlockProcessor = Substitute.For<IBlockProcessor>(),
                BlockValidator = Substitute.For<IBlockValidator>(),
                RewardCalculatorSource = Substitute.For<IRewardCalculatorSource>(),
                TxPoolInfoProvider = Substitute.For<ITxPoolInfoProvider>(),
                StaticNodesManager = Substitute.For<IStaticNodesManager>(),
                Sealer = Substitute.For<ISealer>(),
                Synchronizer = Substitute.For<ISynchronizer>(),
                BlockchainProcessor = Substitute.For<IBlockchainProcessor>(),
                BlockProducer = Substitute.For<IBlockProducer>(),
                DiscoveryApp = Substitute.For<IDiscoveryApp>(),
                FilterManager = Substitute.For<IFilterManager>(),
                FilterStore = Substitute.For<IFilterStore>(),
                GrpcServer = Substitute.For<IGrpcServer>(),
                HeaderValidator = Substitute.For<IHeaderValidator>(),
                MonitoringService = Substitute.For<IMonitoringService>(),
                ProtocolsManager = Substitute.For<IProtocolsManager>(),
                ProtocolValidator = Substitute.For<IProtocolValidator>(),
                RlpxPeer = Substitute.For<IRlpxHost>(),
                SealValidator = Substitute.For<ISealValidator>(),
                SessionMonitor = Substitute.For<ISessionMonitor>(),
                TransactionProcessor = Substitute.For<ITransactionProcessor>(),
                TxSender = Substitute.For<ITxSender>(),
                BlockProcessingQueue = Substitute.For<IBlockProcessingQueue>(),
                RpcModuleProvider = Substitute.For<IRpcModuleProvider>(),
                SyncModeSelector = Substitute.For<ISyncModeSelector>(),
                SyncPeerPool = Substitute.For<ISyncPeerPool>(),
                PeerDifficultyRefreshPool = Substitute.For<IPeerDifficultyRefreshPool>(),
                WebSocketsManager = Substitute.For<IWebSocketsManager>(),
                BlockProducerEnvFactory = Substitute.For<IBlockProducerEnvFactory>(),
                TransactionComparerProvider = Substitute.For<ITransactionComparerProvider>(),
                GasPriceOracle = Substitute.For<IGasPriceOracle>(),
                EthSyncingInfo = Substitute.For<IEthSyncingInfo>(),
                HealthHintService = Substitute.For<IHealthHintService>(),
                TxValidator = new TxValidator(MainnetSpecProvider.Instance.ChainId),
                UnclesValidator = Substitute.For<IUnclesValidator>(),
                BlockProductionPolicy = Substitute.For<IBlockProductionPolicy>(),
                SyncProgressResolver = Substitute.For<ISyncProgressResolver>(),
                BetterPeerStrategy = Substitute.For<IBetterPeerStrategy>(),
                ReceiptMonitor = Substitute.For<IReceiptMonitor>(),
            };
            return api;
        }
    }
}

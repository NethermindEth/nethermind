// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.IO.Abstractions;
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
using Nethermind.Trie;
using NSubstitute;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Facade.Find;
using Nethermind.Core.Test.Builders;
using Nethermind.Init.Steps;
using Nethermind.Network.Config;
using Nethermind.Network.P2P.Analyzers;

namespace Nethermind.Runner.Test.Ethereum
{
    public static class Build
    {
        public static NethermindApi ContextWithoutContainer()
        {
            var api = new NethermindApi(Substitute.For<IConfigProvider>(), Substitute.For<IJsonSerializer>(), LimboLogs.Instance,
                new ChainSpec())
            {
                NodeKey = new ProtectedPrivateKey(TestItem.PrivateKeyA, Path.GetTempPath()),
                Enode = Substitute.For<IEnode>(),
                TxPool = Substitute.For<ITxPool>(),
                Wallet = Substitute.For<IWallet>(),
                BlockTree = Substitute.For<IBlockTree>(),
                DbProvider = TestMemDbProvider.Init(),
                SpecProvider = Substitute.For<ISpecProvider>(),
                EthereumEcdsa = Substitute.For<IEthereumEcdsa>(),
                MainBlockProcessor = Substitute.For<IBlockProcessor>(),
                ReceiptStorage = Substitute.For<IReceiptStorage>(),
                ReceiptFinder = Substitute.For<IReceiptFinder>(),
                BlockValidator = Substitute.For<IBlockValidator>(),
                RewardCalculatorSource = Substitute.For<IRewardCalculatorSource>(),
                TxPoolInfoProvider = Substitute.For<ITxPoolInfoProvider>(),
                BloomStorage = Substitute.For<IBloomStorage>(),
                Sealer = Substitute.For<ISealer>(),
                BlockchainProcessor = Substitute.For<IBlockchainProcessor>(),
                BlockProducer = Substitute.For<IBlockProducer>(),
                EngineSigner = Substitute.For<ISigner>(),
                FileSystem = Substitute.For<IFileSystem>(),
                FilterManager = Substitute.For<IFilterManager>(),
                FilterStore = Substitute.For<IFilterStore>(),
                GrpcServer = Substitute.For<IGrpcServer>(),
                HeaderValidator = Substitute.For<IHeaderValidator>(),
                IpResolver = Substitute.For<IIPResolver>(),
                KeyStore = Substitute.For<IKeyStore>(),
                LogFinder = Substitute.For<ILogFinder>(),
                MonitoringService = Substitute.For<IMonitoringService>(),
                SealValidator = Substitute.For<ISealValidator>(),
                WorldState = Substitute.For<IWorldState>(),
                StateReader = Substitute.For<IStateReader>(),
                TransactionProcessor = Substitute.For<ITransactionProcessor>(),
                TxSender = Substitute.For<ITxSender>(),
                BlockProcessingQueue = Substitute.For<IBlockProcessingQueue>(),
                EngineSignerStore = Substitute.For<ISignerStore>(),
                RpcModuleProvider = Substitute.For<IRpcModuleProvider>(),
                WebSocketsManager = Substitute.For<IWebSocketsManager>(),
                ChainLevelInfoRepository = Substitute.For<IChainLevelInfoRepository>(),
                TrieStore = Substitute.For<ITrieStore>(),
                BlockProducerEnvFactory = Substitute.For<IBlockProducerEnvFactory>(),
                TransactionComparerProvider = Substitute.For<ITransactionComparerProvider>(),
                GasPriceOracle = Substitute.For<IGasPriceOracle>(),
                HealthHintService = Substitute.For<IHealthHintService>(),
                TxValidator = new TxValidator(MainnetSpecProvider.Instance.ChainId),
                UnclesValidator = Substitute.For<IUnclesValidator>(),
                BlockProductionPolicy = Substitute.For<IBlockProductionPolicy>(),
                ReceiptMonitor = Substitute.For<IReceiptMonitor>(),
                BadBlocksStore = Substitute.For<IBlockStore>(),
                BackgroundTaskScheduler = Substitute.For<IBackgroundTaskScheduler>(),

                ApiWithNetworkServiceContainer = new ContainerBuilder()
                    .AddSingleton(Substitute.For<IDiscoveryApp>())
                    .AddSingleton(Substitute.For<ISyncModeSelector>())
                    .AddSingleton(Substitute.For<ISynchronizer>())
                    .AddSingleton(Substitute.For<ISyncPeerPool>())
                    .AddSingleton(Substitute.For<IPivot>())
                    .AddSingleton(Substitute.For<IPeerDifficultyRefreshPool>())
                    .AddSingleton(Substitute.For<IBetterPeerStrategy>())
                    .AddSingleton(Substitute.For<ISyncServer>())
                    .AddSingleton(Substitute.For<IRlpxHost>())
                    .AddSingleton(Substitute.For<ISessionMonitor>())
                    .AddSingleton(Substitute.For<IEthSyncingInfo>())
                    .AddSingleton(Substitute.For<IStaticNodesManager>())
                    .AddSingleton(Substitute.For<IProtocolsManager>())
                    .AddSingleton(Substitute.For<IPeerManager>())
                    .AddSingleton(Substitute.For<IPeerPool>())
                    .AddSingleton(Substitute.For<INodeStatsManager>())
                    .Build(),
            };

            api.WorldStateManager = new ReadOnlyWorldStateManager(api.DbProvider, Substitute.For<IReadOnlyTrieStore>(), LimboLogs.Instance);
            api.NodeStorageFactory = new NodeStorageFactory(INodeStorage.KeyScheme.HalfPath, LimboLogs.Instance);
            return api;
        }

        public static NethermindApi ContextWithMocks()
        {
            NethermindApi api = ContextWithoutContainer();
            api.ApiWithNetworkServiceContainer = new ContainerBuilder()
                .AddSingleton(Substitute.For<IDiscoveryApp>())
                .AddSingleton(Substitute.For<ISyncModeSelector>())
                .AddSingleton(Substitute.For<ISynchronizer>())
                .AddSingleton(Substitute.For<ISyncPeerPool>())
                .AddSingleton(Substitute.For<IPivot>())
                .AddSingleton(Substitute.For<IPeerDifficultyRefreshPool>())
                .AddSingleton(Substitute.For<IBetterPeerStrategy>())
                .AddSingleton(Substitute.For<ISyncServer>())
                .AddSingleton(Substitute.For<IRlpxHost>())
                .AddSingleton(Substitute.For<ISessionMonitor>())
                .AddSingleton(Substitute.For<IEthSyncingInfo>())
                .AddSingleton(Substitute.For<IStaticNodesManager>())
                .AddSingleton(Substitute.For<IProtocolsManager>())
                .AddSingleton(Substitute.For<IPeerManager>())
                .AddSingleton(Substitute.For<IPeerPool>())
                .Build();

            return api;
        }

        public static NethermindApi ContextWithMocksWithTestContainer()
        {
            NethermindApi api = ContextWithoutContainer();

            var builder = new ContainerBuilder();
            ((IApiWithNetwork)api).ConfigureContainerBuilderFromApiWithNetwork(builder);
            builder.RegisterModule(new NetworkModule(new NetworkConfig(), new SyncConfig()));
            api.ApiWithNetworkServiceContainer = builder.Build();

            return api;
        }
    }
}

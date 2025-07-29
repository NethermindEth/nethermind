// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
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
using Nethermind.Facade.Eth;
using Nethermind.Grpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.KeyStore;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Nethermind.Sockets;
using Nethermind.Specs;
using Nethermind.Trie;
using NSubstitute;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Era1;
using Nethermind.Facade.Find;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Runner.Test.Ethereum
{
    public static class Build
    {
        public static NethermindApi ContextWithMocks()
        {
            NethermindApi.Dependencies apiDependencies = new NethermindApi.Dependencies(
                Substitute.For<IConfigProvider>(),
                Substitute.For<IJsonSerializer>(),
                LimboLogs.Instance,
                new ChainSpec { Parameters = new ChainParameters(), },
                Substitute.For<ISpecProvider>(),
                [],
                Substitute.For<IProcessExitSource>(),
                new ContainerBuilder()
                    .AddSingleton(Substitute.For<IPoSSwitcher>())
                    .AddSingleton(Substitute.For<IAdminEraService>())
                    .AddSingleton(Substitute.For<ISyncModeSelector>())
                    .AddSingleton(Substitute.For<ISyncProgressResolver>())
                    .AddSingleton(Substitute.For<ISyncPointers>())
                    .AddSingleton(Substitute.For<ISynchronizer>())
                    .AddSingleton(Substitute.For<ISyncPeerPool>())
                    .AddSingleton(Substitute.For<IPeerDifficultyRefreshPool>())
                    .AddSingleton(Substitute.For<ISyncServer>())
                    .AddSingleton<ITxValidator>(new TxValidator(MainnetSpecProvider.Instance.ChainId))
                    .AddSingleton(Substitute.For<IBlockValidator>())
                    .AddSingleton(Substitute.For<IHeaderValidator>())
                    .AddSingleton(Substitute.For<IUnclesValidator>())
                    .AddSingleton(Substitute.For<IRpcModuleProvider>())
                    .AddSingleton(Substitute.For<IWorldStateManager>())
                    .AddSingleton(Substitute.For<IStateReader>())
                    .AddSingleton(Substitute.For<IEthSyncingInfo>())
                    .AddSingleton(Substitute.For<IPeerPool>())
                    .AddSingleton(Substitute.For<IDiscoveryApp>())
                    .AddSingleton(Substitute.For<IStaticNodesManager>())
                    .AddSingleton(Substitute.For<ITrustedNodesManager>())
                    .AddSingleton(Substitute.For<IPeerManager>())
                    .Build()
            );

            var api = new NethermindApi(apiDependencies);
            MockOutNethermindApi(api);
            api.NodeStorageFactory = new NodeStorageFactory(INodeStorage.KeyScheme.HalfPath, LimboLogs.Instance);
            return api;
        }

        public static void MockOutNethermindApi(NethermindApi api)
        {
            api.Enode = Substitute.For<IEnode>();
            api.TxPool = Substitute.For<ITxPool>();
            api.Wallet = Substitute.For<IWallet>();
            api.BlockTree = Substitute.For<IBlockTree>();
            api.DbProvider = TestMemDbProvider.Init();
            api.EthereumEcdsa = Substitute.For<IEthereumEcdsa>();
            api.ReceiptStorage = Substitute.For<IReceiptStorage>();
            api.ReceiptFinder = Substitute.For<IReceiptFinder>();
            api.RewardCalculatorSource = Substitute.For<IRewardCalculatorSource>();
            api.TxPoolInfoProvider = Substitute.For<ITxPoolInfoProvider>();
            api.BloomStorage = Substitute.For<IBloomStorage>();
            api.Sealer = Substitute.For<ISealer>();
            api.BlockProducer = Substitute.For<IBlockProducer>();
            api.EngineSigner = Substitute.For<ISigner>();
            api.FileSystem = Substitute.For<IFileSystem>();
            api.FilterManager = Substitute.For<IFilterManager>();
            api.FilterStore = Substitute.For<IFilterStore>();
            api.GrpcServer = Substitute.For<IGrpcServer>();
            api.IpResolver = Substitute.For<IIPResolver>();
            api.KeyStore = Substitute.For<IKeyStore>();
            api.LogFinder = Substitute.For<ILogFinder>();
            api.ProtocolsManager = Substitute.For<IProtocolsManager>();
            api.ProtocolValidator = Substitute.For<IProtocolValidator>();
            api.RlpxPeer = Substitute.For<IRlpxHost>();
            api.SealValidator = Substitute.For<ISealValidator>();
            api.SessionMonitor = Substitute.For<ISessionMonitor>();
            api.MainProcessingContext = Substitute.For<IMainProcessingContext>();
            api.TxSender = Substitute.For<ITxSender>();
            api.BlockProcessingQueue = Substitute.For<IBlockProcessingQueue>();
            api.EngineSignerStore = Substitute.For<ISignerStore>();
            api.WebSocketsManager = Substitute.For<IWebSocketsManager>();
            api.ChainLevelInfoRepository = Substitute.For<IChainLevelInfoRepository>();
            api.BlockProducerEnvFactory = Substitute.For<IBlockProducerEnvFactory>();
            api.TransactionComparerProvider = Substitute.For<ITransactionComparerProvider>();
            api.GasPriceOracle = Substitute.For<IGasPriceOracle>();
            api.HealthHintService = Substitute.For<IHealthHintService>();
            api.BlockProductionPolicy = Substitute.For<IBlockProductionPolicy>();
            api.ReceiptMonitor = Substitute.For<IReceiptMonitor>();
            api.BadBlocksStore = Substitute.For<IBadBlockStore>();

            api.NodeStorageFactory = new NodeStorageFactory(INodeStorage.KeyScheme.HalfPath, LimboLogs.Instance);
        }
    }
}

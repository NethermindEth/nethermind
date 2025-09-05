// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO.Abstractions;
using Autofac;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Scheduler;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Facade;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.KeyStore;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Nethermind.Consensus.Processing.CensorshipDetector;
using Nethermind.Facade.Find;
using Nethermind.History;

namespace Nethermind.Api
{
    public class NethermindApi(NethermindApi.Dependencies dependencies) : INethermindApi
    {

        // A simple class to prevent having to modify subclass of NethermindApi many time
        public record Dependencies(
            IConfigProvider ConfigProvider,
            IJsonSerializer JsonSerializer,
            ILogManager LogManager,
            ChainSpec ChainSpec,
            ISpecProvider SpecProvider,
            IReadOnlyList<INethermindPlugin> Plugins,
            IProcessExitSource ProcessExitSource,
            ILifetimeScope Context
        );

        private Dependencies _dependencies = dependencies;

        public IBlobTxStorage BlobTxStorage => Context.Resolve<IBlobTxStorage>();
        public CompositeBlockPreprocessorStep BlockPreprocessor { get; } = new();
        public IGenesisPostProcessor GenesisPostProcessor { get; set; } = new NullGenesisPostProcessor();
        public IBlockProcessingQueue BlockProcessingQueue => Context.Resolve<IBlockProcessingQueue>();
        public IBlockProducer? BlockProducer { get; set; }
        public IBlockProducerRunner BlockProducerRunner { get; set; } = new NoBlockProducerRunner();
        public IBlockTree BlockTree => Context.Resolve<IBlockTree>();
        public IBloomStorage? BloomStorage => Context.Resolve<IBloomStorage>();
        public IChainLevelInfoRepository? ChainLevelInfoRepository => Context.Resolve<IChainLevelInfoRepository>();
        public IConfigProvider ConfigProvider => _dependencies.ConfigProvider;
        public ICryptoRandom CryptoRandom => Context.Resolve<ICryptoRandom>();
        public IDbProvider DbProvider => Context.Resolve<IDbProvider>();
        public ISigner? EngineSigner { get; set; }
        public ISignerStore? EngineSignerStore { get; set; }
        public IEnode? Enode { get; set; }
        public IEthereumEcdsa EthereumEcdsa => Context.Resolve<IEthereumEcdsa>();
        public IFileSystem FileSystem { get; set; } = new FileSystem();
        public IEngineRequestsTracker EngineRequestsTracker => Context.Resolve<IEngineRequestsTracker>();

        public IManualBlockProductionTrigger ManualBlockProductionTrigger { get; set; } =
            new BuildBlocksWhenRequested();

        public IIPResolver IpResolver => Context.Resolve<IIPResolver>();
        public IJsonSerializer EthereumJsonSerializer => _dependencies.JsonSerializer;
        public IKeyStore? KeyStore { get; set; }
        public ILogManager LogManager => _dependencies.LogManager;
        public IMessageSerializationService MessageSerializationService => Context.Resolve<IMessageSerializationService>();
        public IGossipPolicy GossipPolicy { get; set; } = Policy.FullGossip;
        public IPeerManager? PeerManager => Context.Resolve<IPeerManager>();
        public IProtocolsManager? ProtocolsManager { get; set; }
        public IProtocolValidator? ProtocolValidator { get; set; }
        public IReceiptStorage? ReceiptStorage => Context.Resolve<IReceiptStorage>();
        public IReceiptFinder ReceiptFinder => Context.Resolve<IReceiptFinder>();
        public IRlpxHost RlpxPeer => Context.Resolve<IRlpxHost>();
        public IRpcModuleProvider? RpcModuleProvider => Context.Resolve<IRpcModuleProvider>();
        public IJsonRpcLocalStats JsonRpcLocalStats => Context.Resolve<IJsonRpcLocalStats>();
        public ISealer Sealer => Context.Resolve<ISealer>();
        public string SealEngineType => ChainSpec.SealEngineType;
        public ISealEngine SealEngine => Context.Resolve<ISealEngine>();

        public ISessionMonitor SessionMonitor => Context.Resolve<ISessionMonitor>();
        public ISpecProvider SpecProvider => _dependencies.SpecProvider;
        public ISyncModeSelector SyncModeSelector => Context.Resolve<ISyncModeSelector>()!;

        public ISyncPeerPool? SyncPeerPool => Context.Resolve<ISyncPeerPool>();
        public ISyncServer? SyncServer => Context.Resolve<ISyncServer>();
        public IWorldStateManager? WorldStateManager => Context.Resolve<IWorldStateManager>();
        public IStateReader? StateReader => Context.Resolve<IStateReader>();
        public IStaticNodesManager StaticNodesManager => Context.Resolve<IStaticNodesManager>();
        public ITrustedNodesManager TrustedNodesManager => Context.Resolve<ITrustedNodesManager>();
        public ITimestamper Timestamper { get; } = Core.Timestamper.Default;
        public ITimerFactory TimerFactory { get; } = Core.Timers.TimerFactory.Default;
        public IMainProcessingContext MainProcessingContext => Context.Resolve<IMainProcessingContext>();
        public ITxSender? TxSender { get; set; }
        public INonceManager? NonceManager { get; set; }
        public ITxPool? TxPool { get; set; }
        public TxValidator? TxValidator => Context.Resolve<TxValidator>();
        public ITxValidator? HeadTxValidator => Context.ResolveOptionalKeyed<ITxValidator>(ITxValidator.HeadTxValidatorKey);
        public IBlockFinalizationManager? FinalizationManager { get; set; }

        public IBlockProducerEnvFactory BlockProducerEnvFactory => Context.Resolve<IBlockProducerEnvFactory>();
        public IBlockProductionPolicy? BlockProductionPolicy { get; set; }
        public IBackgroundTaskScheduler BackgroundTaskScheduler { get; set; } = null!;
        public ICensorshipDetector CensorshipDetector { get; set; } = new NoopCensorshipDetector();
        public IWallet? Wallet { get; set; }
        public ITransactionComparerProvider? TransactionComparerProvider { get; set; }

        public IProtectedPrivateKey? NodeKey { get; set; }

        /// <summary>
        /// Key used for signing blocks. Original as its loaded on startup. This can later be changed via RPC in <see cref="Signer"/>.
        /// </summary>
        public IProtectedPrivateKey? OriginalSignerKey { get; set; }

        public ChainSpec ChainSpec => _dependencies.ChainSpec;
        public IDisposableStack DisposeStack => Context.Resolve<IDisposableStack>();
        public IReadOnlyList<INethermindPlugin> Plugins => _dependencies.Plugins;
        public IProcessExitSource ProcessExit => _dependencies.ProcessExitSource;
        public CompositeTxGossipPolicy TxGossipPolicy { get; } = new();
        public ILifetimeScope Context => _dependencies.Context;
    }
}

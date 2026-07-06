// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO.Abstractions;
using Autofac;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Scheduler;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Nethermind.Consensus.Processing.CensorshipDetector;

namespace Nethermind.Api
{
    public class NethermindApi(NethermindApi.Dependencies dependencies) : INethermindApi
    {

        // A simple class to prevent having to modify subclass of NethermindApi many times
        public record Dependencies(
            IConfigProvider ConfigProvider,
            EthereumJsonSerializer JsonSerializer,
            ILogManager LogManager,
            ChainSpec ChainSpec,
            ISpecProvider SpecProvider,
            IReadOnlyList<INethermindPlugin> Plugins,
            IProcessExitSource ProcessExitSource,
            ILifetimeScope Context
        );

        private Dependencies _dependencies = dependencies;

        public IBlobTxStorage BlobTxStorage => Context.Resolve<IBlobTxStorage>();
        public IBlockProducer? BlockProducer { get; set; }
        public IBlockProducerRunner BlockProducerRunner { get; set; } = new NoBlockProducerRunner();
        public IBlockTree BlockTree => Context.Resolve<IBlockTree>();
        public IConfigProvider ConfigProvider => _dependencies.ConfigProvider;
        public IDbProvider DbProvider => Context.Resolve<IDbProvider>();
        public ISigner EngineSigner => Context.Resolve<ISigner>();
        public IEthereumEcdsa EthereumEcdsa => Context.Resolve<IEthereumEcdsa>();
        public IFileSystem FileSystem => Context.Resolve<IFileSystem>();
        public IEngineRequestsTracker EngineRequestsTracker => Context.Resolve<IEngineRequestsTracker>();

        public IManualBlockProductionTrigger ManualBlockProductionTrigger { get; set; } =
            new BuildBlocksWhenRequested();

        public IIPResolver IpResolver => Context.Resolve<IIPResolver>();
        public EthereumJsonSerializer EthereumJsonSerializer => _dependencies.JsonSerializer;
        public ILogManager LogManager => _dependencies.LogManager;
        public IGossipPolicy GossipPolicy { get; set; } = Policy.FullGossip;
        public IProtocolsManager? ProtocolsManager => Context.Resolve<IProtocolsManager>();
        public IReceiptFinder ReceiptFinder => Context.Resolve<IReceiptFinder>();
        public IRpcModuleProvider? RpcModuleProvider => Context.Resolve<IRpcModuleProvider>();
        public string SealEngineType => ChainSpec.SealEngineType;

        public ISpecProvider SpecProvider => _dependencies.SpecProvider;
        public ISyncModeSelector SyncModeSelector => Context.Resolve<ISyncModeSelector>()!;

        public ISyncPeerPool? SyncPeerPool => Context.Resolve<ISyncPeerPool>();
        public ITimestamper Timestamper => Context.Resolve<ITimestamper>();
        public ITimerFactory TimerFactory => Context.Resolve<ITimerFactory>();
        public IMainProcessingContext MainProcessingContext => Context.Resolve<IMainProcessingContext>();
        public ITxSender? TxSender { get; set; }
        public INonceManager? NonceManager => Context.Resolve<INonceManager>();
        public ITxPool? TxPool { get; set; }
        public TxValidator? TxValidator => Context.Resolve<TxValidator>();
        public ITxValidator? HeadTxValidator => Context.ResolveOptionalKeyed<ITxValidator>(ITxValidator.HeadTxValidatorKey);

        public IBackgroundTaskScheduler BackgroundTaskScheduler => Context.Resolve<IBackgroundTaskScheduler>();
        public ICensorshipDetector CensorshipDetector { get; set; } = new NoopCensorshipDetector();
        public IWallet Wallet => Context.Resolve<IWallet>();
        public ITransactionComparerProvider? TransactionComparerProvider { get; set; }

        public ChainSpec ChainSpec => _dependencies.ChainSpec;
        public IDisposableStack DisposeStack => Context.Resolve<IDisposableStack>();
        public IReadOnlyList<INethermindPlugin> Plugins => _dependencies.Plugins;
        public IProcessExitSource ProcessExit => _dependencies.ProcessExitSource;

        public ILifetimeScope Context => _dependencies.Context;
    }
}

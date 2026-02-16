// SPDX-FileCopyrightText: 2026 Anil Chinchawale
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Crypto;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Scheduler;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using Nethermind.Xdc.P2P;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc;

/// <summary>
/// Autofac module for XDC Network support
/// Registers XDPoS v2 consensus components and eth/100 protocol
/// </summary>
public class XdcModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        // Register penalty handler for masternode penalties
        builder.RegisterType<PenaltyHandler>()
            .As<IPenaltyHandler>()
            .SingleInstance();

        // Register snapshot manager for XDPoS consensus state
        builder.Register(ctx =>
        {
            var blockTree = ctx.Resolve<IBlockTree>();
            var penaltyHandler = ctx.Resolve<IPenaltyHandler>();
            
            // Use in-memory DB for snapshots (TODO: register proper DB column in DbProvider)
            var snapshotDb = new MemDb("xdc_snapshot");
            
            return new SnapshotManager(snapshotDb, blockTree, penaltyHandler);
        }).As<ISnapshotManager>()
          .SingleInstance();

        // Register XDC-specific genesis builder that uses XdcBlockHeader
        builder.RegisterType<XdcGenesisBuilder>()
            .As<IGenesisBuilder>()
            .InstancePerLifetimeScope();

        // Register XDC block processor that preserves XdcBlockHeader during processing
        builder.RegisterType<XdcBlockProcessor>()
            .As<IBlockProcessor>()
            .InstancePerLifetimeScope();

        // Register XDC header validator (relaxes gas limit validation for XDPoS)
        builder.RegisterType<XdcHeaderValidator>()
            .As<Nethermind.Consensus.Validators.IHeaderValidator>()
            .SingleInstance();

        // Register XDC reward calculator for checkpoint block rewards
        builder.Register(ctx =>
        {
            var logManager = ctx.Resolve<ILogManager>();
            var blockTree = ctx.Resolve<IBlockTree>();
            var worldState = ctx.Resolve<IWorldState>();
            var ecdsa = ctx.Resolve<IEthereumEcdsa>();
            return new XdcRewardCalculator(logManager, blockTree, worldState, ecdsa);
        }).As<IRewardCalculator>()
          .As<IRewardCalculatorSource>()
          .InstancePerLifetimeScope();

        // Register XDC transaction processor for BlockSigners special handling
        builder.RegisterType<XdcTransactionProcessor>()
            .As<ITransactionProcessor>()
            .InstancePerLifetimeScope();

        // Register XDC consensus message processor
        builder.RegisterType<XdcConsensusMessageProcessor>()
            .As<IXdcConsensusMessageProcessor>()
            .SingleInstance();

        // Register custom eth protocol factory for XDC eth/100 using lambda to handle optional dependencies
        builder.Register(ctx =>
        {
            var serializer = ctx.Resolve<IMessageSerializationService>();
            var nodeStatsManager = ctx.Resolve<INodeStatsManager>();
            var syncServer = ctx.Resolve<ISyncServer>();
            var backgroundTaskScheduler = ctx.Resolve<IBackgroundTaskScheduler>();
            var txPool = ctx.Resolve<ITxPool>();
            var gossipPolicy = ctx.Resolve<IGossipPolicy>();
            var logManager = ctx.Resolve<ILogManager>();
            var txGossipPolicy = ctx.ResolveOptional<ITxGossipPolicy>();
            var consensusProcessor = ctx.ResolveOptional<IXdcConsensusMessageProcessor>();

            return new Eth100ProtocolFactory(
                serializer,
                nodeStatsManager,
                syncServer,
                backgroundTaskScheduler,
                txPool,
                gossipPolicy,
                logManager,
                txGossipPolicy,
                consensusProcessor);
        }).As<ICustomEthProtocolFactory>()
          .SingleInstance();
    }
}

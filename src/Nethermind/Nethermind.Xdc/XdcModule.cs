// SPDX-FileCopyrightText: 2026 Anil Chinchawale
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using Nethermind.Xdc.P2P;

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
            var dbProvider = ctx.Resolve<IDbProvider>();
            var blockTree = ctx.Resolve<IBlockTree>();
            var penaltyHandler = ctx.Resolve<IPenaltyHandler>();
            
            // Get or create the XDC snapshot database
            var snapshotDb = dbProvider.GetDb<IDb>("xdc_snapshot");
            
            return new SnapshotManager(snapshotDb, blockTree, penaltyHandler);
        }).As<ISnapshotManager>()
          .SingleInstance();

        // Register XDC-specific genesis builder that uses XdcBlockHeader
        builder.RegisterType<XdcGenesisBuilder>()
            .As<IGenesisBuilder>()
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

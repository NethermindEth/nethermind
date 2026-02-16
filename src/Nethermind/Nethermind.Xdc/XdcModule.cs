// SPDX-FileCopyrightText: 2026 Anil Chinchawale
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Xdc;

using Nethermind.Xdc.P2P.Eth100;

/// <summary>
/// Autofac module for XDC Network support
/// Registers XDPoS v2 consensus components and eth/100 protocol
/// </summary>
public class XdcModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        // Register XDC consensus message processor
        builder.RegisterType<XdcConsensusMessageProcessor>()
            .As<IXdcConsensusMessageProcessor>()
            .SingleInstance();

        // Register custom eth protocol factory for XDC eth/100
        builder.Register(ctx =>
        {
            return new Func<ISession, int, SyncPeerProtocolHandlerBase?>((session, version) =>
            {
                if (version != 100)
                    return null;

                var serializer = ctx.Resolve<IMessageSerializationService>();
                var stats = ctx.Resolve<INodeStatsManager>();
                var syncServer = ctx.Resolve<ISyncServer>();
                var backgroundTaskScheduler = ctx.Resolve<IBackgroundTaskScheduler>();
                var txPool = ctx.Resolve<ITxPool>();
                var gossipPolicy = ctx.Resolve<IGossipPolicy>();
                var logManager = ctx.Resolve<ILogManager>();
                var consensusProcessor = ctx.ResolveOptional<IXdcConsensusMessageProcessor>();
                var txGossipPolicy = ctx.ResolveOptional<ITxGossipPolicy>();

                return new Eth100ProtocolHandler(
                    session,
                    serializer,
                    stats,
                    syncServer,
                    backgroundTaskScheduler,
                    txPool,
                    gossipPolicy,
                    logManager,
                    consensusProcessor,
                    txGossipPolicy);
            });
        }).As<Func<ISession, int, SyncPeerProtocolHandlerBase?>>()
          .SingleInstance();
    }
}

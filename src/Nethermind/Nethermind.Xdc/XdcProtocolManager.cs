// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Network.P2P.Subprotocols.Eth.V64;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.Rlpx;
using Nethermind.State;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;
using Nethermind.Xdc.P2P;
using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Xdc;

internal class XdcProtocolManager : ProtocolsManager
{
    private readonly ITimeoutCertificateManager timeoutCertificateManager;
    private readonly IVotesManager votesManager;
    private readonly ISyncInfoManager syncInfoManager;

    public XdcProtocolManager(
    ITimeoutCertificateManager timeoutCertificateManager,
    IVotesManager votesManager,
    ISyncInfoManager syncInfoManager,
    ISyncPeerPool syncPeerPool,
    ISyncServer syncServer,
    IBackgroundTaskScheduler backgroundTaskScheduler,
    ITxPool txPool,
    IDiscoveryApp discoveryApp,
    IMessageSerializationService serializationService,
    IRlpxHost rlpxHost,
    INodeStatsManager nodeStatsManager,
    IProtocolValidator protocolValidator,
    [KeyFilter("peers")] INetworkStorage peerStorage,
    IForkInfo forkInfo,
    IGossipPolicy gossipPolicy,
    IWorldStateManager worldStateManager,
    ILogManager logManager,
    ITxPoolConfig txPoolConfdig,
    ISpecProvider specProvider,
    ITxGossipPolicy? transactionsGossipPolicy = null) : base(syncPeerPool, syncServer, backgroundTaskScheduler, txPool, discoveryApp, serializationService, rlpxHost, nodeStatsManager, protocolValidator, peerStorage, forkInfo, gossipPolicy, worldStateManager, logManager, txPoolConfdig, specProvider, transactionsGossipPolicy)
    {
        foreach (Capability item in DefaultCapabilities)
        {
            if (item.ProtocolCode == Protocol.NodeData)
                continue;
            RemoveSupportedCapability(item);
        }

        this.timeoutCertificateManager = timeoutCertificateManager;
        this.votesManager = votesManager;
        this.syncInfoManager = syncInfoManager;
    }

    protected override IDictionary<string, Func<ISession, int, IProtocolHandler>> GetProtocolFactories()
    {
        IDictionary<string, Func<ISession, int, IProtocolHandler>> protocolfac = base.GetProtocolFactories();
        protocolfac[Protocol.Eth] = (session, version) =>
        {
            Eth62ProtocolHandler ethHandler = version switch
            {
                62 => new Eth62ProtocolHandler(session, _serializer, _stats, _syncServer, _backgroundTaskScheduler, _txPool, _gossipPolicy, _logManager, _txGossipPolicy),
                63 => new Eth63ProtocolHandler(session, _serializer, _stats, _syncServer, _backgroundTaskScheduler, _txPool, _gossipPolicy, _logManager, _txGossipPolicy),
                64 => new Eth64ProtocolHandler(session, _serializer, _stats, _syncServer, _backgroundTaskScheduler, _txPool, _gossipPolicy, _forkInfo, _logManager, _txGossipPolicy),
                65 => new Eth65ProtocolHandler(session, _serializer, _stats, _syncServer, _backgroundTaskScheduler, _txPool, _gossipPolicy, _forkInfo, _logManager, _txGossipPolicy),
                _ => throw new NotSupportedException($"Eth protocol version {version} is not supported.")
            };

            InitSyncPeerProtocol(session, ethHandler);
            return ethHandler;
        };
        protocolfac["xdpos2"] = (session, version) =>
        {
            Xdpos2ProtocolHandler xdposHandler = version switch
            {
                100 => new Xdpos2ProtocolHandler(timeoutCertificateManager, votesManager, syncInfoManager, session, _serializer, _stats, _backgroundTaskScheduler, _logManager),
                _ => throw new NotSupportedException($"Xdpos2 protocol version {version} is not supported.")
            };
            InitSatelliteProtocol(session, xdposHandler);
            return xdposHandler;
        };

        return protocolfac;
    }
}

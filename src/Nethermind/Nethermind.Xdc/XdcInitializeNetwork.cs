// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Features.AttributeFilters;
using Google.Protobuf.WellKnownTypes;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

internal class XdcInitializeNetwork(
    INethermindApi api,
    INodeStatsManager nodeStatsManager,
    ISyncServer _,
    ISynchronizer synchronizer,
    ISyncPeerPool syncPeerPool,
    NodeSourceToDiscV4Feeder enrDiscoveryAppFeeder,
    IDiscoveryApp discoveryApp,
    Lazy<IPeerPool> peerPool,
    IForkInfo forkInfo,
    [KeyFilter("peers")] INetworkStorage peerStorage,
    INetworkConfig networkConfig,
    ISyncConfig syncConfig,
    IInitConfig initConfig,
    ILogManager logManager) : InitializeNetwork(api, nodeStatsManager, _, synchronizer, syncPeerPool, enrDiscoveryAppFeeder, discoveryApp, peerPool, forkInfo, peerStorage, networkConfig, syncConfig, initConfig, logManager)
{
    protected override IProtocolsManager CreateProtocolManager()
    {
        //We cannot call base since it will setup a spaghetti of event listeners we don't want

        XdcProtocolManager xdcProtocolManager = new XdcProtocolManager(
            _api.Context.Resolve<ITimeoutCertificateManager>(),
            _api.Context.Resolve<IVotesManager>(),
            _api.Context.Resolve<ISyncInfoManager>(),
            _syncPeerPool,
            _api.SyncServer,
            _api.BackgroundTaskScheduler,
            _api.TxPool!,
            _discoveryApp,
            _api.MessageSerializationService,
            _api.RlpxPeer,
            NodeStatsManager,
            _api.ProtocolValidator,
            _peerStorage,
            _forkInfo,
            _api.GossipPolicy,
            _api.WorldStateManager!,
            _api.LogManager,
            _api.Config<ITxPoolConfig>(),
            _api.SpecProvider!,
            _api.TxGossipPolicy
            );
        return xdcProtocolManager;
    }
}

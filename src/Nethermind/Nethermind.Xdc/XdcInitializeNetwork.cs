// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    public override IProtocolsManager ProtocolsManager()
    {
        base.CreateProtocolManager();

    }
}

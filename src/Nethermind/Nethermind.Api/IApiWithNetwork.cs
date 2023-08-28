// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus;
using Nethermind.Core.PubSub;
using Nethermind.Grpc;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.Monitoring;
using Nethermind.Network;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Peers;
using Nethermind.Sockets;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Synchronization.VerkleSync;

namespace Nethermind.Api
{
    public interface IApiWithNetwork : IApiWithBlockchain
    {
        (IApiWithNetwork GetFromApi, IApiWithNetwork SetInApi) ForNetwork => (this, this);

        IDisconnectsAnalyzer? DisconnectsAnalyzer { get; set; }
        IDiscoveryApp? DiscoveryApp { get; set; }
        IGrpcServer? GrpcServer { get; set; }
        IIPResolver? IpResolver { get; set; }
        IMessageSerializationService MessageSerializationService { get; }
        IGossipPolicy GossipPolicy { get; set; }
        IMonitoringService MonitoringService { get; set; }
        INodeStatsManager? NodeStatsManager { get; set; }
        IPeerManager? PeerManager { get; set; }
        IPeerPool? PeerPool { get; set; }
        IProtocolsManager? ProtocolsManager { get; set; }
        IProtocolValidator? ProtocolValidator { get; set; }
        IList<IPublisher> Publishers { get; }
        IRlpxHost? RlpxPeer { get; set; }
        IRpcModuleProvider? RpcModuleProvider { get; set; }
        IJsonRpcLocalStats? JsonRpcLocalStats { get; set; }
        ISessionMonitor? SessionMonitor { get; set; }
        IStaticNodesManager? StaticNodesManager { get; set; }
        ISynchronizer? Synchronizer { get; set; }
        IBlockDownloaderFactory? BlockDownloaderFactory { get; set; }
        IPivot? Pivot { get; set; }
        ISyncPeerPool? SyncPeerPool { get; set; }
        IPeerDifficultyRefreshPool? PeerDifficultyRefreshPool { get; set; }
        ISyncServer? SyncServer { get; set; }
        IWebSocketsManager WebSocketsManager { get; set; }
        ISubscriptionFactory? SubscriptionFactory { get; set; }
        ISnapProvider? SnapProvider { get; set; }
        IVerkleSyncProvider? VerkleProvider { get; set; }
    }
}

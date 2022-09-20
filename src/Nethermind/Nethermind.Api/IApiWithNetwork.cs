//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System.Collections.Generic;
using Nethermind.Consensus;
using Nethermind.Core.PubSub;
using Nethermind.Grpc;
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
        ISessionMonitor? SessionMonitor { get; set; }
        IStaticNodesManager? StaticNodesManager { get; set; }
        ISynchronizer? Synchronizer { get; set; }
        IBlockDownloaderFactory? BlockDownloaderFactory { get; set; }
        IPivot? Pivot { get; set; }
        ISyncPeerPool? SyncPeerPool { get; set; }
        IPeerDifficultyRefreshPool? PeerDifficultyRefreshPool { get; set; }
        ISyncServer? SyncServer { get; set; }
        IWebSocketsManager WebSocketsManager { get; set; }
        ISubscriptionFactory SubscriptionFactory { get; set; }
        ISnapProvider SnapProvider { get; set; }
    }
}

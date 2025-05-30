// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.PubSub;
using Nethermind.Grpc;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.Network;
using Nethermind.Network.Rlpx;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Peers;
using Nethermind.Sockets;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Api
{
    public interface IApiWithNetwork : IApiWithBlockchain
    {
        (IApiWithNetwork GetFromApi, IApiWithNetwork SetInApi) ForNetwork => (this, this);

        IGrpcServer? GrpcServer { get; set; }
        IIPResolver? IpResolver { get; set; }
        IMessageSerializationService MessageSerializationService { get; }
        IGossipPolicy GossipPolicy { get; set; }
        IPeerManager? PeerManager { get; }
        IPeerPool? PeerPool { get; }
        IProtocolsManager? ProtocolsManager { get; set; }
        IProtocolValidator? ProtocolValidator { get; set; }
        IList<IPublisher> Publishers { get; }
        IRlpxHost RlpxPeer { get; }

        [SkipServiceCollection]
        IRpcModuleProvider? RpcModuleProvider { get; }
        IJsonRpcLocalStats? JsonRpcLocalStats { get; set; }
        ISessionMonitor SessionMonitor { get; }
        IStaticNodesManager StaticNodesManager { get; }
        ITrustedNodesManager TrustedNodesManager { get; }
        ISyncModeSelector SyncModeSelector { get; }
        ISyncPeerPool? SyncPeerPool { get; }
        ISyncServer? SyncServer { get; }
        IWebSocketsManager WebSocketsManager { get; set; }
        ISubscriptionFactory? SubscriptionFactory { get; set; }
        IEngineRequestsTracker? EngineRequestsTracker { get; set; }
    }
}

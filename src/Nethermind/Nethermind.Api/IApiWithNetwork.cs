// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Network;
using Nethermind.Network.Rlpx;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Api
{
    public interface IApiWithNetwork : IApiWithBlockchain
    {
        (IApiWithNetwork GetFromApi, IApiWithNetwork SetInApi) ForNetwork => (this, this);

        IIPResolver IpResolver { get; }
        IMessageSerializationService MessageSerializationService { get; }
        IGossipPolicy GossipPolicy { get; set; }
        IPeerManager? PeerManager { get; }
        IProtocolsManager? ProtocolsManager { get; set; }
        IProtocolValidator? ProtocolValidator { get; set; }
        IRlpxHost RlpxPeer { get; }

        [SkipServiceCollection]
        IRpcModuleProvider? RpcModuleProvider { get; }
        IJsonRpcLocalStats JsonRpcLocalStats { get; }
        ISessionMonitor SessionMonitor { get; }
        IStaticNodesManager StaticNodesManager { get; }
        ITrustedNodesManager TrustedNodesManager { get; }
        ISyncModeSelector SyncModeSelector { get; }
        ISyncPeerPool? SyncPeerPool { get; }
        ISyncServer? SyncServer { get; }

        [SkipServiceCollection]
        IEngineRequestsTracker EngineRequestsTracker { get; }
    }
}

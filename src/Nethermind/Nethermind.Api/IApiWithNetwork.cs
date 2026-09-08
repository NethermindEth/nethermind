// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.JsonRpc.Modules;
using Nethermind.Network;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Api
{
    public interface IApiWithNetwork : IApiWithBlockchain
    {
        (IApiWithNetwork GetFromApi, IApiWithNetwork SetInApi) ForNetwork => (this, this);

        IIPResolver IpResolver { get; }
        IProtocolsManager? ProtocolsManager { get; }

        [SkipServiceCollection]
        IRpcModuleProvider? RpcModuleProvider { get; }
        ISyncModeSelector SyncModeSelector { get; }
        ISyncPeerPool? SyncPeerPool { get; }

        [SkipServiceCollection]
        IEngineRequestsTracker EngineRequestsTracker { get; }
    }
}

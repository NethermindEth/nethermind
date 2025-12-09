// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Stats;

namespace Nethermind.Xdc.P2P;

internal class XdcProtocolValidator : ProtocolValidator
{
    public XdcProtocolValidator(
        INodeStatsManager nodeStatsManager,
        IBlockTree blockTree,
        IForkInfo forkInfo,
        IPeerManager peerManager,
        INetworkConfig networkConfig,
        ILogManager logManager) : base(nodeStatsManager, blockTree, forkInfo, peerManager, networkConfig, logManager)
    {
    }

    protected override bool ValidateEthProtocol(ISession session, ProtocolInitializedEventArgs eventArgs)
    {
        SyncPeerProtocolInitializedEventArgs syncPeerArgs = (SyncPeerProtocolInitializedEventArgs)eventArgs;
        if (!ValidateNetworkId(session, syncPeerArgs.NetworkId))
            return false;

        if (!ValidateGenesisHash(session, syncPeerArgs))
            return false;
        return true;
    }
}

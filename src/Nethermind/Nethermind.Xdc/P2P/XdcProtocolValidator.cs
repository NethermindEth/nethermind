// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Stats;
using System;

namespace Nethermind.Xdc.P2P;

internal class XdcProtocolValidator(
    INodeStatsManager nodeStatsManager,
    IBlockTree blockTree,
    IForkInfo forkInfo,
    IPeerManager peerManager,
    INetworkConfig networkConfig,
    ILogManager logManager) : ProtocolValidator(nodeStatsManager, blockTree, forkInfo, peerManager, networkConfig, logManager)
{
    protected override bool MustValidateForkId { get; set; } = false;

    protected override bool ValidateEthProtocol(ISession session, ProtocolInitializedEventArgs eventArgs)
    {
        SyncPeerProtocolInitializedEventArgs syncPeerArgs = (SyncPeerProtocolInitializedEventArgs)eventArgs;
        Console.WriteLine(
            $"[XDC-DBG][ProtocolStatus] peer={session.Node.Host}:{session.Node.Port} " +
            $"networkId={syncPeerArgs.NetworkId} bestHash={syncPeerArgs.BestHash} genesis={syncPeerArgs.GenesisHash} " +
            $"td={syncPeerArgs.TotalDifficulty} localGenesis={_blockTree.Genesis.Hash} " +
            $"knownBest={_blockTree.FindHeader(syncPeerArgs.BestHash) is not null}");

        return base.ValidateEthProtocol(session, eventArgs);
    }
}

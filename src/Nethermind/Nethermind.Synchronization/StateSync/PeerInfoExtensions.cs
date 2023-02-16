// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Contract.P2P;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.StateSync;

public static class PeerInfoExtensions
{
    public static bool CanGetNodeData(this PeerInfo peerInfo)
    {
        return peerInfo.SyncPeer.ProtocolVersion < EthVersions.Eth67;
    }

    public static bool CanGetSnapData(this PeerInfo peerInfo)
    {
        return peerInfo.SyncPeer.TryGetSatelliteProtocol<object>(Protocol.Snap, out _);
    }
}

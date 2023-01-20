// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.StateSync;

public static class PeerInfoExtensions
{
    public static bool CanGetNodeData(this PeerInfo peerInfo)
    {
        return peerInfo.SyncPeer.ProtocolVersion < 67;
    }

    public static bool CanGetSnapData(this PeerInfo peerInfo)
    {
        return peerInfo.SyncPeer.TryGetSatelliteProtocol<object>("snap", out _);
    }
}

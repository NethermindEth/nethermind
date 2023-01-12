// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.StateSync;

public static class PeerInfoExtensions
{
    public static bool CanGetNodeData(this PeerInfo peerInfo)
    {
        if (!peerInfo.SyncPeer.Node.AgreedCapability.TryGetValue("eth", out int ethVersion))
        {
            return false;
        }

        if (ethVersion <= 66) return true;
        if (peerInfo.SyncPeer.Node.ClientType == NodeClientType.Nethermind) return true; // Nethermind will still answer to it
        return false;
    }

    public static bool CanGetSnapData(this PeerInfo peerInfo)
    {
        // Should we like, blacklist Nethermind here?
        return peerInfo.SyncPeer.TryGetSatelliteProtocol<object>("snap", out _);
    }
}

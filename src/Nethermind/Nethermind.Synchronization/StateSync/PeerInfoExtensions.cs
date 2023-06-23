// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Synchronization;
using Nethermind.Network.Contract.P2P;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.StateSync;

public static class PeerInfoExtensions
{
    public static bool CanGetNodeData(this PeerInfo peerInfo) => peerInfo.SyncPeer.CanGetNodeData();

    public static bool CanGetNodeData(this ISyncPeer peer) => peer.ProtocolVersion < EthVersions.Eth67;

    public static bool CanGetSnapData(this PeerInfo peerInfo) => peerInfo.SyncPeer.CanGetSnapData();

    public static bool CanGetSnapData(this ISyncPeer peer) => peer.TryGetSatelliteProtocol<object>(Protocol.Snap, out _);
}

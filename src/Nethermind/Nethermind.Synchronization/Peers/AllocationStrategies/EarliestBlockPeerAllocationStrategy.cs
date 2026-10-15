// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Synchronization.Peers.AllocationStrategies;

/// <summary>
/// Skips peers that announced (eth/69 <c>earliestBlock</c>) they no longer store <paramref name="blockNumber"/>,
/// so bodies and receipts requests for history go only to peers that can answer them.
/// </summary>
/// <remarks>
/// Peers that announce nothing report <c>0</c> and stay eligible.
/// </remarks>
public sealed class EarliestBlockPeerAllocationStrategy(IPeerAllocationStrategy strategy, ulong blockNumber) : FilterPeerAllocationStrategy(strategy)
{
    protected override bool Filter(PeerInfo peerInfo) => peerInfo.SyncPeer.EarliestBlock <= blockNumber;
}

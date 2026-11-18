// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Kademlia;

internal static class DiscoveryLiveness
{
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(30);

    internal static void Mark(Node node, long? timestamp = null)
        => node.LastDiscoveryProof = new Node.DiscoveryProof(node.DiscoveryAddress.Address, node.DiscoveryPort, timestamp ?? Stopwatch.GetTimestamp());

    internal static bool IsRecent(Node node)
    {
        if (node.LastDiscoveryProof is not { } proof || proof.Port != node.DiscoveryPort
            || !proof.Address.Equals(node.DiscoveryAddress.Address)) return false;
        TimeSpan elapsed = Stopwatch.GetElapsedTime(proof.Timestamp);
        return elapsed >= TimeSpan.Zero && elapsed <= MaximumAge;
    }

    internal static void CarryProof(Node incoming, Node existing)
    {
        if (incoming.DiscoveryAddress.Equals(existing.DiscoveryAddress) && IsRecent(existing)
            && existing.LastDiscoveryProof is { } proof
            && (!IsRecent(incoming) || proof.Timestamp > incoming.LastDiscoveryProof!.Timestamp))
        {
            incoming.LastDiscoveryProof = proof;
        }
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv4.Kademlia;

/// <summary>
/// Tracks the peer-candidate identities reserved by the discv4 adapter.
/// </summary>
/// <remarks>
/// The discv4 adapter reserves an identity before publishing it. Release is handled inside this assembly when
/// the admission gate or the node source discards the candidate, so the shared collaborator keeps release
/// independent of the adapter decorator chain. Consumers outside this assembly cannot release a reservation.
/// </remarks>
/// <param name="kademliaConfig">The Kademlia configuration that sizes the reservation cache.</param>
public sealed class PeerCandidateReservations(KademliaConfig<Node> kademliaConfig)
{
    private const int MinimumReservationCount = 64;

    private static readonly ConditionalWeakTable<KademliaConfig<Node>, PeerCandidateReservations> Instances = [];

    /// <summary>
    /// Gets the reservations shared by every component wired to the given Kademlia configuration.
    /// </summary>
    /// <remarks>
    /// The discv4 adapter reserves a candidate and the admission gate releases it, so both must observe the
    /// same instance. Keying on the configuration gives the adapter's compatibility constructor and the
    /// container registration one store without either having to resolve the other.
    /// </remarks>
    internal static PeerCandidateReservations For(KademliaConfig<Node> kademliaConfig)
        => Instances.GetValue(kademliaConfig, static config => new PeerCandidateReservations(config));

    private readonly RecentNodeFilter<Hash256> _reservations = new(
        RecentNodeFilter.GetLimit(kademliaConfig.KSize, ValueHash256KademliaDistance.Instance.MaxDistance, MinimumReservationCount));

    internal bool Contains(Hash256 nodeId) => _reservations.Contains(nodeId);

    internal bool TryReserve(Hash256 nodeId) => _reservations.TryReserve(nodeId);

    internal void Release(Hash256 nodeId) => _reservations.Release(nodeId);
}

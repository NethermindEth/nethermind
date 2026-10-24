// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.Libp2p.Protocols.Pubsub;

namespace Nethermind.BeaconChain.P2P.Gossip;

/// <summary>The gossipsub peer score of the eth2 topics: only invalid message deliveries (P4) count.</summary>
/// <remarks>
/// <para>
/// phase0 p2p-interface.md "The gossip domain: gossipsub" leaves the gossipsub v1.1 scoring parameters "under investigation", so
/// no profile is specified; this is policy. A peer the router charges for a <see cref="MessageValidity.Rejected"/> message scores
/// <see cref="InvalidMessageDeliveriesWeight"/> times the square of its decayed invalid count on that topic: one such message puts
/// it below the router's gossip and publish thresholds and prunes it from every mesh, and a second graylists it. The count
/// decays to a hundredth over two epochs, after which it is cleared.
/// </para>
/// <para>
/// Every other component weighs nothing. Time in mesh and first deliveries (P1, P2) would let earlier deliveries mask an invalid one,
/// and mesh delivery deficits (P3, P3b) would charge an honest peer of a sparse topic. Shared addresses (P6) are not recorded by the
/// library, and broken IWANT promises (P7) cost a peer that this node merely could not answer in time.
/// </para>
/// </remarks>
internal static class GossipScoring
{
    /// <summary>The score of one invalid delivery on a topic; the topic weight is one, so the gossip, publish and graylist thresholds compare against it directly.</summary>
    internal const double InvalidMessageDeliveriesWeight = -100;

    /// <summary>Gives each of <paramref name="topics"/> the P4-only parameters and turns off the score components that are not topic parameters.</summary>
    /// <remarks>Call before the router starts: the router caches a topic's parameters when it first scores a peer on it.</remarks>
    public static PubsubSettings Configure(PubsubSettings settings, IEnumerable<string> topics, BeaconChainSpec spec)
    {
        settings.BehaviorPenaltyWeight = 0;
        settings.IPColocationFactorWeight = 0;
        settings.AppSpecificWeight = 0;
        settings.DecayInterval = settings.HeartbeatInterval;
        TopicScoreParams invalidOnly = new()
        {
            TopicWeight = 1,
            TimeInMeshWeight = 0,
            FirstMessageDeliveriesWeight = 0,
            MeshMessageDeliveriesWeight = 0,
            MeshFailurePenaltyWeight = 0,
            InvalidMessageDeliveriesWeight = InvalidMessageDeliveriesWeight,
            InvalidMessageDeliveriesDecay = InvalidMessageDeliveriesDecay(spec, settings.DecayInterval),
        };
        foreach (string topic in topics)
        {
            settings.TopicScoreParams[topic] = invalidOnly;
        }

        return settings;
    }

    /// <summary>The per-interval factor that decays an invalid count to <c>0.01</c> over two epochs.</summary>
    internal static double InvalidMessageDeliveriesDecay(BeaconChainSpec spec, int decayIntervalMs) =>
        Math.Pow(0.01, decayIntervalMs / (2.0 * spec.SlotsPerEpoch * spec.SecondsPerSlot * 1000));
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Libp2p.Protocols.Pubsub;

namespace Nethermind.Network.Libp2p;

/// <summary>Turns off the gossipsub peer scores Nethermind.Libp2p applies by default.</summary>
/// <remarks>
/// The library scores every topic with generic parameters: a quiet mesh peer is pruned and graylisted within seconds,
/// and each IWANT promise a peer leaves unfulfilled costs it a squared behaviour penalty, so a few ids this node does
/// not recognise exclude an honest peer. Nothing here fills the score from validation verdicts yet.
/// </remarks>
public static class UnscoredGossip
{
    /// <summary>Gives <paramref name="topics"/> zero weight and zeroes the behaviour and shared-address penalties of <paramref name="settings"/>.</summary>
    /// <remarks>Call before the router reads the settings; the router reads the topic table without a lock.</remarks>
    public static PubsubSettings Configure(PubsubSettings settings, IEnumerable<string> topics)
    {
        settings.BehaviorPenaltyWeight = 0;
        // The library records no peer address in 1.0.0, so this only keeps a later address penalty off.
        settings.IPColocationFactorWeight = 0;
        TopicScoreParams unweighted = new() { TopicWeight = 0 };
        foreach (string topic in topics)
        {
            settings.TopicScoreParams[topic] = unweighted;
        }

        return settings;
    }
}

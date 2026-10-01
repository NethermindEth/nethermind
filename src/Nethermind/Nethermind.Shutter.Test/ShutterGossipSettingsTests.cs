// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.PubsubPeerDiscovery;
using NUnit.Framework;

namespace Nethermind.Shutter.Test;

public class ShutterGossipSettingsTests
{
    /// <summary>No delivery on the key or discovery topics, unanswered IWANT or shared address moves a keyper peer's score.</summary>
    /// <remarks>A delivery score would count a mesh peer of a quiet topic as under-delivering and prune it.</remarks>
    [Test]
    public void Key_and_discovery_gossip_is_unscored()
    {
        PubsubPeerDiscoverySettings discovery = new();
        PubsubSettings settings = ShutterP2P.CreatePubsubSettings(discovery);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(settings.TopicScoreParams["decryptionKeys"].TopicWeight, Is.Zero);
            Assert.That(discovery.Topics, Has.All.Matches<string>(topic => settings.TopicScoreParams[topic].TopicWeight == 0));
            Assert.That(settings.BehaviorPenaltyWeight, Is.Zero);
            Assert.That(settings.IPColocationFactorWeight, Is.Zero);
        }
    }
}

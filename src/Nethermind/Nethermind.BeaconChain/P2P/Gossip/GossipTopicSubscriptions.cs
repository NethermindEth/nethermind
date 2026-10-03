// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.BeaconChain.P2P.Gossip;

/// <summary>Hands out pubsub topics whose <see cref="ITopic.Unsubscribe"/> leaves the topic and stops its messages at the pubsub validator.</summary>
/// <remarks>altair p2p-interface.md "Transitioning the gossip": leave retired topics. The router still validates messages on a topic it left,
/// so retired messages are dropped before validation and caching, under the RPC handler monitor.</remarks>
internal sealed class GossipTopicSubscriptions(PubsubRouter router, Func<PeerId, Message, MessageValidity> verify)
{
    private readonly PubsubRouter _router = router;
    private readonly ConcurrentDictionary<string, byte> _retired = new();

    /// <summary>The pubsub validator: <see cref="MessageValidity.Throttled"/> for a retired topic, else the wrapped validator's verdict.</summary>
    /// <remarks>The router neither caches nor forwards a <see cref="MessageValidity.Throttled"/> message.</remarks>
    internal MessageValidity Verify(PeerId source, Message message) =>
        _retired.ContainsKey(message.Topic) ? MessageValidity.Throttled : verify(source, message);

    /// <summary>Gets and subscribes the topic, clearing any earlier retirement.</summary>
    internal ITopic GetTopic(string topicId)
    {
        lock (_router)
        {
            _retired.TryRemove(topicId, out _);
            return new Subscription(this, topicId, _router.GetTopic(topicId));
        }
    }

    private sealed class Subscription(GossipTopicSubscriptions owner, string topicId, ITopic topic) : ITopic
    {
        public bool IsSubscribed => topic.IsSubscribed;

        public event Action<PeerId, byte[]>? OnMessage
        {
            add => topic.OnMessage += value;
            remove => topic.OnMessage -= value;
        }

        public void Publish(byte[] value) => topic.Publish(value);

        public void Subscribe()
        {
            lock (owner._router)
            {
                owner._retired.TryRemove(topicId, out _);
                topic.Subscribe();
            }
        }

        public void Unsubscribe()
        {
            lock (owner._router)
            {
                // altair p2p-interface.md "Transitioning the gossip": pre-fork topics SHOULD be unsubscribed from.
                if (owner._retired.TryAdd(topicId, 0))
                {
                    topic.Unsubscribe();
                }
            }
        }
    }
}

// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Kademlia;

public class KademliaKademliaMessageReceiver<TKey, TNode>(
    IKademlia<TKey, TNode> kademlia,
    INodeHealthTracker<TNode> healthTracker
) : IKademliaMessageReceiver<TKey, TNode> where TNode : notnull
{
    public Task Ping(TNode sender, CancellationToken token)
    {
        healthTracker.OnIncomingMessageFrom(sender);
        return Task.CompletedTask;
    }

    public Task<TNode[]> FindNeighbours(TNode sender, TKey target, CancellationToken token)
    {
        healthTracker.OnIncomingMessageFrom(sender);
        return Task.FromResult(kademlia.GetKNeighbour(target, sender));
    }
}

// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Kademlia;

public class KademliaKademliaMessageReceiver<TNode>(IKademlia<TNode> kademlia): IKademliaMessageReceiver<TNode>
{
    public Task Ping(TNode sender, CancellationToken token)
    {
        kademlia.OnIncomingMessageFrom(sender);
        return Task.CompletedTask;
    }

    public Task<TNode[]> FindNeighbours(TNode sender, ValueHash256 hash, CancellationToken token)
    {
        kademlia.OnIncomingMessageFrom(sender);
        return Task.FromResult(kademlia.GetKNeighbour(hash, sender));
    }
}

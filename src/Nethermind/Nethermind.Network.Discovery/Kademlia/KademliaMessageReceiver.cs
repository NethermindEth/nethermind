// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Kademlia;

public class KademliaMessageReceiver<TNode, TContentKey, TContent>(
    IKademlia<TNode, TContentKey, TContent> kademlia,
    IKademlia<TNode, TContentKey, TContent>.IStore kademliaStore,
    IContentHashProvider<TContentKey> contentHashProvider
): IMessageReceiver<TNode, TContentKey, TContent>
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

    public Task<FindValueResponse<TNode, TContent>> FindValue(TNode sender, TContentKey contentKey, CancellationToken token)
    {
        kademlia.OnIncomingMessageFrom(sender);

        if (kademliaStore.TryGetValue(contentKey, out TContent? value))
        {
            return Task.FromResult(new FindValueResponse<TNode, TContent>(true, value!, Array.Empty<TNode>()));
        }

        return Task.FromResult(
            new FindValueResponse<TNode, TContent>(
                false,
                default,
                kademlia.GetKNeighbour(contentHashProvider.GetHash(contentKey), sender)
            ));
    }
}

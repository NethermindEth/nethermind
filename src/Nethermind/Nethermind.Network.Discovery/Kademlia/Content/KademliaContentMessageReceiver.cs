// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Kademlia.Content;

public class KademliaContentMessageReceiver<TNode, TContentKey, TContent>(
    IKademlia<TNode> kademlia,
    IContentHashProvider<TContentKey> contentHashProvider,
    IKademliaContentStore<TContentKey, TContent> kademliaKademliaContentStore) : IContentMessageReceiver<TNode, TContentKey, TContent>
{
    public Task<FindValueResponse<TNode, TContent>> FindValue(TNode sender, TContentKey contentKey, CancellationToken token)
    {
        kademlia.OnIncomingMessageFrom(sender);

        if (kademliaKademliaContentStore.TryGetValue(contentKey, out TContent? value))
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

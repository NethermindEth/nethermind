// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Kademlia.Content;

public class KademliaContentMessageReceiver<TNode, TContentKey, TContent>(
    IRoutingTable<TNode> kademlia,
    INodeHealthTracker<TNode> nodeHealthTracker,
    IContentHashProvider<TContentKey> contentHashProvider,
    IKademliaContentStore<TContentKey, TContent> kademliaKademliaContentStore) : IContentMessageReceiver<TNode, TContentKey, TContent> where TNode : notnull
{
    public Task<FindValueResponse<TNode, TContent>> FindValue(TNode sender, TContentKey contentKey, CancellationToken token)
    {
        nodeHealthTracker.OnIncomingMessageFrom(sender);

        if (kademliaKademliaContentStore.TryGetValue(contentKey, out TContent? value))
        {
            return Task.FromResult(new FindValueResponse<TNode, TContent>(true, value!, Array.Empty<TNode>()));
        }

        // TODO: Exclude sender.

        return Task.FromResult(
            new FindValueResponse<TNode, TContent>(
                false,
                default,
                kademlia.GetKNearestNeighbour(contentHashProvider.GetHash(contentKey), null, true)
            ));
    }
}

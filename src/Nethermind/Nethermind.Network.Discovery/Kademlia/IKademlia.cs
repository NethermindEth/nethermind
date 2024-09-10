// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Kademlia;

/// A generic kademlia implementation. As in the XOR distance table and routing algorithm.
/// Does not assume any transport, need to implement `IMessageReceiver` and `IMessageSender` for that.
/// The THash is for both node id and content id, which is probably not a good idea since the node id
/// probably need to store the ip also.
public interface IKademlia<TNode, TContentKey, TContent> : IMessageReceiver<TNode, TContentKey, TContent>
{
    /// Add node to the table.
    void AddOrRefresh(TNode node);

    /// Initiate a full traversal for finding the value
    Task<TContent?> LookupValue(TContentKey id, CancellationToken token);

    /// Lookup k nearest neighbour closest to the content id
    Task<TNode[]> LookupNodesClosest(ValueHash256 targetHash, int k, CancellationToken token);

    /// Start timers, refresh and such for maintenance of the table.
    Task Run(CancellationToken token);

    /// Just do the bootstrap sequence, which is to initiate a lookup on current node id.
    /// Also do a refresh on all bucket which is not part of joining strictly speaking.
    Task Bootstrap(CancellationToken token);

    /// Enumerate nodes within the table starting from the node nearest to target hash.
    /// No guarentee is made that the nodes are of exact order.
    IEnumerable<TNode> IterateNeighbour(ValueHash256 hash);

    event EventHandler<TNode> OnNodeAdded;

    public interface IStore
    {
        /// Used for serving transport.
        bool TryGetValue(TContentKey hash, out TContent? value);
    }
}

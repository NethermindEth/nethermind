// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Kademlia;

/// A generic kademlia implementation. As in the XOR distance table and routing algorithm.
/// Does not assume any transport, need to implement `IMessageReceiver` and `IMessageSender` for that.
/// The THash is for both node id and content id, which is probably not a good idea since the node id
/// probably need to store the ip also.
public interface IKademlia<TNode, TContentKey, TContent>: IMessageReceiver<TNode, TContentKey, TContent>
{
    /// Add node to the table.
    public void SeedNode(TNode node);

    /// Initiate a full traversal for finding the value
    public Task<TContent?> LookupValue(TContentKey id, CancellationToken token);

    /// Start timers, refresh and such for maintenance of the table.
    public Task Run(CancellationToken token);

    /// Just do the bootstrap sequence, which is to initiate a lookup on current node id.
    /// Also do a refresh on all bucket which is not part of joining strictly speaking.
    public Task Bootstrap(CancellationToken token);

    public interface IStore
    {
        /// Used for serving transport.
        bool TryGetValue(TContentKey hash, out TContent? value);
    }
}

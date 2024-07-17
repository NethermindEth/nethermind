// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Kademlia;

/// A generic kademlia implementation. As in the XOR distance table and routing algorithm.
/// Does not assume any transport, need to implement `IMessageReceiver` and `IMessageSender` for that.
public interface IKademlia<THash, TValue>: IMessageReceiver<THash, TValue>
{
    /// Add node to the table.
    public void SeedNode(THash node);

    /// Initiate a full traversal for finding the value
    public Task<TValue?> LookupValue(THash hash, CancellationToken token);

    /// Start timers, refresh and such for maintenance of the table.
    public Task Run(CancellationToken token);

    /// Start timers, refresh and such for maintenance of the table.
    public Task Bootstrap(CancellationToken token);

    public interface IStore
    {
        /// Used for serving transport.
        /// Note: The generic kinda breaks things
        bool TryGetValue(THash hash, out TValue value);
    }
}

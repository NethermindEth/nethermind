// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Kademlia;

/// <summary>
/// Main kademlia interface. High level code is expected to interface with this interface.
/// </summary>
/// <typeparam name="TNode"></typeparam>
public interface IKademlia<TNode>
{
    /// Add node to the table.
    void AddOrRefresh(TNode node);

    /// Remove from to the table.
    void Remove(TNode node);

    /// Lookup k nearest neighbour closest to the content id
    Task<TNode[]> LookupNodesClosest(ValueHash256 targetHash, CancellationToken token, int? k = null);

    /// Start timers, refresh and such for maintenance of the table.
    Task Run(CancellationToken token);

    /// Just do the bootstrap sequence, which is to initiate a lookup on current node id.
    /// Also do a refresh on all bucket which is not part of joining strictly speaking.
    Task Bootstrap(CancellationToken token);

    /// Enumerate nodes within the table starting from the node nearest to target hash.
    /// No guarentee is made that the nodes are of exact order.
    TNode[] GetKNeighbour(ValueHash256 hash, TNode? excluding);

    event EventHandler<TNode> OnNodeAdded;

    void OnIncomingMessageFrom(TNode sender);
    void OnRequestFailed(TNode receiver);
}

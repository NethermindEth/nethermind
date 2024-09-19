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

    /// <summary>
    /// Add node to the table.
    /// </summary>
    /// <param name="node"></param>
    void AddOrRefresh(TNode node);

    /// <summary>
    /// Remove from to the table.
    /// </summary>
    /// <param name="node"></param>
    void Remove(TNode node);

    /// <summary>
    /// Lookup k nearest neighbour closest to the target hash. This will traverse the network.
    /// </summary>
    /// <param name="targetHash"></param>
    /// <param name="token"></param>
    /// <param name="k"></param>
    Task<TNode[]> LookupNodesClosest(ValueHash256 targetHash, CancellationToken token, int? k = null);

    /// <summary>
    /// Start timers, refresh and such for maintenance of the table.
    /// </summary>
    /// <param name="token"></param>
    Task Run(CancellationToken token);

    /// <summary>
    /// Just do the bootstrap sequence, which is to initiate a lookup on current node id.
    /// Also do a refresh on all bucket which is not part of joining strictly speaking.
    /// </summary>
    /// <param name="token"></param>
    Task Bootstrap(CancellationToken token);

    /// <summary>
    /// Return the K nearest table entry from hash. This does not traverse the network. The returned array is not
    /// sorted. The routing table may return the exact same array for optimization purpose.
    /// </summary>
    /// <param name="hash"></param>
    /// <param name="excluding"></param>
    /// <param name="excludeSelf"></param>
    TNode[] GetKNeighbour(ValueHash256 hash, TNode? excluding = default, bool excludeSelf = false);

    /// <summary>
    /// Called when a TNode is added to the routing table.
    /// </summary>
    event EventHandler<TNode> OnNodeAdded;

    /// <summary>
    /// Call when an incoming message from a node is received. This is used by other algorithm for health checks.
    /// </summary>
    /// <param name="node"></param>
    void OnIncomingMessageFrom(TNode node);

    /// <summary>
    /// Call when a requset to a node failed. This is used by other algorithm for health checks.
    /// </summary>
    /// <param name="node"></param>
    void OnRequestFailed(TNode node);
}

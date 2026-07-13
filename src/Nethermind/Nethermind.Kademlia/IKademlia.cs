// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Kademlia;

/// <summary>
/// Provides routing-table maintenance and iterative Kademlia lookup over caller-defined key and node types.
/// </summary>
/// <typeparam name="TKey">The protocol-specific lookup key type.</typeparam>
/// <typeparam name="TNode">The protocol-specific node/contact type.</typeparam>
public interface IKademlia<TKey, TNode>
{
    /// <summary>
    /// Adds a node to the routing table or refreshes its position when it is already present.
    /// </summary>
    /// <param name="node">Node to add or refresh.</param>
    void AddOrRefresh(TNode node);

    /// <summary>
    /// Removes a node from the routing table.
    /// </summary>
    /// <param name="node">Node to remove.</param>
    void Remove(TNode node);

    /// <summary>
    /// Runs periodic bootstrap and routing-table refresh until cancelled.
    /// </summary>
    /// <param name="token">Cancellation token that stops the maintenance loop.</param>
    Task Run(CancellationToken token);

    /// <summary>
    /// Runs one bootstrap pass and refreshes stale non-empty buckets.
    /// </summary>
    /// <param name="token">Cancellation token for the bootstrap pass.</param>
    Task Bootstrap(CancellationToken token);

    /// <summary>
    /// Looks up nodes closest to <paramref name="key"/> by traversing the network.
    /// </summary>
    /// <param name="key">Protocol-specific lookup key.</param>
    /// <param name="token">Cancellation token for the lookup.</param>
    /// <param name="k">Optional result size. Defaults to <see cref="KademliaConfig{TNode}.KSize"/>.</param>
    Task<TNode[]> LookupNodesClosest(TKey key, CancellationToken token, int? k = null);

    /// <summary>
    /// Looks up nodes near <paramref name="key"/> and streams newly discovered candidates as soon as they are seen.
    /// </summary>
    /// <param name="key">Protocol-specific lookup key.</param>
    /// <param name="token">Cancellation token for the lookup.</param>
    /// <param name="maxResults">Optional maximum number of candidates to emit. Defaults to <see cref="KademliaConfig{TNode}.KSize"/>.</param>
    IAsyncEnumerable<TNode> LookupNodes(TKey key, CancellationToken token, int? maxResults = null);

    /// <summary>
    /// Returns the closest routing-table entries to <paramref name="target"/> without traversing the network.
    /// </summary>
    /// <param name="target">Protocol-specific lookup key.</param>
    /// <param name="excluding">Optional node to exclude from the result.</param>
    /// <param name="excludeSelf">Whether to exclude the local node from the result.</param>
    /// <remarks>The returned array is not sorted.</remarks>
    TNode[] GetKNeighbour(TKey target, TNode? excluding = default, bool excludeSelf = false);

    /// <summary>
    /// Return all table entries whose hash is at the requested log distance from the local node.
    /// </summary>
    /// <param name="distance">The XOR log distance from the local node.</param>
    TNode[] GetAllAtDistance(int distance);

    /// <summary>
    /// Raised when a node is added to the routing table.
    /// </summary>
    event EventHandler<TNode> OnNodeAdded;

    /// <summary>
    /// Raised when a node is removed from the routing table.
    /// </summary>
    event EventHandler<TNode> OnNodeRemoved;

    /// <summary>
    /// Iterates all nodes currently in the routing table without ordering guarantees.
    /// </summary>
    IEnumerable<TNode> IterateNodes();
}

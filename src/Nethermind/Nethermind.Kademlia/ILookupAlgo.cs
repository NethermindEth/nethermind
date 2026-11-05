// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


namespace Nethermind.Kademlia;

/// <summary>
/// Main find closest-k node within the network. See the kademlia paper, 2.3.
/// Since find value is basically the same also just with a shortcut, this allow changing the find neighbour op.
/// Find closest-k is also used to determine which node should store a particular value which is used by
/// store RPC (not implemented).
/// </summary>
public interface ILookupAlgo<TNode, TKadKey>
    where TNode : notnull
    where TKadKey : notnull
{
    /// <summary>
    /// The find neighbour operation here is configurable because the same algorithm is also used for finding
    /// value int the network, except that it would short circuit once the value was found.
    /// </summary>
    /// <param name="targetHash"></param>
    /// <param name="k"></param>
    /// <param name="findNeighbourOp"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task<TNode[]> Lookup(
        TKadKey targetHash,
        int k,
        Func<TNode, CancellationToken, Task<TNode[]?>> findNeighbourOp,
        CancellationToken token
    );

    /// <summary>
    /// Streams lookup candidates as soon as the lookup sees them.
    /// </summary>
    /// <param name="targetHash">The hash to search near.</param>
    /// <param name="maxResults">Maximum number of candidates to emit before stopping the lookup.</param>
    /// <param name="findNeighbourOp">Operation that fetches neighbours from a candidate node.</param>
    /// <param name="token">Cancellation token.</param>
    IAsyncEnumerable<TNode> LookupNodes(
        TKadKey targetHash,
        int maxResults,
        Func<TNode, CancellationToken, Task<TNode[]?>> findNeighbourOp,
        CancellationToken token
    );
}

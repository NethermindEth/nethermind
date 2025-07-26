// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Kademlia;

/// <summary>
/// Main find closest-k node within the network. See the kademlia paper, 2.3.
/// Since find value is basically the same also just with a shortcut, this allow changing the find neighbour op.
/// Find closest-k is also used to determine which node should store a particular value which is used by
/// store RPC (not implemented).
/// </summary>
public interface ILookupAlgo<THash, TNode> where THash : struct
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
        THash targetHash,
        int k,
        Func<TNode, CancellationToken, Task<TNode[]?>> findNeighbourOp,
        CancellationToken token
    );
}

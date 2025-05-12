// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Kademlia;

/// <summary>
/// Iterate nodes round a target. Returns `IAsyncEnumerable<TNode>` of nodes that it encounters along the way.
/// This mean that the returned value is not in order and may not be the closest to the target, but it try to
/// eventually get there. Additionally, the returned `TNode` may not be online unlike the standard algo which
/// requires it to be online.
/// </summary>
public interface IITeratorAlgo<TNode>
{
    /// <summary>
    /// The find neighbour operation here is configurable because the same algorithm is also used for finding
    /// value int the network, except that it would short circuit once the value was found.
    /// </summary>
    /// <param name="targetHash"></param>
    /// <param name="minResult"></param>
    /// <param name="findNeighbourOp"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    IAsyncEnumerable<TNode> Lookup(
        ValueHash256 target,
        int minResult,
        Func<TNode, CancellationToken, Task<TNode[]?>> findNeighbourOp,
        CancellationToken token
    );
}

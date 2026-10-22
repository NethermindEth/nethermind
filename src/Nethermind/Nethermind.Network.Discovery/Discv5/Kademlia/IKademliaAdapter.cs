// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv5.Kademlia;

/// <summary>
/// Adapts discv5 distance-based FINDNODE requests to the protocol-specific Kademlia routing table.
/// </summary>
public interface IKademliaAdapter : IKademliaMessageSender<PublicKey, Node>, IAsyncDisposable
{
    /// <summary>
    /// Gets known nodes at the requested log distances from the local node.
    /// </summary>
    /// <param name="distances">The requested XOR log distances.</param>
    /// <param name="excluding">An optional node to exclude from the result.</param>
    Node[] GetNodesAtDistances(IEnumerable<int> distances, Node? excluding = null);

    Task RunAsync(CancellationToken token);
}

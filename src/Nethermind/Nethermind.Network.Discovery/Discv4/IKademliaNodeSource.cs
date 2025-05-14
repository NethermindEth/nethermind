// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv4;

/// <summary>
/// Interface for discovering nodes in a Kademlia distributed hash table network.
/// </summary>
public interface IKademliaNodeSource
{
    /// <summary>
    /// Discovers nodes in the network.
    /// </summary>
    /// <param name="token">Cancellation token to stop the discovery process.</param>
    /// <returns>An asynchronous enumerable of discovered nodes.</returns>
    IAsyncEnumerable<Node> DiscoverNodes(CancellationToken token);
}

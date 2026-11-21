// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Crypto;

namespace Nethermind.Network;

public interface IStaticNodesManager : INodeSource
{
    IEnumerable<NetworkNode> Nodes { get; }
    Task InitAsync();
    /// <summary>Adds a node to the in-memory set and, when <paramref name="updateFile"/> is <see langword="true"/>, rewrites the nodes file so the node is persisted even if it was already known.</summary>
    /// <returns><see langword="true"/> when the node was newly added; <see langword="false"/> when it was already present.</returns>
    Task<bool> AddAsync(NetworkNode node, bool updateFile = true, CancellationToken cancellationToken = default);

    /// <summary>Removes a node from the in-memory set and, when <paramref name="updateFile"/> is <see langword="true"/>, rewrites the nodes file so any stale entry is scrubbed even if the node was already forgotten.</summary>
    /// <returns><see langword="true"/> when the node was removed; <see langword="false"/> when it was not present.</returns>
    Task<bool> RemoveAsync(NetworkNode node, bool updateFile = true, CancellationToken cancellationToken = default);
    bool IsStatic(NetworkNode node);

    /// <summary>Returns <see langword="true"/> when the managed set currently holds <paramref name="nodeId"/>, whatever endpoint it was added with.</summary>
    /// <remarks>The default scans <see cref="Nodes"/>, so implementations that keep an index should override it.</remarks>
    bool ContainsNode(PublicKey nodeId) => Nodes.Any(node => node.NodeId.Equals(nodeId));

    bool ContainsIp(IPAddress ip);
}

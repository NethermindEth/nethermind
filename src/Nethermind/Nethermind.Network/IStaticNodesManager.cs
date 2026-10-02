// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;

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

    bool ContainsIp(IPAddress ip);
}

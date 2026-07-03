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
    Task<bool> AddAsync(NetworkNode node, bool updateFile = true, CancellationToken cancellationToken = default);
    Task<bool> RemoveAsync(NetworkNode node, bool updateFile = true, CancellationToken cancellationToken = default);
    bool IsStatic(NetworkNode node);

    /// <summary>Returns <see langword="true"/> when a static node is reachable at the given IP (O(1) lookup).</summary>
    bool ContainsIp(IPAddress ip);
}

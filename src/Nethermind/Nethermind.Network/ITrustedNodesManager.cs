// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;

namespace Nethermind.Network;

public interface ITrustedNodesManager : INodeSource
{
    IEnumerable<NetworkNode> Nodes { get; }
    Task InitAsync();
    Task<bool> AddAsync(Enode enode, bool updateFile = true, CancellationToken cancellationToken = default);
    Task<bool> RemoveAsync(Enode enode, bool updateFile = true, CancellationToken cancellationToken = default);
    bool IsTrusted(Enode enode);

    /// <summary>Returns <see langword="true"/> when a trusted node is reachable at the given IP (O(1) lookup).</summary>
    bool ContainsIp(IPAddress ip);
}

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Config;

namespace Nethermind.Network;

public interface ITrustedNodesManager : INodeSource
{
    IEnumerable<NetworkNode> Nodes { get; }
    Task InitAsync();
    Task<bool> AddAsync(Enode enode, bool updateFile = true);
    Task<bool> RemoveAsync(Enode enode, bool updateFile = true);
    bool IsTrusted(Enode enode);
}

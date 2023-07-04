// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Config;

namespace Nethermind.Network
{
    public interface IStaticNodesManager : INodeSource
    {
        IEnumerable<NetworkNode> Nodes { get; }
        Task InitAsync();
        Task<bool> AddAsync(string enode, bool updateFile = true);
        Task<bool> RemoveAsync(string enode, bool updateFile = true);
        bool IsStatic(string enode);
    }
}

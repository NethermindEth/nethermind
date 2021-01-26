//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Network.StaticNodes;
using Nethermind.Stats.Model;

namespace Nethermind.Network
{
    public interface IStaticNodesManager
    {
        IEnumerable<NetworkNode> Nodes { get; }
        event EventHandler<NetworkNodeEventArgs> NodeAdded;
        event EventHandler<NetworkNodeEventArgs> NodeRemoved;
        Task InitAsync();
        Task<bool> AddAsync(string enode, bool updateFile = true);
        Task<bool> RemoveAsync(string enode, bool updateFile = true);
        bool IsStatic(string enode);
    }
}

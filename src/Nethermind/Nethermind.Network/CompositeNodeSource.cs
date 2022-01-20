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
// 

using System;
using System.Collections.Generic;
using Nethermind.Stats.Model;

namespace Nethermind.Network;

public class CompositeNodeSource : INodeSource
{
    private readonly INodeSource[] _nodeSources;

    public List<Node> LoadInitialList()
    {
        List<Node> all = new();
        foreach (INodeSource nodeSource in _nodeSources)
        {
            all.AddRange(nodeSource.LoadInitialList());
        }

        return all;
    }

    public event EventHandler<NodeEventArgs>? NodeAdded;
    
    public event EventHandler<NodeEventArgs>? NodeRemoved;

    public CompositeNodeSource(params INodeSource[] nodeSources)
    {
        _nodeSources = nodeSources;
        foreach (INodeSource nodeSource in nodeSources)
        {
            nodeSource.NodeAdded += PeerSourceOnNodeAdded;
            nodeSource.NodeRemoved += NodeSourceOnNodeRemoved;
        }
    }

    private void NodeSourceOnNodeRemoved(object? sender, NodeEventArgs e)
    {
        NodeRemoved?.Invoke(sender, e);
    }

    private void PeerSourceOnNodeAdded(object? sender, NodeEventArgs e)
    {
        NodeAdded?.Invoke(sender, e);
    }
}

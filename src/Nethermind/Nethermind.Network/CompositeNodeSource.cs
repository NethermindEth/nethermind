// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

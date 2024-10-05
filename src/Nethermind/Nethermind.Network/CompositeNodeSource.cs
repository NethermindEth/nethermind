// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Stats.Model;

namespace Nethermind.Network;

public class CompositeNodeSource : INodeSource
{
    private readonly INodeSource[] _nodeSources;

    public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Channel<Node> ch = Channel.CreateBounded<Node>(1);

        Task[] feedTasks = _nodeSources.Select(async (innerSource) =>
        {
            await foreach (Node node in innerSource.DiscoverNodes(cancellationToken))
            {
                await ch.Writer.WriteAsync(node, cancellationToken);
            }
        }).ToArray();

        try
        {
            await foreach (Node node in ch.Reader.ReadAllAsync(cancellationToken))
            {
                yield return node;
            }
        }
        finally
        {
            await Task.WhenAll(feedTasks);
        }
    }

    public event EventHandler<NodeEventArgs>? NodeRemoved;

    public CompositeNodeSource(params INodeSource[] nodeSources)
    {
        _nodeSources = nodeSources;
        foreach (INodeSource nodeSource in nodeSources)
        {
            nodeSource.NodeRemoved += NodeSourceOnNodeRemoved;
        }
    }

    private void NodeSourceOnNodeRemoved(object? sender, NodeEventArgs e)
    {
        NodeRemoved?.Invoke(sender, e);
    }
}

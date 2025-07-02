// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Test;

public class TestNodeSource : INodeSource
{
    private readonly Channel<Node> _channel = Channel.CreateUnbounded<Node>();
    public int BufferedNodeCount { get; set; }

    public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (Node node in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return node;
            BufferedNodeCount--;
        }
    }

    public void AddNode(Node node)
    {
        BufferedNodeCount++;
        _channel.Writer.TryWrite(node);
    }

#pragma warning disable CS0067
    public event EventHandler<NodeEventArgs>? NodeRemoved;
}

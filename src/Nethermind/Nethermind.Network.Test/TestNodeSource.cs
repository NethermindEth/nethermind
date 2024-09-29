// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Test;

public class TestNodeSource : INodeSource
{
    private Channel<Node> _channel = Channel.CreateUnbounded<Node>();

    public IAsyncEnumerable<Node> DiscoverNodes(CancellationToken cancellationToken)
    {
        return _channel.Reader.ReadAllAsync(cancellationToken);
    }

    public void AddNode(Node node)
    {
        _channel.Writer.TryWrite(node);
    }

#pragma warning disable CS0067
    public event EventHandler<NodeEventArgs>? NodeRemoved;
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Network;
using Nethermind.Stats.Model;

namespace Nethermind.Eez.Network;

internal sealed class EezPeerDiscovery(INodeSource inner, EezPeerNetwork overlay) : INodeSource
{
    public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (Node node in inner.DiscoverNodes(cancellationToken))
        {
            if (node.Enr is { } record && node.IsVerifiedEnr(record)) overlay.Discover(record);
            yield return node;
        }
    }

    public event EventHandler<NodeEventArgs> NodeRemoved
    {
        add => inner.NodeRemoved += value;
        remove => inner.NodeRemoved -= value;
    }
}

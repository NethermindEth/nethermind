// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Transport.Channels;
using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery;

public class NullDiscoveryApp : IDiscoveryApp
{
    public void Initialize(PublicKey masterPublicKey)
    {
    }

    public void InitializeChannel(IChannel channel)
    {
    }

    public Task StartAsync()
    {
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        return Task.CompletedTask;
    }

    public void AddNodeToDiscovery(Node node)
    {
    }

    public event EventHandler<NodeEventArgs>? NodeAdded
    {
        add { }
        remove { }
    }

    public IAsyncEnumerable<Node> DiscoverNodes(CancellationToken cancellationToken)
    {
        return AsyncEnumerable.Empty<Node>();
    }

    public event EventHandler<NodeEventArgs>? NodeRemoved
    {
        add { }
        remove { }
    }
}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery;

public class NullDiscoveryApp : IDiscoveryApp
{
    public void Initialize(PublicKey masterPublicKey)
    {
    }

    public void Start()
    {
    }

    public Task StopAsync()
    {
        return Task.CompletedTask;
    }

    public void AddNodeToDiscovery(Node node)
    {
    }

    public List<Node> LoadInitialList()
    {
        return new List<Node>();
    }

    public event EventHandler<NodeEventArgs>? NodeAdded
    {
        add { }
        remove { }
    }

    public event EventHandler<NodeEventArgs>? NodeRemoved
    {
        add { }
        remove { }
    }
}

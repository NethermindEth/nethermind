// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery;

public class NodeSourceToDiscV4Feeder : IDisposable
{
    private readonly INodeSource _nodeSource;
    private readonly IDiscoveryApp _discoveryApp;
    private readonly int _maxNodes;
    private int _addedNodes = 0;

    public NodeSourceToDiscV4Feeder(INodeSource nodeSource, IDiscoveryApp discoveryApp, int maxNodes)
    {
        nodeSource.NodeAdded += AddToDiscoveryApp;
        _nodeSource = nodeSource;
        _discoveryApp = discoveryApp;
        _maxNodes = maxNodes;
    }

    private void AddToDiscoveryApp(object? sender, NodeEventArgs e)
    {
        if (_addedNodes >= _maxNodes) return;
        _addedNodes++;
        _discoveryApp.AddNodeToDiscovery(e.Node);
    }

    public void Dispose()
    {
        _nodeSource.NodeAdded -= AddToDiscoveryApp;
    }
}

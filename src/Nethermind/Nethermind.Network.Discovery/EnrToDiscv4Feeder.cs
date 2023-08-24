// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery;

public class EnrToDiscv4Feeder: IDisposable
{
    private readonly INodeSource _nodeSource;
    private readonly IDiscoveryApp _discoveryApp;

    public EnrToDiscv4Feeder(INodeSource nodeSource, IDiscoveryApp discoveryApp)
    {
        nodeSource.NodeAdded += AddToDiscoveryApp;
        _nodeSource = nodeSource;
        _discoveryApp = discoveryApp;
    }

    private void AddToDiscoveryApp(object? sender, NodeEventArgs e)
    {
        _discoveryApp.AddNodeToDiscovery(e.Node);
    }

    public void Dispose()
    {
        _nodeSource.NodeAdded -= AddToDiscoveryApp;
    }
}

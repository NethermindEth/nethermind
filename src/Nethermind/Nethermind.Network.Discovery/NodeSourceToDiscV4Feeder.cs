// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery;

public class NodeSourceToDiscV4Feeder
{
    private readonly INodeSource _nodeSource;
    private readonly IDiscoveryApp _discoveryApp;
    private readonly int _maxNodes;

    public NodeSourceToDiscV4Feeder(INodeSource nodeSource, IDiscoveryApp discoveryApp, int maxNodes)
    {
        _nodeSource = nodeSource;
        _discoveryApp = discoveryApp;
        _maxNodes = maxNodes;
    }

    public async Task Run(CancellationToken token)
    {
        await foreach (Node node in _nodeSource.DiscoverNodes(token).Take(_maxNodes).WithCancellation(token))
        {
            _discoveryApp.AddNodeToDiscovery(node);
        }
    }
}

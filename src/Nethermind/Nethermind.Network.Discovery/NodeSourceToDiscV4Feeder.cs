// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Config;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery;

public class NodeSourceToDiscV4Feeder([KeyFilter(NodeSourceToDiscV4Feeder.SourceKey)] INodeSource nodeSource, IDiscoveryApp discoveryApp, IProcessExitSource exitSource, int maxNodes = 50)
{
    public const string SourceKey = "Enr";

    private readonly INodeSource _nodeSource = nodeSource;
    private readonly IDiscoveryApp _discoveryApp = discoveryApp;
    private readonly IProcessExitSource _exitSource = exitSource;
    private readonly int _maxNodes = maxNodes;

    public async Task Run()
    {
        CancellationToken token = _exitSource.Token;
        await foreach (Node node in _nodeSource.DiscoverNodes(token).Take(_maxNodes).WithCancellation(token))
        {
            _discoveryApp.AddNodeToDiscovery(node);
        }
    }
}

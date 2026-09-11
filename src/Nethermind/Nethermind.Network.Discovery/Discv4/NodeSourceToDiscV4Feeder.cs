// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv4;

public sealed class NodeSourceToDiscV4Feeder(
    [KeyFilter(NodeSourceToDiscV4Feeder.SourceKey)] INodeSource nodeSource,
    IDiscoveryApp discoveryApp,
    IProcessExitSource exitSource,
    NetworkListenerState listenerState,
    ILogManager logManager,
    int maxNodes = 50)
{
    public const string SourceKey = "Enr";

    private readonly INodeSource _nodeSource = nodeSource;
    private readonly IDiscoveryApp _discoveryApp = discoveryApp;
    private readonly IProcessExitSource _exitSource = exitSource;
    private readonly NetworkListenerState _listenerState = listenerState;
    private readonly ILogger _logger = logManager.GetClassLogger<NodeSourceToDiscV4Feeder>();
    private readonly int _maxNodes = maxNodes;

    public async Task Run()
    {
        if (_maxNodes <= 0)
        {
            return;
        }

        CancellationToken token = _exitSource.Token;
        if (_listenerState.DiscoveryAddress is not { } localIp)
        {
            if (_logger.IsDebug) _logger.Debug("Skipping the ENR discovery feeder because no discovery listener is bound.");
            return;
        }

        int addedNodes = 0;
        await foreach (Node node in _nodeSource.DiscoverNodes(token).WithCancellation(token))
        {
            if (!DiscoveryApp.TryCreateReachableNode(node, localIp, out Node? reachableNode))
            {
                continue;
            }

            _discoveryApp.AddNodeToDiscovery(reachableNode);
            if (++addedNodes >= _maxNodes)
            {
                return;
            }
        }
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Logging;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery;

public sealed class EnrForkIdFilteringNodeSource(
    INodeSource nodeSource,
    IEnrForkIdFilter enrForkIdFilter,
    ILogManager logManager) : INodeSource
{
    private readonly ILogger _logger = logManager.GetClassLogger<EnrForkIdFilteringNodeSource>();

    public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (Node node in nodeSource.DiscoverNodes(cancellationToken))
        {
            if (IsForkIdAcceptable(node))
            {
                yield return node;
            }
        }
    }

    public event EventHandler<NodeEventArgs>? NodeRemoved
    {
        add => nodeSource.NodeRemoved += value;
        remove => nodeSource.NodeRemoved -= value;
    }

    private bool IsForkIdAcceptable(Node node)
    {
        if (node.Enr is not { } record)
        {
            return true;
        }

        try
        {
            return enrForkIdFilter.IsAcceptable(record);
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"Unable to parse ENR for discovered node {node}: {e}");
            return false;
        }
    }
}

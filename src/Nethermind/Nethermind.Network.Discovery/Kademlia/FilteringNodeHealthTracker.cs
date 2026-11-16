// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Kademlia;

/// <summary>
/// Applies the configured recent-IP/subnet filter before a discovered node enters the routing table.
/// </summary>
/// <remarks>
/// Sits in front of <see cref="INodeHealthTracker{TNode}"/> so every admission path is covered: inbound
/// discovery messages, lookup results, and nodes fed in by external <see cref="INodeSource"/>s. Nodes already
/// in the routing table are refreshed rather than filtered, and self, bootnodes, static/trusted nodes, and
/// nodes loaded from the discovery database bypass the filter — the same exemptions the pre-Kademlia
/// DiscoveryManager applied.
/// </remarks>
internal sealed class FilteringNodeHealthTracker(
    INodeHealthTracker<Node> inner,
    DiscoveryNodeAdmission admission,
    ILogManager logManager) : INodeHealthTracker<Node>
{
    private readonly INodeHealthTracker<Node> _inner = inner;
    private readonly DiscoveryNodeAdmission _admission = admission;
    private readonly ILogger _logger = logManager.GetClassLogger<FilteringNodeHealthTracker>();

    public void OnIncomingMessageFrom(Node sender)
    {
        if (_admission.TryAdmit(sender))
        {
            _inner.OnIncomingMessageFrom(sender);
        }
        else if (_logger.IsTrace)
        {
            _logger.Trace($"Discovery node filtered by recent IP: {sender:hide}.");
        }
    }

    public void OnRequestFailed(Node node) => _inner.OnRequestFailed(node);
}

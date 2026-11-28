// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;

namespace Nethermind.Network;

/// <summary>Exemption lookup for managed static/trusted nodes and config static peers.</summary>
/// <remarks>
/// Config static peers (<c>Network.StaticPeers</c>) are matched by node id so a routing-table entry built
/// from discovery stays exempt even though its <c>IsStatic</c> flag is unset and refresh merges ENR state only.
/// </remarks>
public sealed class ManagedNodeExemptions : IDiscoveryNodeExemptions
{
    private readonly IStaticNodesManager _staticNodesManager;
    private readonly ITrustedNodesManager _trustedNodesManager;
    private readonly HashSet<PublicKey> _configStaticIds;

    public ManagedNodeExemptions(
        IStaticNodesManager staticNodesManager,
        ITrustedNodesManager trustedNodesManager,
        INetworkConfig networkConfig,
        ILogManager logManager)
    {
        _staticNodesManager = staticNodesManager;
        _trustedNodesManager = trustedNodesManager;
        ILogger logger = logManager.GetClassLogger<ManagedNodeExemptions>();
        // Mirror NodesLoader: only enode entries from config become static peers.
        _configStaticIds = NetworkNode.ParseNodes(networkConfig.StaticPeers, logger)
            .Where(static n => n.IsEnode)
            .Select(static n => n.NodeId)
            .ToHashSet();
    }

    /// <summary>Back-compat shim; config-file peers simply contribute no ids.</summary>
    [System.Obsolete("Use the (IStaticNodesManager, ITrustedNodesManager, INetworkConfig, ILogManager) overload.")]
    public ManagedNodeExemptions(
        IStaticNodesManager staticNodesManager,
        ITrustedNodesManager trustedNodesManager)
        : this(staticNodesManager, trustedNodesManager, new Nethermind.Network.Config.NetworkConfig(), LimboLogs.Instance)
    {
    }

    /// <inheritdoc/>
    public bool IsExempt(PublicKey nodeId)
        => _configStaticIds.Contains(nodeId)
            || _staticNodesManager.ContainsNode(nodeId)
            || _trustedNodesManager.ContainsNode(nodeId);
}

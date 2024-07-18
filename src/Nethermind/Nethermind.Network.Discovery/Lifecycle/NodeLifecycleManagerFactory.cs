// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.Enr;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Lifecycle;

public class NodeLifecycleManagerFactory : INodeLifecycleManagerFactory
{
    private readonly INodeTable _nodeTable;
    private readonly ILogger _logger;
    private readonly IDiscoveryConfig _discoveryConfig;
    private readonly ITimestamper _timestamper;
    private readonly IEvictionManager _evictionManager;
    private readonly INodeStatsManager _nodeStatsManager;
    private readonly NodeRecord _selfNodeRecord;

    public NodeLifecycleManagerFactory(INodeTable nodeTable,
        IEvictionManager evictionManager,
        INodeStatsManager nodeStatsManager,
        NodeRecord self,
        IDiscoveryConfig discoveryConfig,
        ITimestamper timestamper,
        ILogManager? logManager)
    {
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        _nodeTable = nodeTable ?? throw new ArgumentNullException(nameof(nodeTable));
        _discoveryConfig = discoveryConfig ?? throw new ArgumentNullException(nameof(discoveryConfig));
        _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
        _evictionManager = evictionManager ?? throw new ArgumentNullException(nameof(evictionManager));
        _nodeStatsManager = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
        _selfNodeRecord = self ?? throw new ArgumentNullException(nameof(self));
    }

    public IDiscoveryManager? DiscoveryManager { private get; set; }

    public INodeLifecycleManager CreateNodeLifecycleManager(Node node)
    {
        if (DiscoveryManager is null)
        {
            throw new Exception($"{nameof(DiscoveryManager)} has to be set");
        }

        return new NodeLifecycleManager(
            node,
            DiscoveryManager,
            _nodeTable,
            _evictionManager,
            _nodeStatsManager.GetOrAdd(node),
            _selfNodeRecord,
            _discoveryConfig,
            _timestamper,
            _logger);
    }
}

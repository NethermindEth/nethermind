// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.Enr;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Lifecycle;

public class NodeLifecycleManagerFactory(INodeTable nodeTable,
    IEvictionManager evictionManager,
    INodeStatsManager nodeStatsManager,
    NodeRecord self,
    IDiscoveryConfig discoveryConfig,
    ITimestamper timestamper,
    ILogManager? logManager) : INodeLifecycleManagerFactory
{
    private readonly INodeTable _nodeTable = nodeTable ?? throw new ArgumentNullException(nameof(nodeTable));
    private readonly ILogger _logger = logManager?.GetClassLogger<NodeLifecycleManagerFactory>() ?? throw new ArgumentNullException(nameof(logManager));
    private readonly IDiscoveryConfig _discoveryConfig = discoveryConfig ?? throw new ArgumentNullException(nameof(discoveryConfig));
    private readonly ITimestamper _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
    private readonly IEvictionManager _evictionManager = evictionManager ?? throw new ArgumentNullException(nameof(evictionManager));
    private readonly INodeStatsManager _nodeStatsManager = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
    private readonly NodeRecord _selfNodeRecord = self ?? throw new ArgumentNullException(nameof(self));

    public NodeLifecycleManagerFactory(INodeTable nodeTable,
        IEvictionManager evictionManager,
        INodeStatsManager nodeStatsManager,
        INodeRecordProvider nodeRecordProvider,
        IDiscoveryConfig discoveryConfig,
        ITimestamper timestamper,
        ILogManager? logManager)
        : this(nodeTable, evictionManager, nodeStatsManager, nodeRecordProvider.Current, discoveryConfig, timestamper, logManager)
    {
    }

    public IDiscoveryManager? DiscoveryManager { private get; set; }
    public NodeRecord SelfNodeRecord => _selfNodeRecord;

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

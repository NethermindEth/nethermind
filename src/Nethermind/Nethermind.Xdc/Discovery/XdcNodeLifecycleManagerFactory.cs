// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.Enr;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Xdc.Discovery;

public class XdcNodeLifecycleManagerFactory(
    INodeTable nodeTable,
    IEvictionManager evictionManager,
    INodeStatsManager nodeStatsManager,
    INodeRecordProvider nodeRecordProvider,
    IDiscoveryConfig discoveryConfig,
    ITimestamper timestamper,
    ILogManager? logManager) : INodeLifecycleManagerFactory
{
    private readonly INodeTable _nodeTable = nodeTable ?? throw new ArgumentNullException(nameof(nodeTable));
    private readonly ILogger _logger = logManager?.GetClassLogger<XdcNodeLifecycleManagerFactory>() ?? throw new ArgumentNullException(nameof(logManager));
    private readonly IDiscoveryConfig _discoveryConfig = discoveryConfig ?? throw new ArgumentNullException(nameof(discoveryConfig));
    private readonly ITimestamper _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
    private readonly IEvictionManager _evictionManager = evictionManager ?? throw new ArgumentNullException(nameof(evictionManager));
    private readonly INodeStatsManager _nodeStatsManager = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
    private readonly NodeRecord _selfNodeRecord = (nodeRecordProvider ?? throw new ArgumentNullException(nameof(nodeRecordProvider))).Current;

    public IDiscoveryManager? DiscoveryManager { private get; set; }
    public NodeRecord SelfNodeRecord => _selfNodeRecord;

    public INodeLifecycleManager CreateNodeLifecycleManager(Node node)
    {
        if (DiscoveryManager is null)
            throw new Exception($"{nameof(DiscoveryManager)} has to be set");

        return new XdcNodeLifecycleManager(
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

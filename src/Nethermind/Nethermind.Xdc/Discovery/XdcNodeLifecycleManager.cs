// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.Enr;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Xdc.Discovery;

/// <summary>XDC-aware lifecycle manager that suppresses ENR requests.</summary>
/// <remarks>
/// XDC does not support ENR (disc-v4 bytes 5/6). XDC remaps byte 5 to its own pingXDC type,
/// so a standard ENR request would be misread as a Ping by XDC peers.
/// </remarks>
public class XdcNodeLifecycleManager(
    Node node,
    IDiscoveryManager discoveryManager,
    INodeTable nodeTable,
    IEvictionManager evictionManager,
    INodeStats nodeStats,
    NodeRecord nodeRecord,
    IDiscoveryConfig discoveryConfig,
    ITimestamper timestamper,
    ILogger logger)
    : NodeLifecycleManager(node, discoveryManager, nodeTable, evictionManager, nodeStats, nodeRecord, discoveryConfig, timestamper, logger)
{
    protected override void SendEnrRequest() { }
}

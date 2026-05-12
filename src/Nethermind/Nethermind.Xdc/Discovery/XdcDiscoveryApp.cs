// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using DotNetty.Transport.Channels;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.RoutingTable;

namespace Nethermind.Xdc.Discovery;

// Parameters mirror DiscoveryApp so Autofac resolves [KeyFilter] attributes via WithAttributeFiltering().
public class XdcDiscoveryApp(
    [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey,
    INodesLocator nodesLocator,
    IDiscoveryManager? discoveryManager,
    INodeTable? nodeTable,
    IMessageSerializationService? msgSerializationService,
    ICryptoRandom? cryptoRandom,
    [KeyFilter(DbNames.DiscoveryNodes)] INetworkStorage? discoveryStorage,
    DiscoveryPersistenceManager discoveryPersistenceManager,
    IProcessExitSource processExitSource,
    INetworkConfig? networkConfig,
    IDiscoveryConfig? discoveryConfig,
    ITimestamper? timestamper,
    ILogManager? logManager,
    NodeFilter? inboundMessageFilter = null)
    : DiscoveryApp(nodeKey, nodesLocator, discoveryManager, nodeTable, msgSerializationService, cryptoRandom,
        discoveryStorage, discoveryPersistenceManager, processExitSource, networkConfig, discoveryConfig,
        timestamper, logManager, inboundMessageFilter)
{
    protected override NettyDiscoveryHandler CreateDiscoveryHandler(IChannel channel) =>
        new XdcNettyDiscoveryHandler(_discoveryManager, channel, _messageSerializationService, _timestamper, _logManager, _inboundMessageFilter);
}

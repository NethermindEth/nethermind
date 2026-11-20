// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Autofac;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Discv4.Kademlia;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;

namespace Nethermind.Network.Discovery.Test.Kademlia;

/// <summary>
/// Builds the discv4 <see cref="KademliaModule"/> graph with the dependencies that admission tests control overridden.
/// </summary>
/// <remarks>
/// The <see cref="KademliaConfig{TNode}"/> override is skipped when <c>useDiscoveryConfigFactory</c> is set, and the
/// <see cref="AdmissionGatedKademliaAdapter"/> decorator is added only when an inner adapter is supplied. That flag is
/// only for callers that resolve the factory-built config, so it may not be combined with a supplied config or a manual
/// clock. A supplied admission may not be combined with a manual clock either; the admission's own clock is used.
/// Callers that keep using the resolved decorator after this method returns must leave the container undisposed,
/// since disposal disposes the singleton decorator and forwards <c>DisposeAsync</c> to the inner substitute; callers
/// that finish inside a scope should dispose it as usual. Persisted nodes are restored with
/// <see cref="DiscoveryApp.RestorePersistedNode"/> and registered with the admission after the container is built,
/// mirroring the production startup path.
/// </remarks>
internal static class DiscoveryAdmissionTestContainer
{
    private static readonly TimeSpan AdmissionWindow = TimeSpan.FromMinutes(5);

    public static IContainer Create(
        bool enabled = true,
        bool subnetBucketing = true,
        string currentIp = "198.51.100.1",
        string? localIp = null,
        TimeProvider? timeProvider = null,
        NetworkNode[]? persisted = null,
        IDiscoveryConfig? discoveryConfig = null,
        KademliaConfig<Node>? kademliaConfig = null,
        Node? currentNode = null,
        bool useDiscoveryConfigFactory = false,
        IRoutingTable<Node, ValueHash256>? routingTable = null,
        INetworkStorage? storage = null,
        ILogManager? logManager = null,
        IStaticNodesManager? staticNodes = null,
        ITrustedNodesManager? trustedNodes = null,
        IEnumerable<NetworkNode>? configuredStaticNodes = null,
        DiscoveryNodeAdmission? admission = null,
        IKademliaAdapter? innerAdapter = null,
        PeerCandidateReservations? reservations = null,
        INodeHealthTracker<Node>? innerTracker = null)
    {
        if (useDiscoveryConfigFactory && (kademliaConfig is not null || timeProvider is not null))
        {
            throw new ArgumentException(
                "useDiscoveryConfigFactory cannot be combined with kademliaConfig or timeProvider.",
                nameof(useDiscoveryConfigFactory));
        }

        if (admission is not null && timeProvider is not null)
        {
            throw new ArgumentException(
                "admission cannot be combined with timeProvider.",
                nameof(timeProvider));
        }

        logManager ??= LimboLogs.Instance;
        storage ??= Substitute.For<INetworkStorage>();
        if (enabled)
        {
            storage.GetPersistedNodes().Returns(persisted ?? []);
        }

        currentNode ??= kademliaConfig?.CurrentNodeId
            ?? new Node(TestItem.PublicKeyD, "198.51.100.2", 30303, 30303);
        kademliaConfig ??= new KademliaConfig<Node> { CurrentNodeId = currentNode };

        NetworkConfig networkConfig = new()
        {
            ExternalIp = currentIp,
            LocalIp = "0.0.0.0",
            MaxActivePeers = 100,
            FilterDiscoveryNodesByRecentIp = enabled,
            FilterDiscoveryNodesBySameSubnet = subnetBucketing,
            StaticPeers = configuredStaticNodes is null
                ? null
                : string.Join(",", configuredStaticNodes.Select(staticNode => staticNode.ToString())),
        };

        ContainerBuilder builder = new();
        builder.AddModule(new KademliaModule(currentNode, kademliaConfig.BootNodes));
        builder.RegisterInstance(networkConfig).As<INetworkConfig>();
        builder.RegisterInstance(discoveryConfig ?? new DiscoveryConfig { MaxNodeLifecycleManagersCount = 100 }).As<IDiscoveryConfig>();
        builder.RegisterInstance(logManager).As<ILogManager>();
        builder.RegisterInstance(storage).Keyed<INetworkStorage>(DbNames.DiscoveryNodes);
        if (routingTable is not null)
        {
            builder.RegisterInstance(routingTable).As<IRoutingTable<Node, ValueHash256>>();
        }
        if (!useDiscoveryConfigFactory)
        {
            builder.RegisterInstance(kademliaConfig);
        }
        builder.RegisterInstance(Substitute.For<IKademliaMessageSender<PublicKey, Node>>())
            .As<IKademliaMessageSender<PublicKey, Node>>();
        builder.RegisterInstance(Substitute.For<INodeStatsManager>()).As<INodeStatsManager>();

        if (staticNodes is not null) builder.RegisterInstance(staticNodes);
        if (trustedNodes is not null) builder.RegisterInstance(trustedNodes);

        if (admission is not null)
        {
            builder.RegisterInstance(admission);
        }
        else if (timeProvider is not null)
        {
            // The production factory times the filter with the system clock; a manual clock needs its own filter.
            IPAddress? currentAddress = IPAddress.TryParse(currentIp, out IPAddress? parsed) ? parsed : null;
            NodeFilter filter = enabled
                ? new NodeFilter(400, !subnetBucketing, currentAddress, (long)AdmissionWindow.TotalMilliseconds, timeProvider)
                : NodeFilter.AcceptAll;
            DiscoveryNodeAdmission manualAdmission = new(
                filter,
                enabled,
                kademliaConfig,
                logManager,
                staticNodes,
                trustedNodes,
                configuredStaticNodes?.ToArray());
            // Mirror the production module: a promoted replacement and a successful refresh are re-checked by
            // the same admission.
            kademliaConfig.CanPromoteReplacement = manualAdmission.TryAdmitPromoted;
            kademliaConfig.CanRefreshNode = manualAdmission.TryAdmitRefresh;
            builder.RegisterInstance(manualAdmission);
        }

        if (innerTracker is not null)
        {
            builder.RegisterInstance(innerTracker).As<INodeHealthTracker<Node>>();
        }

        if (reservations is not null)
        {
            builder.RegisterInstance(reservations);
        }

        if (innerAdapter is not null)
        {
            builder.RegisterInstance(innerAdapter).As<IKademliaAdapter>();
            builder.AddDecorator<IKademliaAdapter, AdmissionGatedKademliaAdapter>();
        }

        IContainer container = builder.Build();
        if (admission is not null)
        {
            // A supplied instance replaces the module factory, so mirror the callback wiring it would have done.
            KademliaConfig<Node> resolvedConfig = container.Resolve<KademliaConfig<Node>>();
            resolvedConfig.CanPromoteReplacement = admission.TryAdmitPromoted;
            resolvedConfig.CanRefreshNode = admission.TryAdmitRefresh;
        }

        if (enabled && persisted is not null && IPAddress.TryParse(localIp ?? currentIp, out IPAddress? localAddress))
        {
            // Mirror DiscoveryApp: register the exemption at the persisted bonded address, which restore need not have chosen.
            DiscoveryNodeAdmission resolvedAdmission = container.Resolve<DiscoveryNodeAdmission>();
            foreach (NetworkNode persistedNode in persisted)
            {
                Node? node = DiscoveryApp.RestorePersistedNode(persistedNode, localAddress);
                if (node is not null)
                {
                    resolvedAdmission.AddPersistedNode(persistedNode);
                }
            }
        }

        return container;
    }
}

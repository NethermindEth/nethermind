// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Net;
using Autofac;
using DotNetty.Transport.Channels;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv4.Kademlia;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using LogLevel = DotNetty.Handlers.Logging.LogLevel;

namespace Nethermind.Network.Discovery.Discv4;

public class DiscoveryApp : KademliaDiscoveryApp
{
    private readonly IPAddress _localIp;
    private readonly DiscoveryPersistenceManager _persistenceManager;
    private readonly IKademliaAdapter _discv4Adapter;
    private readonly Func<IChannel, NettyDiscoveryHandler> _discoveryHandlerFactory;
    private readonly ILifetimeScope _discv4Services;

    private NettyDiscoveryHandler? _discoveryHandler;

    public DiscoveryApp(
        ILifetimeScope rootScope,
        IEnode enode,
        INetworkConfig networkConfig,
        IDiscoveryConfig discoveryConfig,
        IIPResolver ipResolver,
        IProcessExitSource processExitSource,
        ILogManager logManager,
        Action<ContainerBuilder>? configureDiscv4Services = null)
        : base("discv4", networkConfig, ipResolver, processExitSource, logManager.GetClassLogger<DiscoveryApp>())
    {
        _localIp = ipResolver.Resolve().GetAwaiter().GetResult().LocalIp;
        List<Node> bootNodes = CreateBootNodes(networkConfig.Bootnodes, Logger, _localIp);

        _discv4Services = rootScope.BeginLifetimeScope(
            (builder) =>
            {
                Node currentNode = new(enode.PublicKey, enode.HostIp.ToString(), networkConfig.P2PPort, networkConfig.DiscoveryPort, true);

                builder
                    .AddModule(new KademliaModule(currentNode, bootNodes))
                    .AddSingleton<DiscV4Services>();

                configureDiscv4Services?.Invoke(builder);
            });

        DiscV4Services services = _discv4Services.Resolve<DiscV4Services>();
        _persistenceManager = services.PersistenceManager;
        _discv4Adapter = services.Discv4Adapter;
        _discoveryHandlerFactory = services.NettyDiscoveryHandlerFactory;
        UseKademliaServices(services.NodeSource, services.Kademlia);
    }

    public override void AddNodeToDiscovery(Node node)
    {
        if (!TryCreateReachableNode(node, _localIp, out Node? reachableNode))
        {
            if (Logger.IsTrace) Logger.Trace($"Skipping discv4 node with no discovery endpoint reachable from the local listener: {node:s}.");
            return;
        }

        base.AddNodeToDiscovery(reachableNode);
    }

    internal static bool TryCreateReachableNode(
        Node node,
        IPAddress localIp,
        [NotNullWhen(true)] out Node? reachableNode)
    {
        if (node.Enr is { Signature: not null } record)
        {
            if (record.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1)?.Decompress().Equals(node.Id) != true)
            {
                reachableNode = null;
                return false;
            }

            if (node.HasDiscoveryEndpoint &&
                CompositeDiscoveryApp.SupportsAddress(localIp, node.DiscoveryAddress.Address))
            {
                reachableNode = node;
                return true;
            }

            return CompositeDiscoveryApp.TryCreateReachableDiscoveryNode(
                record,
                localIp,
                preferredEndpoint: null,
                out reachableNode);
        }

        if (node.HasDiscoveryEndpoint &&
            CompositeDiscoveryApp.SupportsAddress(localIp, node.DiscoveryAddress.Address))
        {
            reachableNode = node;
            return true;
        }

        reachableNode = null;
        return false;
    }

    internal static Node? RestorePersistedNode(NetworkNode networkNode, IPAddress localIp)
    {
        if (networkNode.IsEnr)
        {
            return CompositeDiscoveryApp.TryCreateReachableDiscoveryNode(
                networkNode.Enr,
                localIp,
                preferredEndpoint: null,
                out Node? node)
                ? node
                : null;
        }

        Node enode = new(networkNode);
        return enode.HasDiscoveryEndpoint &&
               CompositeDiscoveryApp.SupportsAddress(localIp, enode.DiscoveryAddress.Address)
            ? enode
            : null;
    }

    internal static List<Node> CreateBootNodes(NetworkNode[] configuredBootnodes, ILogger logger, IPAddress localIp)
    {
        List<Node> bootNodes = [];
        if (configuredBootnodes.Length == 0)
        {
            if (logger.IsWarn) logger.Warn("No bootnodes specified in configuration");
        }

        for (int i = 0; i < configuredBootnodes.Length; i++)
        {
            NetworkNode bootnode = configuredBootnodes[i];
            Node? node;
            if (bootnode.IsEnr)
            {
                if (!CompositeDiscoveryApp.TryCreateReachableDiscoveryNode(
                    bootnode.Enr,
                    localIp,
                    preferredEndpoint: null,
                    out node))
                {
                    if (logger.IsDebug) logger.Debug($"ENR bootnode ignored in discv4 because it has no usable discovery endpoint reachable from the local listener: {bootnode}");
                    continue;
                }
            }
            else
            {
                node = new Node(bootnode.NodeId, bootnode.Host, bootnode.Port, bootnode.DiscoveryPort);
                if (!CompositeDiscoveryApp.SupportsAddress(localIp, node.DiscoveryAddress.Address))
                {
                    if (logger.IsTrace) logger.Trace($"Skipping unreachable discv4 bootnode address family {node:s}.");
                    continue;
                }
            }

            bootNodes.Add(node);
        }

        return bootNodes;
    }

    /// <summary>
    /// Just a small class to make resolve easier
    /// </summary>
    private record DiscV4Services(
        IKademliaNodeSource NodeSource,
        DiscoveryPersistenceManager PersistenceManager,
        IKademliaAdapter Discv4Adapter,
        IKademlia<PublicKey, Node> Kademlia,
        Func<IChannel, NettyDiscoveryHandler> NettyDiscoveryHandlerFactory
    )
    {
    }

    protected override void DetachEventHandlers()
    {
        try
        {
            _discoveryHandler?.OnChannelActivated -= OnChannelActivated;
        }
        catch (Exception e)
        {
            Logger.Error("Error during discovery cleanup", e);
        }
    }

    protected virtual NettyDiscoveryHandler CreateDiscoveryHandler(IChannel channel)
    {
        NettyDiscoveryHandler discoveryHandler = _discoveryHandlerFactory(channel);
        _discv4Adapter.MsgSender = discoveryHandler;
        return discoveryHandler;
    }

    public override void InitializeChannel(IChannel channel)
    {
        _discoveryHandler = CreateDiscoveryHandler(channel);
        _discoveryHandler.OnChannelActivated += OnChannelActivated;

        channel.Pipeline
            .AddLast(new DotNetty.Handlers.Logging.LoggingHandler(LogLevel.INFO))
            .AddLast(_discoveryHandler);
    }

    protected override async Task RunDiscoveryAsync(CancellationToken cancellationToken)
    {
        //Step 1 - read nodes and stats from db
        await _persistenceManager.LoadPersistedNodes(
            cancellationToken,
            node => RestorePersistedNode(node, _localIp));

        Task persistenceTask = _persistenceManager.RunDiscoveryPersistenceCommit(cancellationToken);

        try
        {
            // Step 2 - run the standard kademlia routine
            await Kademlia.Run(cancellationToken);
        }
        finally
        {
            // Block until persistence is finished
            await persistenceTask;
        }
    }

    protected override ValueTask DisposeAsyncCore() => _discv4Services.DisposeAsync();
}

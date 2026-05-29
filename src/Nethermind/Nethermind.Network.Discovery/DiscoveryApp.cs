// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Features.AttributeFilters;
using DotNetty.Transport.Channels;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Kademlia;
using Nethermind.Stats.Model;
using LogLevel = DotNetty.Handlers.Logging.LogLevel;

namespace Nethermind.Network.Discovery;

public class DiscoveryApp : KademliaDiscoveryApp
{
    private readonly DiscoveryPersistenceManager _persistenceManager;
    private readonly IKademliaDiscv4Adapter _discv4Adapter;
    private readonly Func<IChannel, NettyDiscoveryHandler> _discoveryHandlerFactory;
    private readonly ILifetimeScope _discv4Services;

    private NettyDiscoveryHandler? _discoveryHandler;

    public DiscoveryApp(
        ILifetimeScope rootScope,
        [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey,
        INetworkConfig networkConfig,
        IDiscoveryConfig discoveryConfig,
        IProcessExitSource processExitSource,
        ILogManager logManager,
        Action<ContainerBuilder>? configureDiscv4Services = null)
        : base("discv4", networkConfig, processExitSource, logManager.GetClassLogger<DiscoveryApp>())
    {
        List<Node> bootNodes = [];
        NetworkNode[] bootnodes = networkConfig.Bootnodes;
        if (bootnodes.Length == 0)
        {
            if (Logger.IsWarn) Logger.Warn("No bootnodes specified in configuration");
        }

        for (int i = 0; i < bootnodes.Length; i++)
        {
            NetworkNode bootnode = bootnodes[i];
            if (!bootnode.IsEnode)
            {
                if (Logger.IsTrace) Logger.Trace($"Ignoring ENR in discovery V4: {bootnode}");
                continue;
            }

            if (bootnode.NodeId is null)
            {
                Logger.Warn($"Bootnode ignored because of missing node ID: {bootnode}");
                continue;
            }

            bootNodes.Add(new(bootnode.NodeId, bootnode.Host, bootnode.Port));
        }

        _discv4Services = rootScope.BeginLifetimeScope(
            (builder) =>
            {
                builder
                .AddModule(new DiscV4KademliaModule(nodeKey.PublicKey, bootNodes))
                .AddSingleton<DiscV4Services>();

                configureDiscv4Services?.Invoke(builder);
            });

        DiscV4Services services = _discv4Services.Resolve<DiscV4Services>();
        _persistenceManager = services.PersistenceManager;
        _discv4Adapter = services.Discv4Adapter;
        _discoveryHandlerFactory = services.NettyDiscoveryHandlerFactory;
        UseKademliaServices(services.NodeSource, services.Kademlia);
    }

    /// <summary>
    /// Just a small class to make resolve easier
    /// </summary>
    private record DiscV4Services(
        IKademliaNodeSource NodeSource,
        DiscoveryPersistenceManager PersistenceManager,
        IKademliaDiscv4Adapter Discv4Adapter,
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
        await _persistenceManager.LoadPersistedNodes(cancellationToken);

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

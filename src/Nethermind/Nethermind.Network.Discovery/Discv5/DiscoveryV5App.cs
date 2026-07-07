// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Autofac;
using Autofac.Features.AttributeFilters;
using Collections.Pooled;
using DotNetty.Transport.Channels;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Discv5.Kademlia;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using Discv5KademliaModule = Nethermind.Network.Discovery.Discv5.Kademlia.KademliaModule;

[assembly: InternalsVisibleTo("Nethermind.Network.Discovery.Test")]

namespace Nethermind.Network.Discovery.Discv5;

public sealed class DiscoveryV5App : KademliaDiscoveryApp
{
    private readonly bool _allowNonRoutableEnrs;
    private readonly DiscoveryPersistenceManager _persistenceManager;
    private readonly IKademliaAdapter _discv5Adapter;
    private readonly Func<NettyDiscoveryV5Handler> _discoveryHandlerFactory;
    private readonly ILifetimeScope _discv5Services;

    private NettyDiscoveryV5Handler? _discoveryHandler;

    public DiscoveryV5App(
        ILifetimeScope rootScope,
        [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey,
        IEnode enode,
        IIPResolver ipResolver,
        INetworkConfig networkConfig,
        IDiscoveryConfig discoveryConfig,
        IProcessExitSource processExitSource,
        ILogManager logManager,
        Action<ContainerBuilder>? configureDiscv5Services = null)
        : base("discv5", networkConfig, ipResolver, processExitSource, logManager.GetClassLogger<DiscoveryV5App>())
    {
        IPAddress externalIp = enode.HostIp;
        _allowNonRoutableEnrs = ShouldAcceptNonRoutableEnrs(externalIp);

        List<Node> bootNodes = CreateBootNodes(networkConfig, discoveryConfig);
        ITimestamper timestamper = rootScope.ResolveOptional<ITimestamper>() ?? Timestamper.Default;

        _discv5Services = rootScope.BeginLifetimeScope(builder =>
        {
            builder.RegisterInstance(discoveryConfig).As<IDiscoveryConfig>();
            builder.RegisterInstance(timestamper).As<ITimestamper>();
            Node currentNode = new(nodeKey.PublicKey, externalIp.ToString(), networkConfig.P2PPort, networkConfig.DiscoveryPort, true);
            builder
                .AddModule(new Discv5KademliaModule(currentNode, bootNodes))
                .AddSingleton<DiscV5Services>();

            configureDiscv5Services?.Invoke(builder);
        });

        DiscV5Services services = _discv5Services.Resolve<DiscV5Services>();
        _persistenceManager = services.PersistenceManager;
        _discv5Adapter = services.Discv5Adapter;
        _discoveryHandlerFactory = services.NettyDiscoveryHandlerFactory;
        UseKademliaServices(services.NodeSource, services.Kademlia);
    }

    /// <summary>
    /// Just a small class to make resolve easier
    /// </summary>
    private record DiscV5Services(
        IKademliaNodeSource NodeSource,
        DiscoveryPersistenceManager PersistenceManager,
        IKademliaAdapter Discv5Adapter,
        IKademlia<PublicKey, Node> Kademlia,
        Func<NettyDiscoveryV5Handler> NettyDiscoveryHandlerFactory
    )
    {
    }

    internal List<Node> CreateBootNodes(INetworkConfig networkConfig, IDiscoveryConfig discoveryConfig)
    {
        List<Node> bootNodes = [];
        using PooledSet<Hash256> seen = new(networkConfig.Bootnodes.Length);
        BootNodeStats configuredStats = new();
        BootNodeStats defaultStats = new();

        NetworkNode[] configuredBootnodes = networkConfig.Bootnodes;
        for (int i = 0; i < configuredBootnodes.Length; i++)
        {
            configuredStats.Record(AddBootNode(bootNodes, seen, configuredBootnodes[i]));
        }

        if (discoveryConfig.UseDefaultDiscv5Bootnodes)
        {
            string[] defaultBootnodes = GetDefaultBootnodes();
            for (int i = 0; i < defaultBootnodes.Length; i++)
            {
                defaultStats.Record(AddBootNode(bootNodes, seen, NodeRecord.FromEnrString(defaultBootnodes[i])));
            }
        }

        if (Logger.IsInfo)
        {
            Logger.Info($"Discv5 bootnodes accepted: {bootNodes.Count} ({configuredStats.Added}/{configuredStats.Total} configured, {defaultStats.Added}/{defaultStats.Total} default, duplicates: {configuredStats.Duplicates + defaultStats.Duplicates}, skipped: {configuredStats.Skipped + defaultStats.Skipped}, use default discv5 bootnodes: {discoveryConfig.UseDefaultDiscv5Bootnodes}).");
        }

        if (bootNodes.Count == 0 && Logger.IsWarn)
        {
            Logger.Warn("No discv5 bootnodes specified in configuration");
        }

        return bootNodes;
    }

    public override void AddNodeToDiscovery(Node node)
    {
        if (node.Enr is not { Signature: not null } record)
        {
            return;
        }

        try
        {
            if (!TryGetAcceptableNodeFromEnr(record, out Node? enrNode))
            {
                return;
            }

            if (!enrNode.IdHash.Equals(node.IdHash))
            {
                if (Logger.IsTrace) Logger.Trace($"Skipping discv5 discovery node {node:s} with mismatched ENR identity.");
                return;
            }

            Kademlia.AddOrRefresh(enrNode);
        }
        catch (Exception e)
        {
            if (Logger.IsTrace) Logger.Trace($"Unable to parse discv5 discovery ENR for {node}: {e}");
        }
    }

    private BootNodeAddResult AddBootNode(List<Node> bootNodes, ISet<Hash256> seen, NetworkNode networkNode)
    {
        if (networkNode.IsEnr)
        {
            return AddBootNode(bootNodes, seen, networkNode.Enr);
        }

        Node node = new(networkNode.NodeId, networkNode.Host, networkNode.Port, networkNode.DiscoveryPort);
        return AddBootNode(bootNodes, seen, node);
    }

    private BootNodeAddResult AddBootNode(List<Node> bootNodes, ISet<Hash256> seen, NodeRecord nodeRecord)
        => TryGetAcceptableNodeFromEnr(nodeRecord, out Node? node)
            ? AddBootNode(bootNodes, seen, node)
            : BootNodeAddResult.Skipped;

    private BootNodeAddResult AddBootNode(List<Node> bootNodes, ISet<Hash256> seen, Node node)
    {
        if (!seen.Add(node.IdHash))
        {
            if (Logger.IsTrace) Logger.Trace($"Skipping duplicate discv5 bootnode {node:s}.");
            return BootNodeAddResult.Duplicate;
        }

        node.IsBootnode = true;
        bootNodes.Add(node);
        if (Logger.IsDebug) Logger.Debug($"Accepted discv5 bootnode {node:s}, has ENR: {node.Enr is not null}.");
        return BootNodeAddResult.Added;
    }

    private static string[] GetDefaultBootnodes() =>
        JsonSerializer.Deserialize<string[]>(typeof(DiscoveryV5App).Assembly.GetManifestResourceStream("Nethermind.Network.Discovery.Discv5.discv5-bootnodes.json")!) ?? [];

    internal bool TryGetAcceptableNodeFromEnr(NodeRecord enr, [NotNullWhen(true)] out Node? node)
    {
        if (IsConsensusOnlyNodeRecord(enr))
        {
            node = null;
            if (Logger.IsTrace) Logger.Trace("Enr declined, consensus-only ENRs are not execution discovery peers.");
            return false;
        }

        if (Node.TryFromDiscoveryEnr(enr, out Node? enrNode) && IsDiscoveryAddressAcceptable(enrNode.DiscoveryAddress.Address, _allowNonRoutableEnrs))
        {
            node = enrNode;
            return true;
        }

        node = null;
        if (Logger.IsTrace) Logger.Trace("Enr declined, unable to extract a usable discv5 node endpoint.");
        return false;
    }

    internal static bool IsDiscoveryAddressAcceptable(IPAddress ipAddress, bool allowNonRoutable)
    {
        if (IPAddress.Any.Equals(ipAddress) || IPAddress.IPv6Any.Equals(ipAddress) || IPAddress.Broadcast.Equals(ipAddress))
        {
            return false;
        }

        if (ipAddress.IsMulticast)
        {
            return false;
        }

        if (ipAddress.IsSpecialUseAddress)
        {
            return false;
        }

        return allowNonRoutable || !ipAddress.IsLoopbackOrPrivateOrLinkLocal;
    }

    internal static bool IsDiscoveryAddressRoutable(IPAddress ipAddress)
        => IsDiscoveryAddressAcceptable(ipAddress, allowNonRoutable: false);

    internal static bool IsConsensusOnlyNodeRecord(NodeRecord enr)
        => enr.HasEntry(EnrContentKey.Eth2) && !enr.HasEntry(EnrContentKey.Eth);

    private static bool ShouldAcceptNonRoutableEnrs(IPAddress externalIp)
        => !IPAddress.Any.Equals(externalIp)
            && !IPAddress.None.Equals(externalIp)
            && externalIp.IsLoopbackOrPrivateOrLinkLocal;

    public override void InitializeChannel(IChannel channel)
    {
        _discoveryHandler = _discoveryHandlerFactory();
        _discoveryHandler.InitializeChannel(channel);
        _discoveryHandler.OnChannelActivated += OnChannelActivated;
        channel.Pipeline.AddLast(_discoveryHandler);
    }

    protected override void DetachEventHandlers()
    {
        try
        {
            if (_discoveryHandler is not null)
            {
                _discoveryHandler.OnChannelActivated -= OnChannelActivated;
            }
        }
        catch (Exception e)
        {
            Logger.Error("Error during discovery v5 cleanup", e);
        }
    }

    protected override async Task RunDiscoveryAsync(CancellationToken cancellationToken)
    {
        Task adapterTask = _discv5Adapter.RunAsync(cancellationToken);
        Task? persistenceTask = null;

        try
        {
            await _persistenceManager.LoadPersistedNodes(cancellationToken);

            persistenceTask = _persistenceManager.RunDiscoveryPersistenceCommit(cancellationToken);
            await Kademlia.Run(cancellationToken);
        }
        finally
        {
            try
            {
                if (persistenceTask is not null)
                {
                    await persistenceTask;
                }
            }
            finally
            {
                await adapterTask;
            }
        }
    }

    protected override async Task StopAsyncCore()
    {
        await _discv5Adapter.DisposeAsync();
        _discoveryHandler?.Close();
    }

    protected override ValueTask DisposeAsyncCore() => _discv5Services.DisposeAsync();

    private enum BootNodeAddResult
    {
        Added,
        Duplicate,
        Skipped
    }

    private struct BootNodeStats
    {
        public int Total { get; private set; }
        public int Added { get; private set; }
        public int Duplicates { get; private set; }
        public int Skipped { get; private set; }

        public void Record(BootNodeAddResult result)
        {
            Total++;
            switch (result)
            {
                case BootNodeAddResult.Added:
                    Added++;
                    break;
                case BootNodeAddResult.Duplicate:
                    Duplicates++;
                    break;
                case BootNodeAddResult.Skipped:
                    Skipped++;
                    break;
            }
        }
    }
}

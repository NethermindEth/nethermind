// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Autofac;
using Autofac.Features.AttributeFilters;
using DotNetty.Transport.Channels;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv5.Kademlia;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using Discv5KademliaModule = Nethermind.Network.Discovery.Discv5.Kademlia.KademliaModule;

[assembly: InternalsVisibleTo("Nethermind.Network.Discovery.Test")]

namespace Nethermind.Network.Discovery.Discv5;

public sealed class DiscoveryV5App : KademliaDiscoveryApp
{
    internal const int MaxPendingEnrsPerWalk = 4_096;
    internal const int MaxTrackedEnrsPerWalk = MaxPendingEnrsPerWalk * 2;

    private readonly IDb _discoveryDb;
    private readonly IDb _legacyDiscoveryDb;
    private readonly bool _allowNonRoutableEnrs;
    private readonly IKademliaAdapter _adapter;
    private readonly Func<NettyDiscoveryV5Handler> _discoveryHandlerFactory;
    private readonly ILifetimeScope _services;

    private NettyDiscoveryV5Handler? _discoveryHandler;

    public DiscoveryV5App(
        ILifetimeScope rootScope,
        [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey,
        IIPResolver ipResolver,
        INetworkConfig networkConfig,
        IDiscoveryConfig discoveryConfig,
        [KeyFilter(DbNames.DiscoveryV5Nodes)] IDb discoveryDb,
        [KeyFilter(DbNames.DiscoveryNodes)] IDb legacyDiscoveryDb,
        IProcessExitSource processExitSource,
        ILogManager logManager,
        Action<ContainerBuilder>? configureServices = null)
        : base("discv5", networkConfig, processExitSource, logManager.GetClassLogger<DiscoveryV5App>())
    {
        _discoveryDb = discoveryDb;
        _legacyDiscoveryDb = legacyDiscoveryDb;
        _allowNonRoutableEnrs = ShouldAcceptNonRoutableEnrs(ipResolver.ExternalIp);

        List<Node> bootNodes = CreateBootNodes(networkConfig, discoveryConfig);
        ITimestamper timestamper = rootScope.ResolveOptional<ITimestamper>() ?? Timestamper.Default;

        _services = rootScope.BeginLifetimeScope(builder =>
        {
            builder.RegisterInstance(discoveryConfig).As<IDiscoveryConfig>();
            builder.RegisterInstance(timestamper).As<ITimestamper>();
            builder
                .AddModule(new Discv5KademliaModule(nodeKey.PublicKey, bootNodes))
                .AddSingleton<Services>();

            configureServices?.Invoke(builder);
        });

        Services services = _services.Resolve<Services>();
        _adapter = services.Adapter;
        _discoveryHandlerFactory = services.NettyDiscoveryHandlerFactory;
        UseKademliaServices(services.NodeSource, services.Kademlia);
    }

    private record Services(
        IKademliaNodeSource NodeSource,
        IKademliaAdapter Adapter,
        IKademlia<PublicKey, Node> Kademlia,
        Func<NettyDiscoveryV5Handler> NettyDiscoveryHandlerFactory
    )
    {
    }

    internal List<Node> CreateBootNodes(INetworkConfig networkConfig, IDiscoveryConfig discoveryConfig)
    {
        List<Node> bootNodes = [];
        HashSet<Hash256> seen = [];
        BootNodeStats configuredStats = new();
        BootNodeStats defaultStats = new();
        BootNodeStats storedStats = new();

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

        List<NodeRecord> storedEnrs = LoadStoredEnrs();
        for (int i = 0; i < storedEnrs.Count; i++)
        {
            storedStats.Record(AddBootNode(bootNodes, seen, storedEnrs[i]));
        }

        if (Logger.IsInfo)
        {
            Logger.Info($"Discv5 bootnodes accepted: {bootNodes.Count} ({configuredStats.Added}/{configuredStats.Total} configured, {defaultStats.Added}/{defaultStats.Total} default, {storedStats.Added}/{storedStats.Total} stored, duplicates: {configuredStats.Duplicates + defaultStats.Duplicates + storedStats.Duplicates}, skipped: {configuredStats.Skipped + defaultStats.Skipped + storedStats.Skipped}, use default discv5 bootnodes: {discoveryConfig.UseDefaultDiscv5Bootnodes}).");
        }

        if (bootNodes.Count == 0 && Logger.IsWarn)
        {
            Logger.Warn("No discv5 bootnodes specified in configuration");
        }

        return bootNodes;
    }

    public override void AddNodeToDiscovery(Node node)
    {
        if (string.IsNullOrEmpty(node.Enr))
        {
            return;
        }

        try
        {
            NodeRecord record = NodeRecord.FromEnrString(node.Enr);
            if (!TryGetNodeFromEnr(record, out Node? enrNode))
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

    private BootNodeAddResult AddBootNode(List<Node> bootNodes, HashSet<Hash256> seen, NetworkNode networkNode)
    {
        if (networkNode.IsEnr)
        {
            return AddBootNode(bootNodes, seen, networkNode.Enr);
        }

        Node node = new(networkNode.NodeId, networkNode.Host, networkNode.Port);
        return AddBootNode(bootNodes, seen, node);
    }

    private BootNodeAddResult AddBootNode(List<Node> bootNodes, HashSet<Hash256> seen, NodeRecord nodeRecord)
    {
        if (TryGetNodeFromEnr(nodeRecord, out Node? node))
        {
            return AddBootNode(bootNodes, seen, node);
        }

        return BootNodeAddResult.Skipped;
    }

    private BootNodeAddResult AddBootNode(List<Node> bootNodes, HashSet<Hash256> seen, Node node)
    {
        if (!seen.Add(node.IdHash))
        {
            if (Logger.IsTrace) Logger.Trace($"Skipping duplicate discv5 bootnode {node:s}.");
            return BootNodeAddResult.Duplicate;
        }

        node.IsBootnode = true;
        bootNodes.Add(node);
        if (Logger.IsDebug) Logger.Debug($"Accepted discv5 bootnode {node:s}, has ENR: {!string.IsNullOrEmpty(node.Enr)}.");
        return BootNodeAddResult.Added;
    }

    private static string[] GetDefaultBootnodes() =>
        JsonSerializer.Deserialize<string[]>(typeof(DiscoveryV5App).Assembly.GetManifestResourceStream("Nethermind.Network.Discovery.Discv5.discv5-bootnodes.json")!) ?? [];

    internal bool TryGetNodeFromEnr(NodeRecord enr, [NotNullWhen(true)] out Node? node)
    {
        if (NodeRecordConverter.TryGetNodeFromEnr(enr, _allowNonRoutableEnrs, out node))
        {
            return true;
        }

        if (Logger.IsTrace) Logger.Trace("Enr declined, unable to extract a usable discv5 node endpoint.");
        return false;
    }

    private static PublicKey? GetPublicKeyFromEnr(NodeRecord enr) =>
        enr.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1)?.Decompress();

    internal static bool IsDiscoveryAddressAcceptable(IPAddress ipAddress, bool allowNonRoutable)
    {
        if (IPAddress.Any.Equals(ipAddress) || IPAddress.IPv6Any.Equals(ipAddress) || IPAddress.Broadcast.Equals(ipAddress))
        {
            return false;
        }

        if (IPAddressClassifier.IsMulticast(ipAddress))
        {
            return false;
        }

        if (IPAddressClassifier.IsSpecialUseAddress(ipAddress))
        {
            return false;
        }

        return allowNonRoutable || !IPAddressClassifier.IsLoopbackOrPrivateOrLinkLocal(ipAddress);
    }

    internal static bool IsDiscoveryAddressRoutable(IPAddress ipAddress)
        => IsDiscoveryAddressAcceptable(ipAddress, allowNonRoutable: false);

    private static bool ShouldAcceptNonRoutableEnrs(IPAddress externalIp)
        => !IPAddress.Any.Equals(externalIp)
            && !IPAddress.None.Equals(externalIp)
            && IPAddressClassifier.IsLoopbackOrPrivateOrLinkLocal(externalIp);

    internal static bool TryEnqueueNewEnr(Queue<NodeRecord> nodesToCheck, HashSet<NodeRecord> seenNodes, NodeRecord enr)
    {
        if (seenNodes.Count >= MaxTrackedEnrsPerWalk || nodesToCheck.Count >= MaxPendingEnrsPerWalk || !seenNodes.Add(enr))
        {
            return false;
        }

        nodesToCheck.Enqueue(enr);
        return true;
    }

    internal List<NodeRecord> LoadStoredEnrs()
    {
        List<NodeRecord> enrs = [];
        foreach (byte[] enrBytes in _discoveryDb.GetAllValues())
        {
            if (TryLoadStoredEnr(enrBytes, out NodeRecord? enr))
            {
                enrs.Add(enr);
            }
        }

        if (enrs.Count is not 0)
        {
            return enrs;
        }

        IWriteBatch? migrateBatch = null;
        IWriteBatch? deleteBatch = null;

        try
        {
            foreach (KeyValuePair<byte[], byte[]?> kv in _legacyDiscoveryDb.GetAll())
            {
                if (kv.Value is null)
                {
                    continue;
                }

                if (!TryLoadStoredEnr(kv.Value, out NodeRecord? enr))
                {
                    continue;
                }

                PublicKey? publicKey = GetPublicKeyFromEnr(enr);
                if (publicKey is null)
                {
                    deleteBatch ??= _legacyDiscoveryDb.StartWriteBatch();
                    deleteBatch[kv.Key] = null;
                    continue;
                }

                migrateBatch ??= _discoveryDb.StartWriteBatch();
                deleteBatch ??= _legacyDiscoveryDb.StartWriteBatch();
                enrs.Add(enr);
                migrateBatch[publicKey.Hash.Bytes] = kv.Value;
                deleteBatch[kv.Key] = null;
            }
        }
        finally
        {
            migrateBatch?.Dispose();
            deleteBatch?.Dispose();
        }

        return enrs;
    }

    private bool TryLoadStoredEnr(byte[] enrBytes, [NotNullWhen(true)] out NodeRecord? enr)
    {
        try
        {
            enr = NodeRecord.FromBytes(enrBytes);
            return true;
        }
        catch (Exception e)
        {
            enr = null;
            if (Logger.IsDebug) Logger.Debug($"Skipping stored discv5 ENR that cannot be decoded: {e}");
            return false;
        }
    }

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

    protected override Task RunDiscoveryAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(_adapter.RunAsync(cancellationToken), Kademlia.Run(cancellationToken));

    protected override async Task StopAsyncCore()
    {
        PersistKnownEnrs();

        await _adapter.DisposeAsync();
        _discoveryHandler?.Close();
    }

    private void PersistKnownEnrs()
    {
        _discoveryDb.Clear();

        IWriteBatch? batch = null;
        try
        {
            foreach (Node node in Kademlia.IterateNodes())
            {
                if (string.IsNullOrEmpty(node.Enr))
                {
                    continue;
                }

                NodeRecord enr;
                try
                {
                    enr = NodeRecord.FromEnrString(node.Enr);
                }
                catch (Exception e)
                {
                    if (Logger.IsDebug) Logger.Debug($"Skipping malformed discv5 ENR while persisting {node}: {e}");
                    continue;
                }

                batch ??= _discoveryDb.StartWriteBatch();
                batch[node.IdHash.Bytes] = enr.ToRlpBytes();
            }
        }
        finally
        {
            batch?.Dispose();
        }
    }

    protected override ValueTask DisposeAsyncCore() => _services.DisposeAsync();

    private enum BootNodeAddResult
    {
        Added,
        Duplicate,
        Skipped
    }

    private sealed class BootNodeStats
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

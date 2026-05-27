// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
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
using Nethermind.Core.ServiceStopper;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;

[assembly: InternalsVisibleTo("Nethermind.Network.Discovery.Test")]

namespace Nethermind.Network.Discovery.Discv5;

public sealed class DiscoveryV5App : KademliaDiscoveryApp
{
    internal const int MaxPendingEnrsPerWalk = 4_096;
    internal const int MaxTrackedEnrsPerWalk = MaxPendingEnrsPerWalk * 2;

    private readonly IDb _discoveryDb;
    private readonly IDb _legacyDiscoveryDb;
    private readonly bool _allowNonRoutableEnrs;
    private readonly IDiscv5KademliaAdapter _discv5Adapter;
    private readonly Func<NettyDiscoveryV5Handler> _discoveryHandlerFactory;
    private readonly ILifetimeScope _discv5Services;

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
        Action<ContainerBuilder>? configureDiscv5Services = null)
        : base("discv5", networkConfig, processExitSource, logManager.GetClassLogger<DiscoveryV5App>())
    {
        _discoveryDb = discoveryDb;
        _legacyDiscoveryDb = legacyDiscoveryDb;
        _allowNonRoutableEnrs = ShouldAcceptNonRoutableEnrs(ipResolver.ExternalIp);

        List<Node> bootNodes = CreateBootNodes(networkConfig, discoveryConfig);
        ITimestamper timestamper = rootScope.ResolveOptional<ITimestamper>() ?? Timestamper.Default;

        _discv5Services = rootScope.BeginLifetimeScope(builder =>
        {
            builder.RegisterInstance(discoveryConfig).As<IDiscoveryConfig>();
            builder.RegisterInstance(timestamper).As<ITimestamper>();
            builder
                .AddModule(new DiscV5KademliaModule(nodeKey.PublicKey, bootNodes))
                .AddSingleton<DiscV5Services>();

            configureDiscv5Services?.Invoke(builder);
        });

        DiscV5Services services = _discv5Services.Resolve<DiscV5Services>();
        _discv5Adapter = services.Discv5Adapter;
        _discoveryHandlerFactory = services.NettyDiscoveryHandlerFactory;
        UseKademliaServices(services.NodeSource, services.Kademlia);
    }

    private record DiscV5Services(
        IKademliaNodeSource NodeSource,
        IDiscv5KademliaAdapter Discv5Adapter,
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
            string[] defaultBootnodes = GetDefaultDiscv5Bootnodes();
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

    private static string[] GetDefaultDiscv5Bootnodes() =>
        JsonSerializer.Deserialize<string[]>(typeof(DiscoveryV5App).Assembly.GetManifestResourceStream("Nethermind.Network.Discovery.Discv5.discv5-bootnodes.json")!) ?? [];

    internal bool TryGetNodeFromEnr(NodeRecord enr, [NotNullWhen(true)] out Node? node)
    {
        node = null;

        PublicKey? key = GetPublicKeyFromEnr(enr);
        if (key is null)
        {
            if (Logger.IsTrace) Logger.Trace("Enr declined, unable to extract public key.");
            return false;
        }

        (IPAddress? ip, int? discoveryPort) = Discv5NodeRecordConverter.GetDiscoveryEndpoint(enr);
        if (ip is null)
        {
            if (Logger.IsTrace) Logger.Trace("Enr declined, no IP.");
            return false;
        }

        if (discoveryPort is null)
        {
            if (Logger.IsTrace) Logger.Trace("Enr declined, no discovery UDP port.");
            return false;
        }

        if (!IsDiscoveryAddressAcceptable(ip, _allowNonRoutableEnrs))
        {
            if (Logger.IsTrace) Logger.Trace($"Enr declined, non-routable IP {ip}.");
            return false;
        }

        if ((uint)discoveryPort.Value > ushort.MaxValue || discoveryPort.Value == 0)
        {
            if (Logger.IsTrace) Logger.Trace($"Enr declined, invalid discovery UDP port {discoveryPort.Value}.");
            return false;
        }

        node = new Node(key, ip.ToString(), discoveryPort.Value)
        {
            Enr = enr.EnrString
        };
        return true;
    }

    private static PublicKey? GetPublicKeyFromEnr(NodeRecord enr) =>
        enr.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1)?.Decompress();

    internal static bool IsDiscoveryAddressAcceptable(IPAddress ipAddress, bool allowNonRoutable)
    {
        if (IPAddress.Any.Equals(ipAddress) || IPAddress.IPv6Any.Equals(ipAddress) || IPAddress.Broadcast.Equals(ipAddress))
        {
            return false;
        }

        if (ipAddress.IsIPv6Multicast || NodeFilter.IsIPv4Multicast(ipAddress))
        {
            return false;
        }

        if (IsSpecialUseAddress(ipAddress))
        {
            return false;
        }

        return allowNonRoutable || !NodeFilter.IsLoopbackOrPrivateOrLinkLocal(ipAddress);
    }

    internal static bool IsDiscoveryAddressRoutable(IPAddress ipAddress)
        => IsDiscoveryAddressAcceptable(ipAddress, allowNonRoutable: false);

    private static bool IsSpecialUseAddress(IPAddress ipAddress)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!ipAddress.TryWriteBytes(bytes, out int written))
        {
            return true;
        }

        if (written == 4)
        {
            return IsSpecialUseIPv4(bytes[..4]);
        }

        if (IsIPv4MappedIPv6(bytes))
        {
            return IsSpecialUseIPv4(bytes[12..]);
        }

        return IsSpecialUseIPv6(bytes);
    }

    private static bool IsSpecialUseIPv4(ReadOnlySpan<byte> bytes)
    {
        uint v4 = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        byte a = (byte)(v4 >> 24);
        byte b = (byte)(v4 >> 16);
        byte c = (byte)(v4 >> 8);

        return a == 0                              // 0.0.0.0/8
            || a == 192 && b == 0 && c is 0 or 2   // 192.0.0.0/24, 192.0.2.0/24
            || a == 192 && b == 31 && c == 196     // 192.31.196.0/24
            || a == 192 && b == 52 && c == 193     // 192.52.193.0/24
            || a == 192 && b == 88 && c == 99      // 192.88.99.0/24
            || a == 192 && b == 175 && c == 48     // 192.175.48.0/24
            || a == 198 && b is 18 or 19           // 198.18.0.0/15
            || a == 198 && b == 51 && c == 100     // 198.51.100.0/24
            || a == 203 && b == 0 && c == 113      // 203.0.113.0/24
            || a >= 224;                           // 224.0.0.0/4, 240.0.0.0/4
    }

    private static bool IsSpecialUseIPv6(ReadOnlySpan<byte> bytes)
        => bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b && IsZero(bytes[4..12]) // 64:ff9b::/96
            || bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b && bytes[4] == 0x00 && bytes[5] == 0x01 // 64:ff9b:1::/48
            || bytes[0] == 0x01 && IsZero(bytes[1..8]) // 100::/64
            || bytes[0] == 0x20 && bytes[1] == 0x01 && (bytes[2] & 0xfe) == 0x00 // 2001::/23
            || bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8 // 2001:db8::/32
            || bytes[0] == 0x20 && bytes[1] == 0x02 // 2002::/16
            || bytes[0] == 0x3f && bytes[1] == 0xff && (bytes[2] & 0xf0) == 0x00; // 3fff::/20

    private static bool IsIPv4MappedIPv6(ReadOnlySpan<byte> bytes)
        => IsZero(bytes[..10]) && bytes[10] == 0xff && bytes[11] == 0xff;

    private static bool IsZero(ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ShouldAcceptNonRoutableEnrs(IPAddress externalIp)
        => !IPAddress.Any.Equals(externalIp)
            && !IPAddress.None.Equals(externalIp)
            && NodeFilter.IsLoopbackOrPrivateOrLinkLocal(externalIp);

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

                if (enrs.Count is 0)
                {
                    migrateBatch = _discoveryDb.StartWriteBatch();
                    deleteBatch = _legacyDiscoveryDb.StartWriteBatch();
                }

                enrs.Add(enr);
                PublicKey? publicKey = GetPublicKeyFromEnr(enr);
                if (publicKey is not null)
                {
                    migrateBatch![publicKey.Hash.Bytes] = kv.Value;
                    deleteBatch![kv.Key] = null;
                }
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
        Task.WhenAll(_discv5Adapter.RunAsync(cancellationToken), Kademlia.Run(cancellationToken));

    protected override async Task StopAsyncCore()
    {
        PersistKnownEnrs();

        await _discv5Adapter.DisposeAsync();
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

    protected override ValueTask DisposeAsyncCore() => _discv5Services.DisposeAsync();

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

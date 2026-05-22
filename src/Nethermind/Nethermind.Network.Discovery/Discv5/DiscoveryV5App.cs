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

public sealed class DiscoveryV5App : IDiscoveryApp, IAsyncDisposable
{
    internal const int MaxPendingEnrsPerWalk = 4_096;
    internal const int MaxTrackedEnrsPerWalk = MaxPendingEnrsPerWalk * 2;

    private readonly ILogger _logger;
    private readonly IDb _discoveryDb;
    private readonly IDb _legacyDiscoveryDb;
    private readonly ILogManager _logManager;
    private readonly bool _allowNonRoutableEnrs;
    private readonly IKademliaNodeSource _kademliaNodeSource;
    private readonly IDiscv5KademliaAdapter _discv5Adapter;
    private readonly IKademlia<PublicKey, Node> _kademlia;
    private readonly Func<NettyDiscoveryV5Handler> _discoveryHandlerFactory;
    private readonly ILifetimeScope _discv5Services;
    private readonly CancellationTokenSource _stopCts;

    private NettyDiscoveryV5Handler? _discoveryHandler;
    private DiscoveryV5Report? _discoveryReport;
    private Task? _runningTask;

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
    {
        _logger = logManager.GetClassLogger<DiscoveryV5App>();
        _discoveryDb = discoveryDb;
        _legacyDiscoveryDb = legacyDiscoveryDb;
        _logManager = logManager;
        _allowNonRoutableEnrs = ShouldAcceptNonRoutableEnrs(ipResolver.ExternalIp);
        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(processExitSource.Token);

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

        (_kademliaNodeSource, _discv5Adapter, _kademlia, _discoveryHandlerFactory) = _discv5Services.Resolve<DiscV5Services>();
    }

    private record DiscV5Services(
        IKademliaNodeSource NodeSource,
        IDiscv5KademliaAdapter Discv5Adapter,
        IKademlia<PublicKey, Node> Kademlia,
        Func<NettyDiscoveryV5Handler> NettyDiscoveryHandlerFactory
    )
    {
    }

    private List<Node> CreateBootNodes(INetworkConfig networkConfig, IDiscoveryConfig discoveryConfig)
    {
        List<Node> bootNodes = [];
        HashSet<Hash256> seen = [];

        NetworkNode[] configuredBootnodes = networkConfig.Bootnodes;
        for (int i = 0; i < configuredBootnodes.Length; i++)
        {
            AddBootNode(bootNodes, seen, configuredBootnodes[i]);
        }

        if (discoveryConfig.UseDefaultDiscv5Bootnodes)
        {
            string[] defaultBootnodes = GetDefaultDiscv5Bootnodes();
            for (int i = 0; i < defaultBootnodes.Length; i++)
            {
                AddBootNode(bootNodes, seen, NodeRecord.FromEnrString(defaultBootnodes[i]));
            }
        }

        List<NodeRecord> storedEnrs = LoadStoredEnrs();
        for (int i = 0; i < storedEnrs.Count; i++)
        {
            AddBootNode(bootNodes, seen, storedEnrs[i]);
        }

        if (bootNodes.Count == 0 && _logger.IsWarn)
        {
            _logger.Warn("No discv5 bootnodes specified in configuration");
        }

        return bootNodes;
    }

    private void AddBootNode(List<Node> bootNodes, HashSet<Hash256> seen, NetworkNode networkNode)
    {
        Node node = new(networkNode.NodeId, networkNode.Host, networkNode.Port);
        if (networkNode.IsEnr)
        {
            node.Enr = networkNode.ToString();
        }

        AddBootNode(bootNodes, seen, node);
    }

    private void AddBootNode(List<Node> bootNodes, HashSet<Hash256> seen, NodeRecord nodeRecord)
    {
        if (TryGetNodeFromEnr(nodeRecord, out Node? node))
        {
            AddBootNode(bootNodes, seen, node);
        }
    }

    private static void AddBootNode(List<Node> bootNodes, HashSet<Hash256> seen, Node node)
    {
        if (seen.Add(node.IdHash))
        {
            node.IsBootnode = true;
            bootNodes.Add(node);
        }
    }

    private static string[] GetDefaultDiscv5Bootnodes() =>
        JsonSerializer.Deserialize<string[]>(typeof(DiscoveryV5App).Assembly.GetManifestResourceStream("Nethermind.Network.Discovery.Discv5.discv5-bootnodes.json")!) ?? [];

    internal bool TryGetNodeFromEnr(NodeRecord enr, [NotNullWhen(true)] out Node? node)
    {
        node = null;

        PublicKey? key = GetPublicKeyFromEnr(enr);
        if (key is null)
        {
            if (_logger.IsTrace) _logger.Trace("Enr declined, unable to extract public key.");
            return false;
        }

        IPAddress? ip = enr.GetObj<IPAddress>(EnrContentKey.Ip);
        if (ip is null)
        {
            if (_logger.IsTrace) _logger.Trace("Enr declined, no IP.");
            return false;
        }

        int? discoveryPort = GetDiscoveryPort(enr);
        if (discoveryPort is null)
        {
            if (_logger.IsTrace) _logger.Trace("Enr declined, no discovery UDP port.");
            return false;
        }

        if (!IsDiscoveryAddressAcceptable(ip, _allowNonRoutableEnrs))
        {
            if (_logger.IsTrace) _logger.Trace($"Enr declined, non-routable IP {ip}.");
            return false;
        }

        if ((uint)discoveryPort.Value > ushort.MaxValue || discoveryPort.Value == 0)
        {
            if (_logger.IsTrace) _logger.Trace($"Enr declined, invalid discovery UDP port {discoveryPort.Value}.");
            return false;
        }

        node = new Node(key, ip.ToString(), discoveryPort.Value)
        {
            Enr = enr.EnrString
        };
        return true;
    }

    private static int? GetDiscoveryPort(NodeRecord enr) =>
        enr.GetValue<int>(EnrContentKey.Udp) ?? enr.GetValue<int>(EnrContentKey.Tcp);

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

        return allowNonRoutable || !NodeFilter.IsLoopbackOrPrivateOrLinkLocal(ipAddress);
    }

    internal static bool IsDiscoveryAddressRoutable(IPAddress ipAddress)
        => IsDiscoveryAddressAcceptable(ipAddress, allowNonRoutable: false);

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
            if (_logger.IsDebug) _logger.Debug($"Skipping stored discv5 ENR that cannot be decoded: {e}");
            return false;
        }
    }

    public event EventHandler<NodeEventArgs>? NodeRemoved { add { } remove { } }

    public void InitializeChannel(IChannel channel)
    {
        _discoveryHandler = _discoveryHandlerFactory();
        _discoveryHandler.InitializeChannel(channel);
        channel.Pipeline.AddLast(_discoveryHandler);
    }

    public Task StartAsync()
    {
        _discoveryReport = new DiscoveryV5Report(_kademlia, _logManager, _stopCts.Token);
        _runningTask = Task.Factory.StartNew(static state => ((DiscoveryV5App)state!).ActivateAsync(), this, _stopCts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        return Task.CompletedTask;
    }

    private async Task ActivateAsync()
    {
        try
        {
            await Task.WhenAll(_discv5Adapter.RunAsync(_stopCts.Token), _kademlia.Run(_stopCts.Token));
        }
        catch (OperationCanceledException)
        {
            if (_logger.IsInfo) _logger.Info("Discovery V5 App stopped");
        }
        catch (Exception e)
        {
            _logger.DebugError("Error during discovery v5 initialization", e);
        }
    }

    public IAsyncEnumerable<Node> DiscoverNodes(CancellationToken token) => _kademliaNodeSource.DiscoverNodes(token);

    public async Task StopAsync()
    {
        try
        {
            await _stopCts.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            if (_runningTask is not null)
            {
                await _runningTask;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Error in discovery v5 task", e);
        }

        PersistKnownEnrs();

        await _discv5Adapter.DisposeAsync();
        _discoveryHandler?.Close();
        _stopCts.Dispose();
    }

    private void PersistKnownEnrs()
    {
        _discoveryDb.Clear();

        IWriteBatch? batch = null;
        try
        {
            foreach (Node node in _kademlia.IterateNodes())
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
                    if (_logger.IsDebug) _logger.Debug($"Skipping malformed discv5 ENR while persisting {node}: {e}");
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

    string IStoppableService.Description => "discv5";

    public void AddNodeToDiscovery(Node node) => _kademlia.AddOrRefresh(node);

    public ValueTask DisposeAsync() => _discv5Services.DisposeAsync();
}

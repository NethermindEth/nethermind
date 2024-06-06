// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;
using Lantern.Discv5.WireProtocol;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.Enr;
using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Session;
using Lantern.Discv5.WireProtocol.Table;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Config;
using Lantern.Discv5.Enr.Identity.V4;
using Nethermind.Crypto;
using Nethermind.Network.Config;
using Nethermind.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Nethermind.Db;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Nethermind.Core;
using Nethermind.Api;
using System.Collections.Concurrent;
using Nethermind.Network.Discovery.Discv5;

namespace Nethermind.Network.Discovery;

public class DiscoveryV5App : IDiscoveryApp
{
    private readonly IDiscv5Protocol _discv5Protocol;
    private readonly IApiWithNetwork _api;
    private readonly Logging.ILogger _logger;
    private readonly SameKeyGenerator _privateKeyProvider;
    private readonly INetworkConfig _networkConfig;
    private readonly IDiscoveryConfig _discoveryConfig;
    private readonly SimpleFilePublicKeyDb _discoveryDb;
    private readonly CancellationTokenSource _appShutdownSource = new();
    private DiscoveryReport? _discoveryReport;

    public DiscoveryV5App(SameKeyGenerator privateKeyProvider, IApiWithNetwork api, INetworkConfig networkConfig, IDiscoveryConfig discoveryConfig, SimpleFilePublicKeyDb discoveryDb, ILogManager logManager)
    {
        _logger = logManager.GetClassLogger();
        _privateKeyProvider = privateKeyProvider;
        _networkConfig = networkConfig;
        _discoveryConfig = discoveryConfig;
        _discoveryDb = discoveryDb;
        _api = api;

        IdentityVerifierV4 identityVerifier = new();

        SessionOptions sessionOptions = new SessionOptions
        {
            Signer = new IdentitySignerV4(_privateKeyProvider.Generate().KeyBytes),
            Verifier = identityVerifier,
            SessionKeys = new SessionKeys(_privateKeyProvider.Generate().KeyBytes),
        };

        string[] bootstrapNodes = [.. (discoveryConfig.Bootnodes ?? "").Split(",", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct()];

        IServiceCollection services = new ServiceCollection()
           .AddSingleton<ILoggerFactory, NullLoggerFactory>()
           .AddSingleton(sessionOptions.Verifier)
           .AddSingleton(sessionOptions.Signer);

        EnrFactory enrFactory = new(new EnrEntryRegistry());

        Lantern.Discv5.Enr.Enr[] bootstrapEnrs = [
            .. bootstrapNodes.Where(e => e.StartsWith("enode:"))
                .Select(e => new Enode(e))
                .Select(e => new EnrBuilder()
                    .WithIdentityScheme(sessionOptions.Verifier, sessionOptions.Signer)
                    .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
                    .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(NBitcoin.Secp256k1.Context.Instance.CreatePubKey(e.PublicKey.PrefixedBytes).ToBytes(false)))
                    .WithEntry(EnrEntryKey.Ip, new EntryIp(e.HostIp))
                    .WithEntry(EnrEntryKey.Tcp, new EntryTcp(e.Port))
                    .WithEntry(EnrEntryKey.Udp, new EntryUdp(e.DiscoveryPort))
                    .Build()),
            .. bootstrapNodes.Where(e => e.StartsWith("enr:")).Select(enr => enrFactory.CreateFromString(enr, identityVerifier)),
            // TODO: Move to routing table's UpdateFromEnr
            .. _discoveryDb.GetAllValues().Select(enr => enrFactory.CreateFromBytes(enr, identityVerifier))
            ];

        EnrBuilder enrBuilder = new EnrBuilder()
            .WithIdentityScheme(sessionOptions.Verifier, sessionOptions.Signer)
            .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
            .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(sessionOptions.Signer.PublicKey))
            .WithEntry(EnrEntryKey.Ip, new EntryIp(_api.IpResolver!.ExternalIp))
            .WithEntry(EnrEntryKey.Tcp, new EntryTcp(_networkConfig.P2PPort))
            .WithEntry(EnrEntryKey.Udp, new EntryUdp(_networkConfig.DiscoveryPort));

        _discv5Protocol = new Discv5ProtocolBuilder(services)
            .WithConnectionOptions(new ConnectionOptions
            {
                UdpPort = _networkConfig.DiscoveryPort,
            })
            .WithSessionOptions(sessionOptions)
            .WithTableOptions(new TableOptions(bootstrapEnrs.Select(enr => enr.ToString()).ToArray()))
            .WithEnrBuilder(enrBuilder)
            .WithLoggerFactory(NullLoggerFactory.Instance)
            .Build();


        _discv5Protocol.NodeAdded += (e) => NodeAddedByDiscovery(e.Record);
        _discv5Protocol.NodeRemoved += NodeRemovedByDiscovery;

        _discoveryReport = new DiscoveryReport(_discv5Protocol, logManager, _appShutdownSource.Token);
    }

    private void NodeAddedByDiscovery(IEnr newEntry)
    {
        if (!TryGetNodeFromEnr(newEntry, out Node? newNode))
        {
            return;
        }

        NodeAdded?.Invoke(this, new NodeEventArgs(newNode));

        _discoveryReport?.NodeFound();

        if (_logger.IsDebug) _logger.Debug($"A node discovered via discv5: {newEntry} = {newNode}.");
    }

    private void NodeRemovedByDiscovery(NodeTableEntry removedEntry)
    {
        if (!TryGetNodeFromEnr(removedEntry.Record, out Node? removedNode))
        {
            return;
        }

        NodeRemoved?.Invoke(this, new NodeEventArgs(removedNode));

        if (_logger.IsDebug) _logger.Debug($"Node removed from discovered via discv5: {removedEntry.Record} = {removedNode}.");
    }

    private bool TryGetNodeFromEnr(IEnr enr, [NotNullWhen(true)] out Node? node)
    {
        static PublicKey GetPublicKeyFromEnr(IEnr entry)
        {
            byte[] keyBytes = entry.GetEntry<EntrySecp256K1>(EnrEntryKey.Secp256K1).Value;
            return new PublicKey(keyBytes.Length == 33 ? NBitcoin.Secp256k1.Context.Instance.CreatePubKey(keyBytes).ToBytes(false) : keyBytes);
        }

        node = null;
        if (!enr.HasKey(EnrEntryKey.Tcp))
        {
            if (_logger.IsTrace) _logger.Trace($"Enr declined, no TCP port.");
            return false;
        }
        if (!enr.HasKey(EnrEntryKey.Ip))
        {
            if (_logger.IsTrace) _logger.Trace($"Enr declined, no IP.");
            return false;
        }
        if (!enr.HasKey(EnrEntryKey.Secp256K1))
        {
            if (_logger.IsTrace) _logger.Trace($"Enr declined, no signature.");
            return false;
        }
        if (enr.HasKey(EnrEntryKey.Eth2))
        {
            if (_logger.IsTrace) _logger.Trace($"Enr declined, ETH2 detected.");
            return false;
        }

        PublicKey key = GetPublicKeyFromEnr(enr);
        IPAddress ip = enr.GetEntry<EntryIp>(EnrEntryKey.Ip).Value;
        int tcpPort = enr.GetEntry<EntryTcp>(EnrEntryKey.Tcp).Value;

        node = new(key, ip.ToString(), tcpPort);
        return true;
    }

    public List<Node> LoadInitialList()
    {
        return [];
    }

    public event EventHandler<NodeEventArgs>? NodeAdded;
    public event EventHandler<NodeEventArgs>? NodeRemoved;

    public void Initialize(PublicKey masterPublicKey)
    {
    }

    public void Start()
    {
        _ = DiscoverViaCustomRandomWalk();
    }

    private async Task DiscoverViaCustomRandomWalk()
    {
        async Task DiscoverAsync(IEnr[] getAllNodes, byte[] nodeId, CancellationToken token)
        {
            static int[] GetDistances(byte[] srcNodeId, byte[] destNodeId)
            {
                const int totalDistances = 3;
                int[] distances = new int[totalDistances];
                distances[0] = TableUtility.Log2Distance(srcNodeId, destNodeId);
                for (int n = 1, i = 1; n < totalDistances; i++)
                {
                    if (distances[0] - i > 0)
                    {
                        distances[n++] = distances[0] - i;
                    }
                    if (distances[0] + i <= 256)
                    {
                        distances[n++] = distances[0] + i;
                    }
                }

                return distances;
            }

            ConcurrentQueue<IEnr> nodesToCheck = new(getAllNodes);
            HashSet<IEnr> checkedNodes = [];

            while (!token.IsCancellationRequested)
            {
                if (!nodesToCheck.TryDequeue(out IEnr? newEntry))
                {
                    return;
                }

                NodeAddedByDiscovery(newEntry);

                if (!checkedNodes.Add(newEntry))
                {
                    continue;
                }

                IEnr[]? newNodesFound = (await _discv5Protocol.SendFindNodeAsync(newEntry, GetDistances(newEntry.NodeId, nodeId)))?.Where(x => !checkedNodes.Contains(x)).ToArray();

                if (newNodesFound is not null)
                {
                    foreach (IEnr? node in newNodesFound)
                    {
                        nodesToCheck.Enqueue(node);
                    }
                }
            }
        }

        IEnr[] GetStartingNodes()
        {
            IEnr[] activeNodes = _discv5Protocol.GetActiveNodes.ToArray();
            if (activeNodes.Length != 0)
            {
                return activeNodes;
            }
            return _discv5Protocol.GetAllNodes.ToArray();
        }

        Random random = new();
        await _discv5Protocol!.InitAsync();

        // temporary fix: give discv5 time to initialize
        await Task.Delay(10_000);

        if (_logger.IsDebug) _logger.Debug($"Initially discovered {_discv5Protocol.GetActiveNodes.Count()} active peers, {_discv5Protocol.GetAllNodes.Count()} in total.");

        byte[] randomNodeId = new byte[32];
        while (!_appShutdownSource.IsCancellationRequested)
        {
            try
            {
                await DiscoverAsync(GetStartingNodes(), _discv5Protocol.SelfEnr.NodeId, _appShutdownSource.Token);

                for (int i = 0; i < 3; i++)
                {
                    random.NextBytes(randomNodeId);
                    await DiscoverAsync(GetStartingNodes(), randomNodeId, _appShutdownSource.Token);
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsWarn) _logger.Warn($"Custom random walk failed with {ex.Message} at {ex.StackTrace}.");
            }
            await Task.Delay(_discoveryConfig.DiscoveryInterval, _appShutdownSource.Token);
        }
    }

    public async Task StopAsync()
    {
        await _discv5Protocol!.StopAsync();
        _appShutdownSource.Cancel();
        _discoveryDb.Clear();

        if (_api.PeerManager is null)
        {
            return;
        }

        List<EntrySecp256K1> activeNodes = _api.PeerManager.ActivePeers.Select(x => new EntrySecp256K1(NBitcoin.Secp256k1.Context.Instance.CreatePubKey(x.Node.Id.PrefixedBytes).ToBytes(false))).ToList();

        IEnumerable<IEnr> activeNodeEnrs = _discv5Protocol.GetAllNodes.Where(x => activeNodes.Any(n => n.Equals(x.GetEntry<EntrySecp256K1>(EnrEntryKey.Secp256K1))));

        if (activeNodeEnrs.Any())
        {
            using IWriteBatch batch = _discoveryDb.StartWriteBatch();
            foreach (IEnr enr in activeNodeEnrs)
            {
                batch[enr.NodeId] = enr.EncodeRecord();
            }
        }
    }

    public void AddNodeToDiscovery(Node node)
    {
    }
}

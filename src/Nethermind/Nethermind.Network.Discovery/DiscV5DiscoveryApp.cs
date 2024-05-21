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

namespace Nethermind.Network.Discovery;

public class Discv5DiscoveryApp : IDiscoveryApp
{
    private readonly IDiscv5Protocol _discv5Protocol;
    private readonly IApiWithNetwork _api;
    private readonly Logging.ILogger _logger;
    private readonly SameKeyGenerator _privateKeyProvider;
    private readonly INetworkConfig _networkConfig;
    private readonly IDiscoveryConfig _discoveryConfig;
    private readonly SimpleFilePublicKeyDb _discoveryDb;
    private readonly CancellationTokenSource _appShutdownSource = new();
    private bool logDiscovery = true;

    public Discv5DiscoveryApp(SameKeyGenerator privateKeyProvider, IApiWithNetwork api, INetworkConfig networkConfig, IDiscoveryConfig discoveryConfig, SimpleFilePublicKeyDb discoveryDb, ILogManager logManager)
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

        EnrFactory enrFactory = new (new EnrEntryRegistry());

        Lantern.Discv5.Enr.Enr[] bootstrapEnrs = [
            ..bootstrapNodes.Where(e => e.StartsWith("enode:"))
                .Select(e => new Enode(e))
                .Select(e => new EnrBuilder()
                    .WithIdentityScheme(sessionOptions.Verifier, sessionOptions.Signer)
                    .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
                    .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(NBitcoin.Secp256k1.Context.Instance.CreatePubKey(e.PublicKey.PrefixedBytes).ToBytes(false)))
                    .WithEntry(EnrEntryKey.Ip, new EntryIp(e.HostIp))
                    .WithEntry(EnrEntryKey.Tcp, new EntryTcp(e.Port))
                    .WithEntry(EnrEntryKey.Udp, new EntryUdp(e.DiscoveryPort))
                    .Build()),
            ..bootstrapNodes.Where(e => e.StartsWith("enr:")).Select(enr => enrFactory.CreateFromString(enr, identityVerifier)),
            // TODO: Move to routing table's UpdateFromEnr
            .._discoveryDb.GetAllValues().Select(enr => enrFactory.CreateFromBytes(enr, identityVerifier))
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
            .WithTableOptions(new TableOptions(bootstrapEnrs.Select(x => x.ToString()).ToArray()))
            .WithEnrBuilder(enrBuilder)
            .WithLoggerFactory(NullLoggerFactory.Instance)
            .Build();


        _discv5Protocol.NodeAdded += (e) =>  NodeAddedByDiscovery(e.Record);
        _discv5Protocol.NodeRemoved += NodeRemovedByDiscovery;

        _ = Task.Run(async () =>
        {
            while (!_appShutdownSource.IsCancellationRequested)
            {
                if (_logger.IsInfo) _logger.Info($"Nodes checked: {Interlocked.Exchange(ref NodesChecked, 0)}, table: {_discv5Protocol.GetActiveNodes.Count()} {_discv5Protocol.GetAllNodes.Count()}, total eth {TotalEth}.");
                await Task.Delay(5_000);
            }
        });
    }

    int NodesChecked = 0;
    int TotalEth = 0;
    HashSet<IEnr> seen = [];

    private void NodeAddedByDiscovery(IEnr newEntry)
    {
        if (!TryGetNodeFromEnr(newEntry, out Node? newNode))
        {
            return;
        }
        if (seen.Contains(newEntry))
        {
            return;
        }
        seen.Add(newEntry);
        NodeAdded?.Invoke(this, new NodeEventArgs(newNode));
        Interlocked.Increment(ref NodesChecked);
        Interlocked.Increment(ref TotalEth);
        if (_logger.IsInfo && logDiscovery) _logger.Info($"A node discovered via discv5: {newEntry} = {newNode}.");
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

    private static PublicKey GetPublicKeyFromEnr(IEnr entry)
    {
        byte[] keyBytes = entry.GetEntry<EntrySecp256K1>(EnrEntryKey.Secp256K1).Value;
        return new PublicKey(keyBytes.Length == 33 ? NBitcoin.Secp256k1.Context.Instance.CreatePubKey(keyBytes).ToBytes(false) : keyBytes);
    }

    private bool TryGetNodeFromEnr(IEnr enr, [NotNullWhen(true)] out Node? node)
    {
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
        _ = DiscoverViaRandomWalk();
    }

    private async Task DiscoverViaRandomWalk()
    {
        await _discv5Protocol!.InitAsync();
        _logger.Info($"Initially discovered: {_discv5Protocol.GetActiveNodes.Count()} {_discv5Protocol.GetAllNodes.Count()}.");

        try
        {

            byte[] randomNodeId = new byte[32];
            while (!_appShutdownSource.IsCancellationRequested)
            {
                await GethDiscover(_discv5Protocol.GetActiveNodes.ToArray(), _discv5Protocol.SelfEnr.NodeId, NodeAddedByDiscovery);

                for (int i = 0; i < 3; i++)
                {
                    rnd.NextBytes(randomNodeId);
                    await GethDiscover(_discv5Protocol.GetActiveNodes.ToArray(), randomNodeId, NodeAddedByDiscovery);
                }

                await Task.Delay(refreshTime, _appShutdownSource.Token);
                _logger.Info($"Refreshed with: {_discv5Protocol.GetActiveNodes.Count()} {_discv5Protocol.GetAllNodes.Count()}.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"DiscoverViaRandomWalk exception {ex.Message}.");
        }
    }

    SemaphoreSlim s = new(20, 20);
    private async Task GethDiscover(IEnr[] getAllNodes, byte[] nodeId, Action<IEnr> tryAddNode)
    {
        ConcurrentQueue<IEnr> nodesToCheck = new(getAllNodes);
        HashSet<IEnr> checkedNodes = [];

        while (true)
        {
            if (!nodesToCheck.TryDequeue(out IEnr? newEntry))
            {
                return;
            }
            tryAddNode(newEntry);

            if (!checkedNodes.Add(newEntry))
            {
                continue;
            }

            var found = (await _discv5Protocol.SendFindNodeAsync(newEntry, nodeId))?.ToArray() ?? [];

            var toCheck = found.Where(x => !checkedNodes.Contains(x));

            foreach (var node in toCheck)
            {
                nodesToCheck.Enqueue(node);
            }
        }
    }

    int refreshTime = 30_000;

    Random rnd = new();
    //private async Task DiscoverViaRandomWalk()
    //{
    //    try
    //    {
    //        await _discv5Protocol!.InitAsync();
    //        await _discv5Protocol.DiscoverAsync(_discv5Protocol.SelfEnr.NodeId);

    //        _logger.Info($"Initially discovered: {_discv5Protocol.GetActiveNodes.Count()} {_discv5Protocol.GetAllNodes.Count()}.");

    //        byte[] randomNodeId = new byte[32];
    //        while (!_appShutdownSource.IsCancellationRequested)
    //        {               
    //            for (int i = 0; i < 3; i++)
    //            {
    //                rnd.NextBytes(randomNodeId);
    //                (await DiscoverAsync(randomNodeId))?.ToList().ForEach(NodeAddedByDiscovery);;
    //            }

    //            {
    //                (await DiscoverAsync(_discv5Protocol.SelfEnr.NodeId))?.ToList().ForEach(NodeAddedByDiscovery);
    //            }

    //            await Task.Delay(refreshTime, _appShutdownSource.Token);
    //            _logger.Info($"Refreshed with: {_discv5Protocol.GetActiveNodes.Count()} {_discv5Protocol.GetAllNodes.Count()}.");
    //        }
    //    }
    //    catch (Exception ex)
    //    {
    //        _logger.Error($"DiscoverViaRandomWalk exception {ex.Message}.");

    //    }
    //}

    //private async Task<List<IEnr>> DiscoverAsync(byte[] randomNodeId)
    //{
    //    HashSet<IEnr> discovered = [];

    //    for (; ; )
    //    {
    //        var c = discovered.Count;
    //        var nodes = (await _discv5Protocol.DiscoverAsync(randomNodeId))?.ToArray();
    //        if (nodes is null)
    //        {
    //            break;
    //        }
    //        if (nodes.Length == 0)
    //        {
    //            break;
    //        }
    //        Console.WriteLine("ITERATE", Convert.ToHexString(randomNodeId.Take(8).ToArray()).ToLower());
    //        discovered.AddRange(nodes);
    //        if (c == discovered.Count)
    //        {
    //            break;
    //        }
    //    }
    //    return [.. discovered];
    //}

    //private async Task DiscoverAsync(byte[] nodeId, bool isSelf = false)
    //{
    //    HashSet<string> checkedNodes = [];
    //    Queue<IEnr> nodesToCheck = new(_discv5Protocol.GetActiveNodes.ToList());

    //    while (nodesToCheck.Any())
    //    {
    //        rnd.NextBytes(nodeId);
    //        IEnr newEntry = nodesToCheck.Dequeue();
    //        byte[] lookupNodeId = isSelf ? TableUtility.Distance(newEntry.NodeId, nodeId) : nodeId;

    //        checkedNodes.Add(newEntry.ToPeerId());

    //        if (!_discv5Protocol.GetActiveNodes.Contains(newEntry))
    //        {
    //            await _discv5Protocol.SendPingAsync(newEntry);
    //        }
    //        var enrs = (await _discv5Protocol.SendFindNodeAsync(newEntry, lookupNodeId))?.ToList();
    //        if (enrs is null)
    //        {
    //            continue;
    //        }
    //        foreach (var enr in enrs)
    //        {
    //            if (checkedNodes.Contains(enr.ToPeerId()))
    //            {
    //                continue;
    //            }

    //            if (newEntry.NodeId.SequenceEqual(nodeId) || newEntry.NodeId.SequenceEqual(lookupNodeId))
    //            {
    //                continue;
    //            }

    //            NodeAddedByDiscovery(enr);

    //            nodesToCheck.Enqueue(enr);
    //        }
    //    }
    //}

    //SemaphoreSlim s = new(1, 1);
    //private async Task Crawl()
    //{
    //    byte[] nodeId = new byte[32];

    //    HashSet<string> checkedNodes = [];
    //    Queue<IEnr> nodesToCheck = new(_discv5Protocol.GetActiveNodes.ToList());

    //    rnd.NextBytes(nodeId);
    //    while (true)
    //    {
    //        if (!nodesToCheck.TryDequeue(out IEnr? newEntry))
    //        {
    //            await Task.Delay(100);
    //            continue;
    //        }
    //        checkedNodes.Add(newEntry.ToPeerId());

    //        _ = Task.Run(async () =>
    //        {
    //            var item = newEntry;
    //            await s.WaitAsync();
    //            try
    //            {
    //                _logger.Info($"Querying {Convert.ToHexString(item.NodeId).ToLower()}");
    //                Task timeout = Task.Delay(20_000);

    //                for (byte i = 255; i > 240; i--)
    //                {
    //                    if (timeout.IsCompleted)
    //                    {
    //                        _logger.Info($"Break on t/o");
    //                        break;
    //                    }
    //                    _logger.Info($"Request {i + 1} from {Convert.ToHexString(item.NodeId).ToLower()}");
    //                    var enrs = (await _discv5Protocol.SendFindNodeAsync(item, nodeId, [i]))?.ToList();
    //                    if (enrs is null)
    //                    {
    //                        continue;
    //                    }
    //                    _logger.Info($"Returned {enrs.Count} from {Convert.ToHexString(item.NodeId).ToLower()}");

    //                    await Task.Delay(300);

    //                    foreach (var enr in enrs)
    //                    {
    //                        if (checkedNodes.Contains(enr.ToPeerId()))
    //                        {
    //                            continue;
    //                        }

    //                        if (item.NodeId.SequenceEqual(nodeId) || item.NodeId.SequenceEqual(nodeId))
    //                        {
    //                            continue;
    //                        }

    //                        NodeAddedByDiscovery(enr);

    //                        nodesToCheck.Enqueue(enr);
    //                    }
    //                }
    //            }
    //            finally
    //            {
    //                s.Release();
    //            }
    //        });
    //    }


    //}



    public async Task StopAsync()
    {
        await _discv5Protocol!.StopAsync();
        _appShutdownSource.Cancel();
        _discoveryDb.Clear();

        if (_api.PeerManager is null)
        {
            return;
        }
        
        var activeNodes = _api.PeerManager.ActivePeers.Select(x => new EntrySecp256K1(NBitcoin.Secp256k1.Context.Instance.CreatePubKey(x.Node.Id.PrefixedBytes).ToBytes(false))).ToList();

        var activeNodeEnrs = _discv5Protocol.GetAllNodes.Where(x => activeNodes.Any(n => n.Equals(x.GetEntry<EntrySecp256K1>(EnrEntryKey.Secp256K1))));

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
        if (_logger.IsInfo && logDiscovery) _logger.Info($"Node for discovery: {node}.");
    }
}

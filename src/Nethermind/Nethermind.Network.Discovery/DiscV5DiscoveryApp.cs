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

namespace Nethermind.Network.Discovery;

public class Discv5DiscoveryApp : IDiscoveryApp
{
    private readonly IDiscv5Protocol _discv5Protocol;
    private readonly IApiWithNetwork _api;
    private readonly Logging.ILogger _logger;
    private readonly SameKeyGenerator _privateKeyProvider;
    private readonly INetworkConfig _networkConfig;
    private readonly SimpleFilePublicKeyDb _discoveryDb;
    private readonly CancellationTokenSource _appShutdownSource = new();

    public Discv5DiscoveryApp(SameKeyGenerator privateKeyProvider, IApiWithNetwork api, INetworkConfig networkConfig, IDiscoveryConfig discoveryConfig, SimpleFilePublicKeyDb discoveryDb, ILogManager logManager)
    {
        _logger = logManager.GetClassLogger();
        _privateKeyProvider = privateKeyProvider;
        _networkConfig = networkConfig;
        _discoveryDb = discoveryDb;
        _api = api;

        TableConstants.BucketSize = discoveryConfig.BucketSize;

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
            .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(sessionOptions.Signer.PublicKey));

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


        _discv5Protocol.NodeAdded += NodeAddedByDiscovery;
        _discv5Protocol.NodeRemoved += NodeRemovedByDiscovery;
    }

    private void NodeAddedByDiscovery(NodeTableEntry newEntry)
    {
        if (!TryGetNodeFromEnr(newEntry.Record, out Node? newNode))
        {
            return;
        }

        NodeAdded?.Invoke(this, new NodeEventArgs(newNode));

        if (_logger.IsWarn) _logger.Warn($"A node discovered via discv5: {newEntry.Record} = {newNode}.");
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
        PeriodicTimer timer = new(TimeSpan.FromMilliseconds(30_000));

        Random rnd = new();
        await _discv5Protocol!.InitAsync();
        await _discv5Protocol.DiscoverAsync(_discv5Protocol.SelfEnr.NodeId);

        byte[] bytes = new byte[32];

        while (!_appShutdownSource.IsCancellationRequested)
        {
            if (_logger.IsInfo) _logger.Info($"Refresh with: {_discv5Protocol.GetActiveNodes.Count()} {_discv5Protocol.GetAllNodes.Count()}.");

            rnd.NextBytes(bytes);
            var d1 = await _discv5Protocol.DiscoverAsync(bytes);
            rnd.NextBytes(bytes);
            var d2 = await _discv5Protocol.DiscoverAsync(bytes);
            rnd.NextBytes(bytes);
            var d3 = await _discv5Protocol.DiscoverAsync(bytes);
            await timer.WaitForNextTickAsync(_appShutdownSource.Token);
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
    }
}

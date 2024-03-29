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
using Nethermind.State.Snap;

namespace Nethermind.Network.Discovery;

public class DiscV5DiscoveryApp : IDiscoveryApp
{
    private IDiscv5Protocol discv5Protocol;
    private Logging.ILogger _logger;
    private ProtectedPrivateKey _privateKey;
    private readonly INetworkConfig _networkConfig;
    private IDiscoveryConfig _config;
    private SessionOptions _sessionOptions;
    Dictionary<PublicKey, Node> nodes = new();
    Lantern.Discv5.Enr.Enr[] bootstrapEnrs;

    public DiscV5DiscoveryApp(ProtectedPrivateKey privateKey, INetworkConfig networkConfig, IDiscoveryConfig config, ILogManager logManager)
    {
        nodeId[31] = 1;
        _logger = logManager.GetClassLogger();
        _privateKey = privateKey;
        _networkConfig = networkConfig;
        _config = config;

        IdentityVerifierV4 _verifier = new();

        _sessionOptions = new SessionOptions
        {
            Signer = new IdentitySignerV4(_privateKey.Unprotect().KeyBytes),
            Verifier = _verifier,
            SessionKeys = new SessionKeys(_privateKey.Unprotect().KeyBytes),
        };


        string[] bootstrapNodes = [.. (_config.Bootnodes ?? "").Split(",", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct()];
        if (bootstrapNodes is { Length: 0 })
        {
            bootstrapNodes =
            [
                // OP Labs
                "enode://2bd2e657bb3c8efffb8ff6db9071d9eb7be70d7c6d7d980ff80fc93b2629675c5f750bc0a5ef27cd788c2e491b8795a7e9a4a6e72178c14acc6753c0e5d77ae4@34.65.205.244:30305",
                "enode://db8e1cab24624cc62fc35dbb9e481b88a9ef0116114cd6e41034c55b5b4f18755983819252333509bd8e25f6b12aadd6465710cd2e956558faf17672cce7551f@34.65.173.88:30305",
                "enode://bfda2e0110cfd0f4c9f7aa5bf5ec66e6bd18f71a2db028d36b8bf8b0d6fdb03125c1606a6017b31311d96a36f5ef7e1ad11604d7a166745e6075a715dfa67f8a@34.65.229.245:30305",
                // Base
                "enode://548f715f3fc388a7c917ba644a2f16270f1ede48a5d88a4d14ea287cc916068363f3092e39936f1a3e7885198bef0e5af951f1d7b1041ce8ba4010917777e71f@18.210.176.114:30301",
                "enode://6f10052847a966a725c9f4adf6716f9141155b99a0fb487fea3f51498f4c2a2cb8d534e680ee678f9447db85b93ff7c74562762c3714783a7233ac448603b25f@107.21.251.55:30301",
            ];
        }

        var services = new ServiceCollection()
           .AddSingleton<ILoggerFactory, NullLoggerFactory>()
           .AddSingleton(_sessionOptions.Verifier)
           .AddSingleton(_sessionOptions.Signer);


        bootstrapEnrs = [
            .. bootstrapNodes.Where(e => e.StartsWith("enode:"))
                .Select(e => new Enode(e))
                .Select(e => new EnrBuilder()
                    .WithIdentityScheme(_sessionOptions.Verifier, _sessionOptions.Signer)
                    .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
                    .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(NBitcoin.Secp256k1.Context.Instance.CreatePubKey(e.PublicKey.PrefixedBytes).ToBytes(false)))
                    .WithEntry(EnrEntryKey.Ip, new EntryIp(e.HostIp))
                    .WithEntry(EnrEntryKey.Tcp, new EntryTcp(e.Port))
                    .WithEntry(EnrEntryKey.Udp, new EntryUdp(e.DiscoveryPort))
                    .Build()),
            .. bootstrapNodes.Where(e => e.StartsWith("enr:")).Select(enr => new EnrFactory(new EnrEntryRegistry()).CreateFromString(enr, _verifier))
            ];


        var discv5Builder = new Discv5ProtocolBuilder(services);
        var enr = new EnrBuilder()
            .WithIdentityScheme(_sessionOptions.Verifier, _sessionOptions.Signer)
            .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
            .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(_sessionOptions.Signer.PublicKey));
        discv5Protocol = discv5Builder
            .WithConnectionOptions(new ConnectionOptions
            {
                UdpPort = _networkConfig.DiscoveryPort,
            })
            .WithSessionOptions(_sessionOptions)
            .WithTableOptions(new TableOptions(bootstrapEnrs.Select(x => x.ToString()).ToArray()) { RefreshIntervalMilliseconds = 20_000 })
            .WithEnrBuilder(enr)
            .WithLoggerFactory(NullLoggerFactory.Instance)
            .Build();

        HashSet<string> peers = new();

        discv5Protocol.NodeAdded += (e) =>
    {
            if (!e.Record.HasKey(EnrEntryKey.Tcp))
            {
                _logger.Warn($"no tcp");
                return;
            }
            if (!e.Record.HasKey(EnrEntryKey.Ip))
            {
                _logger.Warn($"no ip");
                return;
            }
            if (bootstrapNodes.Any(x => x.Contains(e.Record.GetEntry<EntryTcp>(EnrEntryKey.Tcp).Value.ToString())))
            {
                return;
            }
            if (!e.Record.HasKey(EnrEntryKey.Secp256K1))
            {
                _logger.Warn($"no key");
                return;
            }

            var tcp = e.Record.GetEntry<EntryTcp>(EnrEntryKey.Tcp).Value;
              
            peers.Add(e.Record.GetEntry<EntryIp>(EnrEntryKey.Ip).Value.ToString());

            var keyBytes = e.Record.GetEntry<EntrySecp256K1>(EnrEntryKey.Secp256K1).Value;

            var key = new PublicKey(keyBytes.Length == 33 ? NBitcoin.Secp256k1.Context.Instance.CreatePubKey(keyBytes).ToBytes(false) : keyBytes);
            nodes[key] = new Node(
                key,
                e.Record.GetEntry<EntryIp>(EnrEntryKey.Ip).Value.ToString(),
                e.Record.GetEntry<EntryTcp>(EnrEntryKey.Tcp).Value
                );

            NodeAdded?.Invoke(this, new NodeEventArgs(nodes[key]));
            _logger.Warn($"{e.Record} node discovered by discv5: {e.Record.GetEntry<EntryIp>(EnrEntryKey.Ip).Value}:{e.Record.GetEntry<EntryTcp>(EnrEntryKey.Tcp)?.Value}. Total: {peers.Count}");
        };

        discv5Protocol.NodeAddedToCache += (e) =>
        {
            _logger.Debug($"Add {e} node to discv5 cache.");
        };

        discv5Protocol.NodeRemoved += (e) =>
        {
            var keyBytes = e.Record.GetEntry<EntrySecp256K1>(EnrEntryKey.Secp256K1).Value;

            var key = new PublicKey(keyBytes.Length == 33 ? NBitcoin.Secp256k1.Context.Instance.CreatePubKey(keyBytes).ToBytes(false) : keyBytes);
            var node = nodes.GetValueOrDefault(key);
            if (node is not null)
            {
                NodeRemoved?.Invoke(this, new NodeEventArgs(nodes[key]));
            }
            _logger.Debug($"Remove {e} node from discovered by discv5.");
        };

        discv5Protocol.NodeRemovedFromCache += (e) =>
        {
            _logger.Debug($"Remove {e} node from discv5 cache.");
        };
    }

    public List<Node> LoadInitialList()
    {
        return [];
    }

#pragma warning disable CS0067 // The event is never used
    public event EventHandler<NodeEventArgs>? NodeAdded;
    public event EventHandler<NodeEventArgs>? NodeRemoved;
#pragma warning restore CS0067 // The event is never used
    public void Initialize(PublicKey masterPublicKey)
    {


    }

    byte[] nodeId = new byte[32];
    private bool stopped;

    public void Start()
    {
        Task.Run(async () =>
        {
            await discv5Protocol!.InitAsync();
            await discv5Protocol.DiscoverAsync(discv5Protocol.SelfEnr.NodeId);

            static byte[] R()
            {
                var bytes = new byte[32];
                rnd.NextBytes(bytes);
                return bytes;
            }

            while (!stopped)
            {
                await discv5Protocol.DiscoverAsync(R());
                await discv5Protocol.DiscoverAsync(R());
                await discv5Protocol.DiscoverAsync(R());
                await Task.Delay(30_000);
            }
        });
     

    }
    static readonly Random rnd = new();

    public Task StopAsync()
    {
        stopped = true;
        return discv5Protocol!.StopAsync();
    }

    public void AddNodeToDiscovery(Node node)
    {
    }
}

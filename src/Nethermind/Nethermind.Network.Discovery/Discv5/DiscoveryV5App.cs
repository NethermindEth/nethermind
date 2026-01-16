// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using DotNetty.Transport.Channels;
using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.Enr.Identity.V4;
using Lantern.Discv5.WireProtocol;
using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Session;
using Lantern.Discv5.WireProtocol.Table;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin.Secp256k1;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.ServiceStopper;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Stats.Model;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using ENR = Lantern.Discv5.Enr.Enr;

[assembly: InternalsVisibleTo("Nethermind.Network.Discovery.Test")]

namespace Nethermind.Network.Discovery.Discv5;

public sealed class DiscoveryV5App : IDiscoveryApp
{
    private readonly IDiscv5Protocol _discv5Protocol;
    private readonly Logging.ILogger _logger;
    private readonly IDb _discoveryDb;
    private readonly IDb _legacyDiscoveryDb;
    private readonly ILogManager _logManager;
    private readonly CancellationTokenSource _appShutdownSource = new();
    private DiscoveryV5Report? _discoveryReport;
    private readonly IServiceProvider _serviceProvider;
    private readonly SessionOptions _sessionOptions;
    private readonly EnrFactory _enrFactory;

    public DiscoveryV5App(
        [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey,
        IIPResolver ipResolver,
        INetworkConfig networkConfig,
        IDiscoveryConfig discoveryConfig,
        [KeyFilter(DbNames.DiscoveryV5Nodes)] IDb discoveryDb,
        [KeyFilter(DbNames.DiscoveryNodes)] IDb legacyDiscoveryDb,
        ILogManager logManager)
    {
        _logger = logManager.GetClassLogger();
        _discoveryDb = discoveryDb;
        _legacyDiscoveryDb = legacyDiscoveryDb;
        _logManager = logManager;
        IdentityVerifierV4 identityVerifier = new();

        PrivateKey privateKey = nodeKey.Unprotect();
        _sessionOptions = new()
        {
            Signer = new IdentitySignerV4(privateKey.KeyBytes),
            Verifier = identityVerifier,
            SessionKeys = new SessionKeys(privateKey.KeyBytes),
        };

        string[] bootstrapNodes = [.. (networkConfig.Bootnodes ?? "").Split(",", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct()];

        IServiceCollection services = new ServiceCollection()
           .AddSingleton<ILoggerFactory, NullLoggerFactory>()
           .AddSingleton(_sessionOptions.Verifier)
           .AddSingleton(_sessionOptions.Signer);

        _enrFactory = new EnrFactory(new EnrEntryRegistry());

        ENR[] bootstrapEnrs = [
            .. bootstrapNodes.Where(e => Enode.IsEnode(e, out _)).Select(e => ToEnr(new Enode(e))),
            .. bootstrapNodes.Where(e => e.StartsWith("enr:")).Select(ToEnr),
            .. (discoveryConfig.UseDefaultDiscv5Bootnodes ? GetDefaultDiscv5Bootnodes().Select(ToEnr) : []),
            .. LoadStoredEnrs(),
            ];

        EnrBuilder enrBuilder = new EnrBuilder()
            .WithIdentityScheme(_sessionOptions.Verifier, _sessionOptions.Signer)
            .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
            .WithEntry(EnrEntryKey.Ip, new EntryIp(ipResolver.ExternalIp))
            .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(_sessionOptions.Signer.PublicKey))
            .WithEntry(EnrEntryKey.Tcp, new EntryTcp(networkConfig.P2PPort))
            .WithEntry(EnrEntryKey.Udp, new EntryUdp(networkConfig.DiscoveryPort));

        IDiscv5ProtocolBuilder discv5Builder = new Discv5ProtocolBuilder(services)
            .WithConnectionOptions(new ConnectionOptions
            {
                UdpPort = networkConfig.DiscoveryPort
            })
            .WithSessionOptions(_sessionOptions)
            .WithTableOptions(new TableOptions(bootstrapEnrs.Select(enr => enr.ToString()).ToArray()))
            .WithEnrBuilder(enrBuilder)
            .WithTalkResponder(new TalkReqAndRespHandler())
            .WithLoggerFactory(new NethermindLoggerFactory(logManager, true, Microsoft.Extensions.Logging.LogLevel.Debug))
            .WithServices(s =>
            {
                s.AddSingleton(logManager);
                NettyDiscoveryV5Handler.Register(s);
            });

        _discv5Protocol = NetworkHelper.HandlePortTakenError(discv5Builder.Build, networkConfig.DiscoveryPort);

        _serviceProvider = discv5Builder.GetServiceProvider();
    }
    private static string[] GetDefaultDiscv5Bootnodes() =>
        JsonSerializer.Deserialize<string[]>(typeof(DiscoveryV5App).Assembly.GetManifestResourceStream("Nethermind.Network.Discovery.Discv5.discv5-bootnodes.json")!) ?? [];

    private ENR ToEnr(string enrString) => _enrFactory.CreateFromString(enrString, _sessionOptions.Verifier!);

    private ENR ToEnr(byte[] enrBytes) => _enrFactory.CreateFromBytes(enrBytes, _sessionOptions.Verifier!);

    private ENR ToEnr(Enode node) => new EnrBuilder()
        .WithIdentityScheme(_sessionOptions.Verifier!, _sessionOptions.Signer!)
        .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
        .WithEntry(EnrEntryKey.Ip, new EntryIp(node.HostIp))
        .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(Context.Instance.CreatePubKey(node.PublicKey.PrefixedBytes).ToBytes(false)))
        .WithEntry(EnrEntryKey.Tcp, new EntryTcp(node.Port))
        .WithEntry(EnrEntryKey.Udp, new EntryUdp(node.DiscoveryPort))
        .Build();

    private ENR ToEnr(Node node) => new EnrBuilder()
        .WithIdentityScheme(_sessionOptions.Verifier!, _sessionOptions.Signer!)
        .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
        .WithEntry(EnrEntryKey.Ip, new EntryIp(node.Address.Address))
        .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(node.Id.PrefixedBytes))
        .WithEntry(EnrEntryKey.Tcp, new EntryTcp(node.Address.Port))
        .WithEntry(EnrEntryKey.Udp, new EntryUdp(node.Address.Port))
        .Build();

    private bool TryGetNodeFromEnr(IEnr enr, [NotNullWhen(true)] out Node? node)
    {
        static PublicKey? GetPublicKeyFromEnr(IEnr entry)
        {
            byte[] keyBytes = entry.GetEntry<EntrySecp256K1>(EnrEntryKey.Secp256K1).Value;
            return Context.Instance.TryCreatePubKey(keyBytes, out _, out ECPubKey? key) ? new PublicKey(key.ToBytes(false)) : null;
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

        PublicKey? key = GetPublicKeyFromEnr(enr);
        if (key is null)
        {
            if (_logger.IsTrace) _logger.Trace($"Enr declined, unable to extract public key.");
            return false;
        }

        IPAddress ip = enr.GetEntry<EntryIp>(EnrEntryKey.Ip).Value;
        int tcpPort = enr.GetEntry<EntryTcp>(EnrEntryKey.Tcp).Value;

        node = new(key, ip.ToString(), tcpPort)
        {
            Enr = enr.ToString()
        };
        return true;
    }

    internal List<ENR> LoadStoredEnrs()
    {
        List<ENR> enrs = [.. _discoveryDb.GetAllValues().Select(ToEnr)];

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

                try
                {
                    ENR enr = ToEnr(kv.Value);

                    if (enrs.Count is 0)
                    {
                        migrateBatch = _discoveryDb.StartWriteBatch();
                        deleteBatch = _legacyDiscoveryDb.StartWriteBatch();
                    }

                    enrs.Add(enr);
                    migrateBatch![enr.NodeId] = kv.Value;
                    deleteBatch![kv.Key] = null;
                }
                catch
                {
                    // The database has enodes only
                    return [];
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

    public event EventHandler<NodeEventArgs>? NodeRemoved { add { } remove { } }

    public void InitializeChannel(IChannel channel)
    {
        var handler = _serviceProvider.GetRequiredService<NettyDiscoveryV5Handler>();
        handler.InitializeChannel(channel);
        channel.Pipeline.AddLast(handler);
    }

    public async Task StartAsync()
    {
        await _discv5Protocol.InitAsync();

        if (_logger.IsDebug) _logger.Debug($"Initially discovered {_discv5Protocol.GetActiveNodes.Count()} active peers, {_discv5Protocol.GetAllNodes.Count()} in total.");

        _discoveryReport = new DiscoveryV5Report(_discv5Protocol, _logManager, _appShutdownSource.Token);
    }

    public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken token)
    {
        Channel<Node> discoveredNodesChannel = Channel.CreateBounded<Node>(1);

        async Task DiscoverAsync(IEnumerable<IEnr> startingNode, ArrayPoolSpan<byte> nodeId, bool disposeNodeId = true)
        {
            try
            {
                static int[] GetDistances(byte[] srcNodeId, in ArrayPoolSpan<byte> destNodeId)
                {
                    const int WiderDistanceRange = 3;

                    int[] distances = new int[WiderDistanceRange];
                    distances[0] = TableUtility.Log2Distance(srcNodeId, destNodeId);

                    for (int n = 1, i = 1; n < WiderDistanceRange; i++)
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

                Queue<IEnr> nodesToCheck = new(startingNode);
                HashSet<IEnr> checkedNodes = [];

                while (!token.IsCancellationRequested)
                {
                    if (!nodesToCheck.TryDequeue(out IEnr? newEntry))
                    {
                        return;
                    }

                    if (TryGetNodeFromEnr(newEntry, out Node? node2))
                    {
                        await discoveredNodesChannel.Writer.WriteAsync(node2!, token);

                        if (_logger.IsDebug) _logger.Debug($"A node discovered via discv5: {newEntry} = {node2}.");

                        _discoveryReport?.NodeFound();
                    }

                    if (!checkedNodes.Add(newEntry))
                    {
                        continue;
                    }

                    foreach (IEnr newEnr in await _discv5Protocol.SendFindNodeAsync(newEntry, GetDistances(newEntry.NodeId, in nodeId)) ?? [])
                    {
                        if (!checkedNodes.Contains(newEnr))
                        {
                            nodesToCheck.Enqueue(newEnr);
                        }
                    }
                }
            }
            finally
            {
                if (disposeNodeId)
                {
                    nodeId.Dispose();
                }
            }
        }

        IEnumerable<IEnr> GetStartingNodes() => _discv5Protocol.GetAllNodes;
        Random random = new();

        const int RandomNodesToLookupCount = 3;

        Task discoverTask = Task.Run(async () =>
        {
            using ArrayPoolSpan<byte> selfNodeId = new(32);
            _discv5Protocol.SelfEnr.NodeId.CopyTo(selfNodeId);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    using ArrayPoolList<Task> discoverTasks = new(RandomNodesToLookupCount);

                    discoverTasks.Add(DiscoverAsync(GetStartingNodes(), selfNodeId, false));

                    for (int i = 0; i < RandomNodesToLookupCount; i++)
                    {
                        ArrayPoolSpan<byte> randomNodeId = new(32);
                        random.NextBytes(randomNodeId);
                        discoverTasks.Add(DiscoverAsync(GetStartingNodes(), randomNodeId));
                    }

                    await Task.WhenAll(discoverTasks);
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                }
                catch (OperationCanceledException)
                {
                    if (_logger.IsTrace) _logger.Trace($"Discovery has been stopped.");
                }
                catch (Exception ex)
                {
                    if (_logger.IsError) _logger.Error($"Discovery via custom random walk failed.", ex);
                }
            }
        }, token);

        try
        {
            await foreach (Node node in discoveredNodesChannel.Reader.ReadAllAsync(token))
            {
                yield return node;
            }
        }
        finally
        {
            await discoverTask;
        }
    }

    public async Task StopAsync()
    {
        IEnumerable<IEnr> activeNodeEnrs = _discv5Protocol.GetAllNodes;
        _discoveryDb.Clear();

        IWriteBatch? batch = null;

        try
        {
            foreach (IEnr enr in activeNodeEnrs)
            {
                batch ??= _discoveryDb.StartWriteBatch();
                batch[enr.NodeId] = enr.EncodeRecord();
            }
        }
        finally
        {
            batch?.Dispose();
        }

        try
        {
            await _discv5Protocol.StopAsync();
        }
        catch (Exception ex)
        {
            if (_logger.IsWarn) _logger.Warn($"Error when attempting to stop discv5: {ex}");
        }

        await _appShutdownSource.CancelAsync();
    }

    string IStoppableService.Description => "discv5";

    public void AddNodeToDiscovery(Node node)
    {
        IRoutingTable routingTable = _serviceProvider.GetRequiredService<IRoutingTable>();
        routingTable.UpdateFromEnr(ToEnr(node));
    }
}

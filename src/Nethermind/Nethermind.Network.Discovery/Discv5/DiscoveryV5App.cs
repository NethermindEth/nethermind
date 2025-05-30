// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
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
using Nethermind.Core.Crypto;
using Nethermind.Core.ServiceStopper;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv5;

public class DiscoveryV5App : IDiscoveryApp
{
    private readonly IDiscv5Protocol _discv5Protocol;
    private readonly Logging.ILogger _logger;
    private readonly IDb _discoveryDb;
    private readonly CancellationTokenSource _appShutdownSource = new();
    private readonly DiscoveryReport? _discoveryReport;
    private readonly IServiceProvider _serviceProvider;
    private readonly SessionOptions _sessionOptions;

    public DiscoveryV5App(
        [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey,
        IIPResolver? ipResolver,
        INetworkConfig networkConfig,
        IDiscoveryConfig discoveryConfig,
        [KeyFilter(DbNames.DiscoveryNodes)] IDb discoveryDb,
        ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(ipResolver);

        _logger = logManager.GetClassLogger();
        _discoveryDb = discoveryDb;

        IdentityVerifierV4 identityVerifier = new();

        PrivateKey privateKey = nodeKey.Unprotect();
        _sessionOptions = new()
        {
            Signer = new IdentitySignerV4(privateKey.KeyBytes),
            Verifier = identityVerifier,
            SessionKeys = new SessionKeys(privateKey.KeyBytes),
        };

        string[] bootstrapNodes = [.. (discoveryConfig.Bootnodes ?? "").Split(",", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct()];

        IServiceCollection services = new ServiceCollection()
           .AddSingleton<ILoggerFactory, NullLoggerFactory>()
           .AddSingleton(_sessionOptions.Verifier)
           .AddSingleton(_sessionOptions.Signer);

        EnrFactory enrFactory = new(new EnrEntryRegistry());

        Lantern.Discv5.Enr.Enr[] bootstrapEnrs = [
            .. bootstrapNodes.Where(e => e.StartsWith("enode:"))
                .Select(e => new Enode(e))
                .Select(GetEnr),
            .. bootstrapNodes.Where(e => e.StartsWith("enr:")).Select(enr => enrFactory.CreateFromString(enr, identityVerifier)),
            // TODO: Move to routing table's UpdateFromEnr
            .. _discoveryDb.GetAllValues().Select(enr =>
                {
                    try
                    {
                        return enrFactory.CreateFromBytes(enr, identityVerifier);
                    }
                    catch (Exception e)
                    {
                        if (_logger.IsWarn) _logger.Warn($"unable to decode enr {e}");
                        return null;
                    }
                })
                .Where(enr => enr != null)!
            ];

        EnrBuilder enrBuilder = new EnrBuilder()
            .WithIdentityScheme(_sessionOptions.Verifier, _sessionOptions.Signer)
            .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
            .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(_sessionOptions.Signer.PublicKey))
            .WithEntry(EnrEntryKey.Ip, new EntryIp(ipResolver.ExternalIp))
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
            .WithLoggerFactory(new NethermindLoggerFactory(logManager, true))
            .WithServices(s =>
            {
                s.AddSingleton(logManager);
                NettyDiscoveryV5Handler.Register(s);
            });

        _discv5Protocol = NetworkHelper.HandlePortTakenError(discv5Builder.Build, networkConfig.DiscoveryPort);
        _discv5Protocol.NodeRemoved += NodeRemovedByDiscovery;

        _serviceProvider = discv5Builder.GetServiceProvider();
        _discoveryReport = new DiscoveryReport(_discv5Protocol, logManager, _appShutdownSource.Token);
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

        node = new(key, ip.ToString(), tcpPort);
        return true;
    }

    private Lantern.Discv5.Enr.Enr GetEnr(Enode node) => new EnrBuilder()
        .WithIdentityScheme(_sessionOptions.Verifier, _sessionOptions.Signer)
        .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
        .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(Context.Instance.CreatePubKey(node.PublicKey.PrefixedBytes).ToBytes(false)))
        .WithEntry(EnrEntryKey.Ip, new EntryIp(node.HostIp))
        .WithEntry(EnrEntryKey.Tcp, new EntryTcp(node.Port))
        .WithEntry(EnrEntryKey.Udp, new EntryUdp(node.DiscoveryPort))
        .Build();

    private Lantern.Discv5.Enr.Enr GetEnr(Node node) => new EnrBuilder()
        .WithIdentityScheme(_sessionOptions.Verifier, _sessionOptions.Signer)
        .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
        .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(node.Id.PrefixedBytes))
        .WithEntry(EnrEntryKey.Ip, new EntryIp(node.Address.Address))
        .WithEntry(EnrEntryKey.Tcp, new EntryTcp(node.Address.Port))
        .WithEntry(EnrEntryKey.Udp, new EntryUdp(node.Address.Port))
        .Build();

    public event EventHandler<NodeEventArgs>? NodeRemoved;

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
    }

    public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken token)
    {
        Channel<Node> ch = Channel.CreateBounded<Node>(1);

        async Task DiscoverAsync(IEnumerable<IEnr> startingNode, byte[] nodeId)
        {
            static int[] GetDistances(byte[] srcNodeId, byte[] destNodeId)
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
                    await ch.Writer.WriteAsync(node2!, token);
                    if (_logger.IsDebug) _logger.Debug($"A node discovered via discv5: {newEntry} = {node2}.");
                    _discoveryReport?.NodeFound();
                }

                if (!checkedNodes.Add(newEntry))
                {
                    continue;
                }

                IEnumerable<IEnr>? newNodesFound = (await _discv5Protocol.SendFindNodeAsync(newEntry, GetDistances(newEntry.NodeId, nodeId)))?.Where(x => !checkedNodes.Contains(x));

                if (newNodesFound is not null)
                {
                    foreach (IEnr? node in newNodesFound)
                    {
                        nodesToCheck.Enqueue(node);
                    }
                }
            }
        }

        IEnumerable<IEnr> GetStartingNodes() => _discv5Protocol.GetAllNodes;
        Random random = new();

        const int RandomNodesToLookupCount = 3;

        Task discoverTask = Task.Run(async () =>
        {
            byte[] randomNodeId = new byte[32];
            while (!token.IsCancellationRequested)
            {
                try
                {
                    List<Task> discoverTasks = new List<Task>();
                    discoverTasks.Add(DiscoverAsync(GetStartingNodes(), _discv5Protocol.SelfEnr.NodeId));

                    for (int i = 0; i < RandomNodesToLookupCount; i++)
                    {
                        random.NextBytes(randomNodeId);
                        discoverTasks.Add(DiscoverAsync(GetStartingNodes(), randomNodeId));
                    }

                    await Task.WhenAll(discoverTasks);
                }
                catch (Exception ex)
                {
                    if (_logger.IsError) _logger.Error($"Discovery via custom random walk failed.", ex);
                }
            }
        });

        try
        {
            await foreach (Node node in ch.Reader.ReadAllAsync(token))
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
            if (_logger.IsWarn) _logger.Warn($"Err stopping discv5: {ex}");
        }
        await _appShutdownSource.CancelAsync();
    }

    string IStoppableService.Description => "discv5";

    public void AddNodeToDiscovery(Node node)
    {
        var routingTable = _serviceProvider.GetRequiredService<IRoutingTable>();
        routingTable.UpdateFromEnr(GetEnr(node));
    }
}

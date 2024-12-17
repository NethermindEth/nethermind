// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
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
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Stats.Model;
using Nethermind.Blockchain;
using Nethermind.JsonRpc.Modules;
using Nethermind.Network.Discovery.Portal;
using Nethermind.Network.Discovery.Portal.History;
using Nethermind.Network.Discovery.Portal.History.Rpc;
using Nethermind.Network.Discovery.Portal.LanternAdapter;

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
        IBlockTree blockTree,
        IRpcModuleProvider rpcModuleProvider,
        SameKeyGenerator privateKeyProvider,
        IIPResolver? ipResolver,
        INetworkConfig networkConfig,
        IDiscoveryConfig discoveryConfig,
        IDb discoveryDb,
        ILogManager logManager
    )
    {
        ArgumentNullException.ThrowIfNull(ipResolver);

        _logger = logManager.GetClassLogger();
        _discoveryDb = discoveryDb;

        IdentityVerifierV4 identityVerifier = new();

        _sessionOptions = new()
        {
            Signer = new IdentitySignerV4(privateKeyProvider.Generate().KeyBytes),
            Verifier = identityVerifier,
            SessionKeys = new SessionKeys(privateKeyProvider.Generate().KeyBytes),
        };

        string[] bootstrapNodes = [.. (discoveryConfig.Bootnodes ?? "").Split(",", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct()];

        IServiceCollection services = new ServiceCollection()
           .AddSingleton<ILoggerFactory, NullLoggerFactory>()
           .AddSingleton(blockTree)
           .AddSingleton(logManager)
           .AddSingleton(_sessionOptions.Verifier)
           .AddSingleton(_sessionOptions.Signer);

        IEnrEntryRegistry registry = new EnrEntryRegistry();
        registry.RegisterEntry("c", (b) => new RawEntry("c", b));
        registry.RegisterEntry("quic", (b) => new RawEntry("quic", b));
        registry.RegisterEntry("domaintype", (b) => new RawEntry("domaintype", b));
        registry.RegisterEntry("subnets", (b) => new RawEntry("subnets", b));
        registry.RegisterEntry("eth", (b) => new RawEntry("eth", b));
        registry.RegisterEntry("v4", (b) => new RawEntry("v4", b));
        registry.RegisterEntry("opstack", (b) => new RawEntry("opstack", b));

        EnrFactory enrFactory = new(registry);

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
            .WithEnrEntryRegistry(registry)
            .WithLoggerFactory(new NethermindLoggerFactory(logManager, true))
            .WithServices(s =>
            {
                NettyDiscoveryV5Handler.Register(s);
                s
                    .ConfigurePortalNetworkCommonServices()
                    .ConfigureLanternPortalAdapter();
            });

        _discv5Protocol = NetworkHelper.HandlePortTakenError(discv5Builder.Build, networkConfig.DiscoveryPort);
        _discv5Protocol.NodeRemoved += NodeRemovedByDiscovery;

        _serviceProvider = discv5Builder.GetServiceProvider();
        _discoveryReport = new DiscoveryReport(_discv5Protocol, logManager, _appShutdownSource.Token);

        string[] bootNodesStr =
        [
             //Trin bootstrap nodes
             "enr:-Jy4QIs2pCyiKna9YWnAF0zgf7bT0GzlAGoF8MEKFJOExmtofBIqzm71zDvmzRiiLkxaEJcs_Amr7XIhLI74k1rtlXICY5Z0IDAuMS4xLWFscGhhLjEtMTEwZjUwgmlkgnY0gmlwhKEjVaWJc2VjcDI1NmsxoQLSC_nhF1iRwsCw0n3J4jRjqoaRxtKgsEe5a-Dz7y0JloN1ZHCCIyg",
             "enr:-Jy4QKSLYMpku9F0Ebk84zhIhwTkmn80UnYvE4Z4sOcLukASIcofrGdXVLAUPVHh8oPCfnEOZm1W1gcAxB9kV2FJywkCY5Z0IDAuMS4xLWFscGhhLjEtMTEwZjUwgmlkgnY0gmlwhJO2oc6Jc2VjcDI1NmsxoQLMSGVlxXL62N3sPtaV-n_TbZFCEM5AR7RDyIwOadbQK4N1ZHCCIyg",
             "enr:-Jy4QH4_H4cW--ejWDl_W7ngXw2m31MM2GT8_1ZgECnfWxMzZTiZKvHDgkmwUS_l2aqHHU54Q7hcFSPz6VGzkUjOqkcCY5Z0IDAuMS4xLWFscGhhLjEtMTEwZjUwgmlkgnY0gmlwhJ31OTWJc2VjcDI1NmsxoQPC0eRkjRajDiETr_DRa5N5VJRm-ttCWDoO1QAMMCg5pIN1ZHCCIyg",

             //Fluffy bootstrap nodes
             "enr:-Ia4QLBxlH0Y8hGPQ1IRF5EStZbZvCPHQ2OjaJkuFMz0NRoZIuO2dLP0L-W_8ZmgnVx5SwvxYCXmX7zrHYv0FeHFFR0TY2aCaWSCdjSCaXCEwiErIIlzZWNwMjU2azGhAnnTykipGqyOy-ZRB9ga9pQVPF-wQs-yj_rYUoOqXEjbg3VkcIIjjA",
             "enr:-Ia4QM4amOkJf5z84Lv5Fl0RgWeSSDUekwnOPRn6XA1eMWgrHwWmn_gJGtOeuVfuX7ywGuPMRwb0odqQ9N_w_2Qc53gTY2aCaWSCdjSCaXCEwiErIYlzZWNwMjU2azGhAzaQEdPmz9SHiCw2I5yVAO8sriQ-mhC5yB7ea1u4u5QZg3VkcIIjjA",
             "enr:-Ia4QKVuHjNafkYuvhU7yCvSarNIVXquzJ8QOp5YbWJRIJw_EDVOIMNJ_fInfYoAvlRCHEx9LUQpYpqJa04pUDU21uoTY2aCaWSCdjSCaXCEwiErQIlzZWNwMjU2azGhA47eAW5oIDJAqxxqI0sL0d8ttXMV0h6sRIWU4ZwS4pYfg3VkcIIjjA",
             "enr:-Ia4QIU9U3zrP2DM7sfpgLJbbYpg12sWeXNeYcpKN49-6fhRCng0IUoVRI2E51mN-2eKJ4tbTimxNLaAnbA7r7fxVjcTY2aCaWSCdjSCaXCEwiErQYlzZWNwMjU2azGhAxOroJ3HceYvdD2yK1q9w8c9tgrISJso8q_JXI6U0Xwng3VkcIIjjA",

             //Ultralight bootstrap nodes
             "enr:-IS4QFV_wTNknw7qiCGAbHf6LxB-xPQCktyrCEZX-b-7PikMOIKkBg-frHRBkfwhI3XaYo_T-HxBYmOOQGNwThkBBHYDgmlkgnY0gmlwhKRc9_OJc2VjcDI1NmsxoQKHPt5CQ0D66ueTtSUqwGjfhscU_LiwS28QvJ0GgJFd-YN1ZHCCE4k",
             "enr:-IS4QDpUz2hQBNt0DECFm8Zy58Hi59PF_7sw780X3qA0vzJEB2IEd5RtVdPUYZUbeg4f0LMradgwpyIhYUeSxz2Tfa8DgmlkgnY0gmlwhKRc9_OJc2VjcDI1NmsxoQJd4NAVKOXfbdxyjSOUJzmA4rjtg43EDeEJu1f8YRhb_4N1ZHCCE4o",
             "enr:-IS4QGG6moBhLW1oXz84NaKEHaRcim64qzFn1hAG80yQyVGNLoKqzJe887kEjthr7rJCNlt6vdVMKMNoUC9OCeNK-EMDgmlkgnY0gmlwhKRc9-KJc2VjcDI1NmsxoQLJhXByb3LmxHQaqgLDtIGUmpANXaBbFw3ybZWzGqb9-IN1ZHCCE4k",
             "enr:-IS4QA5hpJikeDFf1DD1_Le6_ylgrLGpdwn3SRaneGu9hY2HUI7peHep0f28UUMzbC0PvlWjN8zSfnqMG07WVcCyBhADgmlkgnY0gmlwhKRc9-KJc2VjcDI1NmsxoQJMpHmGj1xSP1O-Mffk_jYIHVcg6tY5_CjmWVg1gJEsPIN1ZHCCE4o"
        ];
        IEnr[] historyNetworkBootnodes = bootNodesStr.Select((str) => enrFactory.CreateFromString(str, identityVerifier)).ToArray();

        IServiceProvider historyNetworkServiceProvider = _serviceProvider.CreateHistoryNetworkServiceProvider(historyNetworkBootnodes);
        _historyNetwork = historyNetworkServiceProvider.GetRequiredService<IPortalHistoryNetwork>();
        rpcModuleProvider.RegisterSingle(historyNetworkServiceProvider.GetRequiredService<IPortalHistoryRpcModule>());
    }

    private IPortalHistoryNetwork _historyNetwork;

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

    public void Initialize(PublicKey masterPublicKey) { }

    public void InitializeChannel(IChannel channel)
    {
        var handler = _serviceProvider.GetRequiredService<NettyDiscoveryV5Handler>();
        handler.InitializeChannel(channel);
        channel.Pipeline.AddLast(handler);

        Task.Run(async () =>
        {
            _logger.Info("lantern adapter registration");

            CancellationToken token = default;

            try
            {
                await _historyNetwork.Run(token);
            }
            catch (Exception e)
            {
                _logger.Error("It crashed", e);
            }
        });
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


        await _discv5Protocol.StopAsync();
        await _appShutdownSource.CancelAsync();
    }

    public void AddNodeToDiscovery(Node node)
    {
        var routingTable = _serviceProvider.GetRequiredService<IRoutingTable>();
        routingTable.UpdateFromEnr(GetEnr(node));
    }
}

// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Sockets;
using System.Runtime.InteropServices;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Api;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.Discovery.Serializers;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery;

/// <summary>
/// Combines several protocol versions under a single <see cref="IDiscoveryApp"/> implementation.
/// </summary>
public class CompositeDiscoveryApp : IDiscoveryApp
{
    private const string DiscoveryNodesDbPath = "discoveryNodes";

    private readonly IApiWithNetwork _api;
    private readonly ILogger _logger;
    private readonly SameKeyGenerator _privateKeyProvider;
    private readonly INetworkConfig _networkConfig;
    private readonly IDiscoveryConfig _discoveryConfig;
    private IConnectionsPool _connections { get; set; }

    private IDiscoveryApp? _v4;
    private IDiscoveryApp? _v5;

    public CompositeDiscoveryApp(IApiWithNetwork api, ILogger logger, SameKeyGenerator privateKeyProvider)
    {
        _api = api;
        _logger = logger;
        _privateKeyProvider = privateKeyProvider;
        _networkConfig = api.Config<INetworkConfig>();
        _discoveryConfig = api.Config<IDiscoveryConfig>();
        _connections = new DiscoveryConnectionsPool(_logger, _networkConfig, _discoveryConfig);
    }

    public event EventHandler<NodeEventArgs>? NodeAdded
    {
        add
        {
            if (_v4 != null) _v4.NodeAdded += value;
            if (_v5 != null) _v5.NodeAdded += value;
        }
        remove
        {
            if (_v4 != null) _v4.NodeAdded -= value;
            if (_v5 != null) _v5.NodeAdded -= value;
        }
    }

    public event EventHandler<NodeEventArgs>? NodeRemoved
    {
        add
        {
            if (_v4 != null) _v4.NodeRemoved += value;
            if (_v5 != null) _v5.NodeRemoved += value;
        }
        remove
        {
            if (_v4 != null) _v4.NodeRemoved -= value;
            if (_v5 != null) _v5.NodeRemoved -= value;
        }
    }

    public void Initialize(PublicKey masterPublicKey)
    {
        if ((_discoveryConfig.DiscoveryVersion & DiscoveryVersion.V4) != 0)
            InitDiscoveryV4(_discoveryConfig, _privateKeyProvider);

        if ((_discoveryConfig.DiscoveryVersion & DiscoveryVersion.V5) != 0)
            InitDiscoveryV5(_privateKeyProvider);
    }

    public void InitializeChannel(IDatagramChannel channel)
    {
        _v4?.InitializeChannel(channel);
        _v5?.InitializeChannel(channel);
    }

    public async Task StartAsync()
    {
        if (_v4 == null && _v5 == null) return;

        Bootstrap bootstrap = new();
        bootstrap.Group(new MultithreadEventLoopGroup(1));

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            bootstrap.ChannelFactory(() => new SocketDatagramChannel(AddressFamily.InterNetwork));
        else
            bootstrap.Channel<SocketDatagramChannel>();

        bootstrap.Handler(new ActionChannelInitializer<IDatagramChannel>(InitializeChannel));

        await _connections.BindAsync(bootstrap, _networkConfig.DiscoveryPort);

        await Task.WhenAll(
            _v4?.StartAsync() ?? Task.CompletedTask,
            _v5?.StartAsync() ?? Task.CompletedTask
        );
    }

    public Task StopAsync() => Task.WhenAll(
        _connections.StopAsync(),
        _v4?.StopAsync() ?? Task.CompletedTask,
        _v5?.StopAsync() ?? Task.CompletedTask
    );

    public void AddNodeToDiscovery(Node node)
    {
        // Add to v4 only
        _v4?.AddNodeToDiscovery(node);
    }

    private void InitDiscoveryV4(IDiscoveryConfig discoveryConfig, SameKeyGenerator privateKeyProvider)
    {
        NodeIdResolver nodeIdResolver = new(_api.EthereumEcdsa!);
        NodeRecord selfNodeRecord = PrepareNodeRecord(privateKeyProvider);
        IDiscoveryMsgSerializersProvider msgSerializersProvider = new DiscoveryMsgSerializersProvider(
            _api.MessageSerializationService,
            _api.EthereumEcdsa!,
            privateKeyProvider,
            nodeIdResolver);

        msgSerializersProvider.RegisterDiscoverySerializers();

        NodeDistanceCalculator nodeDistanceCalculator = new(discoveryConfig);

        NodeTable nodeTable = new(nodeDistanceCalculator, discoveryConfig, _networkConfig, _api.LogManager);
        EvictionManager evictionManager = new(nodeTable, _api.LogManager);

        NodeLifecycleManagerFactory nodeLifeCycleFactory = new(
            nodeTable,
            evictionManager,
            _api.NodeStatsManager!,
            selfNodeRecord,
            discoveryConfig,
            _api.Timestamper,
            _api.LogManager);

        // ToDo: DiscoveryDB is registered outside dbProvider - bad
        SimpleFilePublicKeyDb discoveryDb = new(
            "DiscoveryDB",
            DiscoveryNodesDbPath.GetApplicationResourcePath(_api.Config<IInitConfig>().BaseDbPath),
            _api.LogManager);

        NetworkStorage discoveryStorage = new(
            discoveryDb,
            _api.LogManager);

        DiscoveryManager discoveryManager = new(
            nodeLifeCycleFactory,
            nodeTable,
            discoveryStorage,
            discoveryConfig,
            _api.LogManager
        );

        NodesLocator nodesLocator = new(
            nodeTable,
            discoveryManager,
            discoveryConfig,
            _api.LogManager);

        _v4 = new DiscoveryApp(
            nodesLocator,
            discoveryManager,
            nodeTable,
            _api.MessageSerializationService,
            _api.CryptoRandom,
            discoveryStorage,
            _networkConfig,
            discoveryConfig,
            _api.Timestamper,
            _api.LogManager);

        _v4.Initialize(_api.NodeKey!.PublicKey);
    }

    private void InitDiscoveryV5(SameKeyGenerator privateKeyProvider)
    {
        SimpleFilePublicKeyDb discv5DiscoveryDb = new(
            "EnrDiscoveryDB",
            DiscoveryNodesDbPath.GetApplicationResourcePath(_api.Config<IInitConfig>().BaseDbPath),
            _api.LogManager);

        _v5 = new DiscoveryV5App(privateKeyProvider, _api, _networkConfig, _discoveryConfig, discv5DiscoveryDb, _api.LogManager);
        _v5.Initialize(_api.NodeKey!.PublicKey);
    }

    private NodeRecord PrepareNodeRecord(SameKeyGenerator privateKeyProvider)
    {
        NodeRecord selfNodeRecord = new();
        selfNodeRecord.SetEntry(IdEntry.Instance);
        selfNodeRecord.SetEntry(new IpEntry(_api.IpResolver!.ExternalIp));
        selfNodeRecord.SetEntry(new TcpEntry(_networkConfig.P2PPort));
        selfNodeRecord.SetEntry(new UdpEntry(_networkConfig.DiscoveryPort));
        selfNodeRecord.SetEntry(new Secp256K1Entry(_api.NodeKey!.CompressedPublicKey));
        selfNodeRecord.EnrSequence = 1;
        NodeRecordSigner enrSigner = new(_api.EthereumEcdsa, privateKeyProvider.Generate());
        enrSigner.Sign(selfNodeRecord);
        if (!enrSigner.Verify(selfNodeRecord))
        {
            throw new NetworkingException("Self ENR initialization failed", NetworkExceptionType.Discovery);
        }

        return selfNodeRecord;
    }
}

// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Sockets;
using System.Runtime.InteropServices;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Api;
using Nethermind.Core;
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
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery;

/// <summary>
/// Combines several protocol versions under a single <see cref="IDiscoveryApp"/> implementation.
/// </summary>
public class CompositeDiscoveryApp : IDiscoveryApp
{
    private const string DiscoveryNodesDbPath = "discoveryNodes";

    private readonly ProtectedPrivateKey _nodeKey;
    private readonly INetworkConfig _networkConfig;
    private readonly IDiscoveryConfig _discoveryConfig;
    private readonly IInitConfig _initConfig;
    private readonly IEthereumEcdsa _ethereumEcdsa;
    private readonly IMessageSerializationService _serializationService;
    private readonly ILogManager _logManager;
    private readonly ITimestamper _timestamper;
    private readonly ICryptoRandom? _cryptoRandom;
    private readonly INodeStatsManager _nodeStatsManager;
    private readonly IIPResolver _ipResolver;
    private readonly IPeerManager? _peerManager;
    private readonly IConnectionsPool _connections;

    private IDiscoveryApp? _v4;
    private IDiscoveryApp? _v5;

    public CompositeDiscoveryApp(ProtectedPrivateKey? nodeKey,
        INetworkConfig networkConfig, IDiscoveryConfig discoveryConfig, IInitConfig initConfig,
        IEthereumEcdsa? ethereumEcdsa, IMessageSerializationService? serializationService,
        ILogManager? logManager, ITimestamper? timestamper, ICryptoRandom? cryptoRandom,
        INodeStatsManager? nodeStatsManager, IIPResolver? ipResolver, IPeerManager? peerManager
    )
    {
        _nodeKey = nodeKey ?? throw new ArgumentNullException(nameof(nodeKey));
        _networkConfig = networkConfig;
        _discoveryConfig = discoveryConfig;
        _initConfig = initConfig;
        _ethereumEcdsa = ethereumEcdsa ?? throw new ArgumentNullException(nameof(ethereumEcdsa));
        _serializationService = serializationService ?? throw new ArgumentNullException(nameof(serializationService));
        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
        _cryptoRandom = cryptoRandom;
        _nodeStatsManager = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
        _ipResolver = ipResolver ?? throw new ArgumentNullException(nameof(ipResolver));
        _peerManager = peerManager;
        _connections = new DiscoveryConnectionsPool(logManager.GetClassLogger<DiscoveryConnectionsPool>(), _networkConfig, _discoveryConfig);
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
        var nodeKeyProvider = new SameKeyGenerator(_nodeKey.Unprotect());

        if ((_discoveryConfig.DiscoveryVersion & DiscoveryVersion.V4) != 0)
            InitDiscoveryV4(_discoveryConfig, nodeKeyProvider);

        if ((_discoveryConfig.DiscoveryVersion & DiscoveryVersion.V5) != 0)
            InitDiscoveryV5(nodeKeyProvider);
    }

    public void InitializeChannel(IChannel channel)
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
        _v4?.AddNodeToDiscovery(node);
        _v5?.AddNodeToDiscovery(node);
    }

    private void InitDiscoveryV4(IDiscoveryConfig discoveryConfig, SameKeyGenerator privateKeyProvider)
    {
        NodeIdResolver nodeIdResolver = new(_ethereumEcdsa);
        NodeRecord selfNodeRecord = PrepareNodeRecord(privateKeyProvider);
        IDiscoveryMsgSerializersProvider msgSerializersProvider = new DiscoveryMsgSerializersProvider(
            _serializationService,
            _ethereumEcdsa,
            privateKeyProvider,
            nodeIdResolver);

        msgSerializersProvider.RegisterDiscoverySerializers();

        NodeDistanceCalculator nodeDistanceCalculator = new(discoveryConfig);

        NodeTable nodeTable = new(nodeDistanceCalculator, discoveryConfig, _networkConfig, _logManager);
        EvictionManager evictionManager = new(nodeTable, _logManager);

        NodeLifecycleManagerFactory nodeLifeCycleFactory = new(
            nodeTable,
            evictionManager,
            _nodeStatsManager,
            selfNodeRecord,
            discoveryConfig,
            _timestamper,
            _logManager);

        // ToDo: DiscoveryDB is registered outside dbProvider - bad
        SimpleFilePublicKeyDb discoveryDb = new(
            "DiscoveryDB",
            DiscoveryNodesDbPath.GetApplicationResourcePath(_initConfig.BaseDbPath),
            _logManager);

        NetworkStorage discoveryStorage = new(
            discoveryDb,
            _logManager);

        DiscoveryManager discoveryManager = new(
            nodeLifeCycleFactory,
            nodeTable,
            discoveryStorage,
            discoveryConfig,
            _logManager
        );

        NodesLocator nodesLocator = new(
            nodeTable,
            discoveryManager,
            discoveryConfig,
            _logManager);

        _v4 = new DiscoveryApp(
            nodesLocator,
            discoveryManager,
            nodeTable,
            _serializationService,
            _cryptoRandom,
            discoveryStorage,
            _networkConfig,
            discoveryConfig,
            _timestamper,
            _logManager);

        _v4.Initialize(_nodeKey.PublicKey);
    }

    private void InitDiscoveryV5(SameKeyGenerator privateKeyProvider)
    {
        SimpleFilePublicKeyDb discv5DiscoveryDb = new(
            "EnrDiscoveryDB",
            DiscoveryNodesDbPath.GetApplicationResourcePath(_initConfig.BaseDbPath),
            _logManager);

        _v5 = new DiscoveryV5App(privateKeyProvider, _ipResolver, _peerManager, _networkConfig, _discoveryConfig, discv5DiscoveryDb, _logManager);
        _v5.Initialize(_nodeKey.PublicKey);
    }

    private NodeRecord PrepareNodeRecord(SameKeyGenerator privateKeyProvider)
    {
        NodeRecord selfNodeRecord = new();
        selfNodeRecord.SetEntry(IdEntry.Instance);
        selfNodeRecord.SetEntry(new IpEntry(_ipResolver.ExternalIp));
        selfNodeRecord.SetEntry(new TcpEntry(_networkConfig.P2PPort));
        selfNodeRecord.SetEntry(new UdpEntry(_networkConfig.DiscoveryPort));
        selfNodeRecord.SetEntry(new Secp256K1Entry(_nodeKey.CompressedPublicKey));
        selfNodeRecord.EnrSequence = 1;
        NodeRecordSigner enrSigner = new(_ethereumEcdsa, privateKeyProvider.Generate());
        enrSigner.Sign(selfNodeRecord);
        if (!enrSigner.Verify(selfNodeRecord))
        {
            throw new NetworkingException("Self ENR initialization failed", NetworkExceptionType.Discovery);
        }

        return selfNodeRecord;
    }
}

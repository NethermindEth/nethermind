// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Sockets;
using System.Runtime.InteropServices;
using Autofac.Features.AttributeFilters;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv5;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.RoutingTable;
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

    private readonly IProtectedPrivateKey _nodeKey;
    private readonly INetworkConfig _networkConfig;
    private readonly IDiscoveryConfig _discoveryConfig;
    private readonly IInitConfig _initConfig;
    private readonly INodeRecordProvider _nodeRecordProvider;
    private readonly IMessageSerializationService _serializationService;
    private readonly ILogManager _logManager;
    private readonly ITimestamper _timestamper;
    private readonly ICryptoRandom? _cryptoRandom;
    private readonly INodeStatsManager _nodeStatsManager;
    private readonly IConnectionsPool _connections;
    private readonly IChannelFactory? _channelFactory;

    private IDiscoveryApp? _v4;
    private IDiscoveryApp? _v5;
    private INodeSource _compositeNodeSource = null!;

    public CompositeDiscoveryApp(
        [KeyFilter(IProtectedPrivateKey.NodeKey)]
        IProtectedPrivateKey? nodeKey,
        INetworkConfig networkConfig, IDiscoveryConfig discoveryConfig, IInitConfig initConfig,
        INodeRecordProvider nodeRecordProvider, IMessageSerializationService? serializationService,
        ILogManager? logManager, ITimestamper? timestamper, ICryptoRandom? cryptoRandom,
        INodeStatsManager? nodeStatsManager,
        Func<DiscoveryV5App> discoveryV5Factory,
        IChannelFactory? channelFactory = null
    )
    {
        _nodeKey = nodeKey ?? throw new ArgumentNullException(nameof(nodeKey));
        _networkConfig = networkConfig;
        _discoveryConfig = discoveryConfig;
        _initConfig = initConfig;
        _nodeRecordProvider = nodeRecordProvider;
        _serializationService = serializationService ?? throw new ArgumentNullException(nameof(serializationService));
        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
        _cryptoRandom = cryptoRandom;
        _nodeStatsManager = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
        _connections = new DiscoveryConnectionsPool(logManager.GetClassLogger<DiscoveryConnectionsPool>(), _networkConfig, _discoveryConfig);
        _channelFactory = channelFactory;

        List<INodeSource> allNodeSources = new();

        if ((_discoveryConfig.DiscoveryVersion & DiscoveryVersion.V4) != 0)
        {
            InitDiscoveryV4(_discoveryConfig);
            allNodeSources.Add(_v4!);
        }

        if ((_discoveryConfig.DiscoveryVersion & DiscoveryVersion.V5) != 0)
        {
            _v5 = discoveryV5Factory();
            allNodeSources.Add(_v5!);
        }

        _compositeNodeSource = new CompositeNodeSource(allNodeSources.ToArray());
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

        if (_channelFactory is not null)
            bootstrap.ChannelFactory(() => _channelFactory!.CreateDatagramChannel());
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            bootstrap.ChannelFactory(static () => new SocketDatagramChannel(AddressFamily.InterNetwork));
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

    private void InitDiscoveryV4(IDiscoveryConfig discoveryConfig)
    {
        NodeRecord selfNodeRecord = _nodeRecordProvider.Current;

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
            _networkConfig,
            _logManager
        );

        NodesLocator nodesLocator = new(
            nodeTable,
            discoveryManager,
            discoveryConfig,
            _logManager);

        _v4 = new DiscoveryApp(
            _nodeKey,
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
    }

    public IAsyncEnumerable<Node> DiscoverNodes(CancellationToken cancellationToken)
    {
        return _compositeNodeSource.DiscoverNodes(cancellationToken);
    }

    public event EventHandler<NodeEventArgs>? NodeRemoved
    {
        add => _compositeNodeSource.NodeRemoved += value;
        remove => _compositeNodeSource.NodeRemoved -= value;
    }
}

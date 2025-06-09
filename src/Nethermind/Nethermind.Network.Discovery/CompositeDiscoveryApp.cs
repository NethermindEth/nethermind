// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Sockets;
using System.Runtime.InteropServices;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Core.ServiceStopper;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv5;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery;

/// <summary>
/// Combines several protocol versions under a single <see cref="IDiscoveryApp"/> implementation.
/// </summary>
public class CompositeDiscoveryApp : IDiscoveryApp
{
    private readonly INetworkConfig _networkConfig;
    private readonly IConnectionsPool _connections;
    private readonly IChannelFactory? _channelFactory;

    private IDiscoveryApp? _v4;
    private IDiscoveryApp? _v5;
    private INodeSource _compositeNodeSource = null!;

    public CompositeDiscoveryApp(
        INetworkConfig networkConfig,
        IDiscoveryConfig discoveryConfig,
        ILogManager logManager,
        Func<DiscoveryV5App> discoveryV5Factory, // These two are factory because they are optional.
        Func<DiscoveryApp> discoveryV4Factory,
        IChannelFactory? channelFactory = null
    )
    {
        _networkConfig = networkConfig;
        _connections = new DiscoveryConnectionsPool(logManager.GetClassLogger<DiscoveryConnectionsPool>(), _networkConfig, discoveryConfig);
        _channelFactory = channelFactory;

        List<INodeSource> allNodeSources = new();

        if ((discoveryConfig.DiscoveryVersion & DiscoveryVersion.V4) != 0)
        {
            _v4 = discoveryV4Factory();
            allNodeSources.Add(_v4!);
        }

        if ((discoveryConfig.DiscoveryVersion & DiscoveryVersion.V5) != 0)
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
        _connections.StopAsync()
    );

    string IStoppableService.Description => "discovery connection";

    public void AddNodeToDiscovery(Node node)
    {
        _v4?.AddNodeToDiscovery(node);
        _v5?.AddNodeToDiscovery(node);
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

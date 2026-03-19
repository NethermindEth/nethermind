// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Sockets;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Core.ServiceStopper;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv5;
using Nethermind.Serialization.Rlp;
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
    private readonly IDiscoveryApp[] _discoveryApps;
    private readonly CompositeNodeSource _compositeNodeSource;

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

        List<IDiscoveryApp> discoveryApps = new(2);

        if ((discoveryConfig.DiscoveryVersion & DiscoveryVersion.V4) != 0)
        {
            discoveryApps.Add(discoveryV4Factory());
        }

        if ((discoveryConfig.DiscoveryVersion & DiscoveryVersion.V5) != 0)
        {
            discoveryApps.Add(discoveryV5Factory());
        }

        _discoveryApps = discoveryApps.ToArray();
        _compositeNodeSource = new CompositeNodeSource(_discoveryApps);
    }

    public void InitializeChannel(IChannel channel)
        => ForEachDiscoveryApp(static (discoveryApp, state) => discoveryApp.InitializeChannel(state), channel);

    public async Task StartAsync()
    {
        if (_discoveryApps.Length == 0) return;

        Bootstrap bootstrap = new Bootstrap()
            .Group(new MultithreadEventLoopGroup(1))
            .Option(ChannelOption.Allocator, NethermindBuffers.DiscoveryAllocator)
            .Option(ChannelOption.RcvbufAllocator, new FixedRecvByteBufAllocator(2048 * 2))
            ;

        if (_channelFactory is not null)
            bootstrap.ChannelFactory(() => _channelFactory!.CreateDatagramChannel());
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            bootstrap.ChannelFactory(static () => new SocketDatagramChannel(AddressFamily.InterNetwork));
        else
            bootstrap.Channel<SocketDatagramChannel>();

        bootstrap.Handler(new ActionChannelInitializer<IDatagramChannel>(InitializeChannel));

        await _connections.BindAsync(bootstrap, _networkConfig.DiscoveryPort);

        await WhenAllDiscoveryApps(static discoveryApp => discoveryApp.StartAsync());
    }

    public async Task StopAsync()
    {
        try
        {
            await Task.WhenAll(_connections.StopAsync(), WhenAllDiscoveryApps(static discoveryApp => discoveryApp.StopAsync()));
        }
        finally
        {
            _compositeNodeSource.Dispose();
        }
    }

    string IStoppableService.Description => "discovery connection";

    public void AddNodeToDiscovery(Node node)
    {
        ForEachDiscoveryApp(static (discoveryApp, discoveredNode) => discoveryApp.AddNodeToDiscovery(discoveredNode), node);
    }

    private void ForEachDiscoveryApp<TState>(Action<IDiscoveryApp, TState> action, TState state)
    {
        IDiscoveryApp[] discoveryApps = _discoveryApps;
        for (int i = 0; i < discoveryApps.Length; i++)
        {
            action(discoveryApps[i], state);
        }
    }

    private Task WhenAllDiscoveryApps(Func<IDiscoveryApp, Task> action)
    {
        IDiscoveryApp[] discoveryApps = _discoveryApps;
        if (discoveryApps.Length == 0)
        {
            return Task.CompletedTask;
        }

        ArrayPoolListRef<Task> tasks = new(discoveryApps.Length);
        for (int i = 0; i < discoveryApps.Length; i++)
        {
            tasks.Add(action(discoveryApps[i]));
        }

        Task result = Task.WhenAll(tasks.AsSpan());
        tasks.Dispose();
        return result;
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

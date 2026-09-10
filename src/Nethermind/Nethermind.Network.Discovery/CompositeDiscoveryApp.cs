// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using Nethermind.Core.Collections;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Core.ServiceStopper;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Discv5;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery;

/// <summary>
/// Combines several protocol versions under a single <see cref="IDiscoveryApp"/> implementation.
/// </summary>
public sealed class CompositeDiscoveryApp : IDiscoveryApp
{
    private readonly INetworkConfig _networkConfig;
    private readonly IIPResolver _ipResolver;
    private readonly IConnectionsPool _connections;
    private readonly IChannelFactory? _channelFactory;
    private readonly IDiscoveryApp[] _discoveryApps;
    private readonly CompositeNodeSource _compositeNodeSource;
    private readonly ILogger _logger;

    public CompositeDiscoveryApp(
        INetworkConfig networkConfig,
        IDiscoveryConfig discoveryConfig,
        IIPResolver ipResolver,
        ILogManager logManager,
        Func<DiscoveryV5App> discoveryV5Factory, // These two are factory because they are optional.
        Func<DiscoveryApp> discoveryV4Factory,
        IChannelFactory? channelFactory = null
    )
    {
        _networkConfig = networkConfig;
        _ipResolver = ipResolver;
        _connections = new DiscoveryConnectionsPool(logManager.GetClassLogger<DiscoveryConnectionsPool>(), ipResolver, discoveryConfig);
        _channelFactory = channelFactory;
        _logger = logManager.GetClassLogger<CompositeDiscoveryApp>();

        List<IDiscoveryApp> discoveryApps = new(2);

        if ((discoveryConfig.DiscoveryVersion & DiscoveryVersion.V4) != 0)
        {
            discoveryApps.Add(discoveryV4Factory());
        }

        if ((discoveryConfig.DiscoveryVersion & DiscoveryVersion.V5) != 0)
        {
            discoveryApps.Add(discoveryV5Factory());
        }

        _discoveryApps = [.. discoveryApps];
        _compositeNodeSource = new CompositeNodeSource(_discoveryApps);
    }

    public void InitializeChannel(IChannel channel)
    {
        channel.Pipeline.AddLast(new DiscoveryTrafficHandler());
        ForEachDiscoveryApp(static (discoveryApp, state) => discoveryApp.InitializeChannel(state), channel);
    }

    public async Task StartAsync()
    {
        if (_discoveryApps.Length == 0) return;

        IPAddress localIp = (await _ipResolver.Resolve()).LocalIp;
        Bootstrap bootstrap = new Bootstrap()
            .Group(new MultithreadEventLoopGroup(1))
            .Option(ChannelOption.Allocator, NethermindBuffers.DiscoveryAllocator)
            .Option(ChannelOption.RcvbufAllocator, new FixedRecvByteBufAllocator(2048 * 2))
            ;

        if (_channelFactory is not null)
            bootstrap.ChannelFactory(() => _channelFactory!.CreateDatagramChannel());
        else
            bootstrap.ChannelFactory(() => new SocketDatagramChannel(CreateDatagramSocket(localIp)));

        bootstrap.Handler(new ActionChannelInitializer<IDatagramChannel>(InitializeChannel));

        await _connections.BindAsync(bootstrap, _networkConfig.DiscoveryPort);

        await WhenAllDiscoveryApps(static discoveryApp => discoveryApp.StartAsync());
    }

    /// <summary>
    /// Creates the UDP socket whose address family and dual-mode behavior match a configured listener address.
    /// </summary>
    /// <remarks>
    /// IPv4-mapped listener addresses require an IPv6 dual-mode socket even though endpoint selection treats them as IPv4-only.
    /// </remarks>
    internal static Socket CreateDatagramSocket(IPAddress localIp)
    {
        Socket socket = new(localIp.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        if (localIp.AddressFamily == AddressFamily.InterNetworkV6)
        {
            socket.DualMode = localIp.Equals(IPAddress.IPv6Any) || localIp.IsIPv4MappedToIPv6;
        }

        return socket;
    }

    /// <summary>
    /// Creates a discovery node from an ENR using an address family reachable through the local listener.
    /// </summary>
    internal static bool TryCreateReachableDiscoveryNode(
        NodeRecord record,
        IPAddress localIp,
        IPEndPoint? preferredEndpoint,
        [NotNullWhen(true)] out Node? node)
    {
        Span<AddressFamily> addressFamilies = stackalloc AddressFamily[2];
        int count = DiscoveryAddressSupport.GetSupportedFamilies(localIp, preferredEndpoint, addressFamilies);
        for (int i = 0; i < count; i++)
        {
            if (Node.TryFromDiscoveryEnr(record, addressFamilies[i], out node))
            {
                return true;
            }
        }

        node = null;
        return false;
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
            await DisposeDiscoveryApps();
        }
    }

    string IStoppableService.Description => "discovery connection";

    public void AddNodeToDiscovery(Node node) => ForEachDiscoveryApp(static (discoveryApp, discoveredNode) => discoveryApp.AddNodeToDiscovery(discoveredNode), node);

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

    private async Task DisposeDiscoveryApps()
    {
        IDiscoveryApp[] discoveryApps = _discoveryApps;
        for (int i = 0; i < discoveryApps.Length; i++)
        {
            if (discoveryApps[i] is IAsyncDisposable asyncDisposable)
            {
                try
                {
                    await asyncDisposable.DisposeAsync();
                }
                catch (Exception e)
                {
                    if (_logger.IsWarn) _logger.Warn($"Error disposing discovery app {discoveryApps[i]}: {e}");
                }
            }
        }
    }

    public IAsyncEnumerable<Node> DiscoverNodes(CancellationToken cancellationToken) => _compositeNodeSource.DiscoverNodes(cancellationToken);

    public event EventHandler<NodeEventArgs>? NodeRemoved
    {
        add => _compositeNodeSource.NodeRemoved += value;
        remove => _compositeNodeSource.NodeRemoved -= value;
    }
}

internal sealed class DiscoveryTrafficHandler : SimpleChannelInboundHandler<DatagramPacket>
{
    protected override void ChannelRead0(IChannelHandlerContext context, DatagramPacket packet)
    {
        Interlocked.Add(ref Metrics.DiscoveryBytesReceived, packet.Content.ReadableBytes);
        context.FireChannelRead(packet.Retain());
    }
}

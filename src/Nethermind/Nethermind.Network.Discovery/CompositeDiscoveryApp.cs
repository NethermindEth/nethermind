// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using Nethermind.Core.Collections;
using Nethermind.Core.ServiceStopper;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Discv5;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery;

/// <summary>
/// Combines several protocol versions under a single <see cref="IDiscoveryApp"/> implementation.
/// </summary>
public sealed class CompositeDiscoveryApp : IDiscoveryApp
{
    private readonly INetworkConfig _networkConfig;
    private readonly DiscoveryConnectionsPool _connections;
    private readonly IChannelFactory? _channelFactory;
    private readonly KademliaDiscoveryApp[] _discoveryApps;
    private readonly CompositeNodeSource _compositeNodeSource;
    private readonly ILogger _logger;
    private Action<PooledUdpReceiveResult>? _receiver;

    public CompositeDiscoveryApp(
        INetworkConfig networkConfig,
        IDiscoveryConfig discoveryConfig,
        ILogManager logManager,
        Func<DiscoveryV5App> discoveryV5Factory, // These two are factory because they are optional.
        Func<DiscoveryApp> discoveryV4Factory,
        NetworkListenerState listenerState,
        IChannelFactory? channelFactory = null
    )
        : this(
            networkConfig,
            discoveryConfig,
            logManager,
            listenerState,
            CreateDiscoveryApps(discoveryConfig, discoveryV4Factory, discoveryV5Factory),
            channelFactory)
    {
    }

    internal CompositeDiscoveryApp(
        INetworkConfig networkConfig,
        IDiscoveryConfig discoveryConfig,
        ILogManager logManager,
        NetworkListenerState listenerState,
        KademliaDiscoveryApp[] discoveryApps,
        IChannelFactory? channelFactory = null)
    {
        _networkConfig = networkConfig;
        _connections = new DiscoveryConnectionsPool(logManager.GetClassLogger<DiscoveryConnectionsPool>(), discoveryConfig, listenerState);
        _channelFactory = channelFactory;
        _logger = logManager.GetClassLogger<CompositeDiscoveryApp>();
        _discoveryApps = discoveryApps;
        _compositeNodeSource = new CompositeNodeSource(_discoveryApps);
    }

    private static KademliaDiscoveryApp[] CreateDiscoveryApps(
        IDiscoveryConfig discoveryConfig,
        Func<DiscoveryApp> discoveryV4Factory,
        Func<DiscoveryV5App> discoveryV5Factory)
    {
        List<KademliaDiscoveryApp> discoveryApps = new(2);
        if ((discoveryConfig.DiscoveryVersion & DiscoveryVersion.V4) != 0)
        {
            discoveryApps.Add(discoveryV4Factory());
        }

        if ((discoveryConfig.DiscoveryVersion & DiscoveryVersion.V5) != 0)
        {
            discoveryApps.Add(discoveryV5Factory());
        }

        return [.. discoveryApps];
    }

    /// <summary>
    /// Attaches the protocols to <paramref name="socket"/>, each forwarding the datagrams it does not handle to the next.
    /// </summary>
    private void InitializeChannel(IDatagramSocket socket)
    {
        Action<PooledUdpReceiveResult> next = static datagram => datagram.Dispose();
        for (int i = _discoveryApps.Length - 1; i >= 0; i--)
        {
            KademliaDiscoveryApp discoveryApp = _discoveryApps[i];
            discoveryApp.InitializeChannel(socket, next);
            next = discoveryApp.Receive;
        }

        Volatile.Write(ref _receiver, next);
    }

    private void Receive(PooledUdpReceiveResult datagram)
    {
        Action<PooledUdpReceiveResult>? receiver = Volatile.Read(ref _receiver);
        if (receiver is null)
        {
            datagram.Dispose();
            return;
        }

        receiver(datagram);
    }

    public async Task StartAsync()
    {
        if (_discoveryApps.Length == 0) return;

        try
        {
            IDatagramSocket socket = _connections.Bind(CreateSocket, _networkConfig.DiscoveryPort, Receive);
            // A failed bind leaves the protocols detached, so attach them only to the successful socket.
            // Datagrams received until then are discarded, before the protocol apps start.
            InitializeChannel(socket);

            await WhenAllDiscoveryApps(static discoveryApp => discoveryApp.StartAsync());
        }
        catch
        {
            try
            {
                await StopAsync();
            }
            catch (Exception e)
            {
                if (_logger.IsWarn) _logger.Warn($"Error stopping discovery after startup failed. {e}");
            }

            throw;
        }
    }

    private IDatagramSocket CreateSocket(IPAddress address)
        => _channelFactory?.CreateDatagramSocket() ?? new UdpDatagramSocket(CreateDatagramSocket(address));

    /// <summary>
    /// Creates the UDP socket whose address family and dual-mode behavior match a configured listener address.
    /// </summary>
    /// <remarks>
    /// IPv4-mapped listener addresses require an IPv6 dual-mode socket even though endpoint selection treats them as IPv4-only.
    /// </remarks>
    internal static Socket CreateDatagramSocket(IPAddress localIp)
    {
        Socket socket = new(localIp.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            // UDP has no TIME_WAIT state; exclusive ownership keeps collision and fallback behavior deterministic.
            socket.ExclusiveAddressUse = true;
            if (localIp.AddressFamily == AddressFamily.InterNetworkV6)
            {
                socket.DualMode = DiscoveryAddressSupport.SupportsFamily(localIp, AddressFamily.InterNetwork);
            }

            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
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

// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Sockets;
using System.Runtime.InteropServices;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Api;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery;

/// <summary>
/// Manages all supported versions of <see cref="IDiscoveryApp"/>.
/// </summary>
public class MultiVersionDiscoveryApp : IDiscoveryApp
{
    private readonly ILogger _logger;
    private readonly IApiWithNetwork _api;
    private readonly INetworkConfig _networkConfig;
    private readonly IDiscoveryConfig _discoveryConfig;

    private readonly Dictionary<int, IDiscoveryApp> _byVersion = new();

    public MultiVersionDiscoveryApp(
        ILogger logger, IApiWithNetwork api,
        INetworkConfig networkConfig, IDiscoveryConfig discoveryConfig
    )
    {
        _logger = logger;
        _api = api;
        _networkConfig = networkConfig;
        _discoveryConfig = discoveryConfig;

        //if (_networkConfig.DiscoveryPort >= 0)

    }

    public void Start()
    {
        Bootstrap bootstrap = new();
        bootstrap.Group(new MultithreadEventLoopGroup(1));

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            bootstrap.ChannelFactory(() => new SocketDatagramChannel(AddressFamily.InterNetwork));
        }
        else
        {
            bootstrap.Channel<SocketDatagramChannel>();
        }

        if (_api.DiscoveryApp is { } discoveryApp)
        {
            bootstrap.Handler(new ActionChannelInitializer<IDatagramChannel>(discoveryApp.InitializeChannel));
        }

        if (_api.DiscoveryV5App is { } discoveryV5App)
        {
            bootstrap.Handler(new ActionChannelInitializer<IDatagramChannel>(discoveryV5App.InitializeChannel));
        }

        if ((_api.DiscoveryApp ?? _api.DiscoveryV5App) != null)
        {
            var noMatchHandler = new NoVersionMatchDiscoveryHandler(_logger);
            bootstrap.Handler(new ActionChannelInitializer<IDatagramChannel>(noMatchHandler.Add));
        }
    }

    public void Initialize(PublicKey masterPublicKey)
    {
        foreach (IDiscoveryApp app in _byVersion.Values)
            app.Initialize(masterPublicKey);
    }

    public void InitializeChannel(IDatagramChannel channel)
    {
        foreach (IDiscoveryApp app in _byVersion.Values)
            app.InitializeChannel(channel);
    }

    public Task StopAsync()
    {
        throw new NotImplementedException();
    }

    public void AddNodeToDiscovery(Node node)
    {
        foreach (IDiscoveryApp app in _byVersion.Values)
            app.AddNodeToDiscovery(node);
    }

    public List<Node> LoadInitialList() => [];

    public event EventHandler<NodeEventArgs>? NodeAdded
    {
        add { throw new NotImplementedException(); }
        remove { throw new NotImplementedException(); }
    }

    public event EventHandler<NodeEventArgs>? NodeRemoved
    {
        add { throw new NotImplementedException(); }
        remove { throw new NotImplementedException(); }
    }
}

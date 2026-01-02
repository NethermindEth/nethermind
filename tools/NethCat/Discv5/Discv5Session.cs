// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Discv5;
using Nethermind.Serialization.Rlp;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NethCat.Discv5;

internal sealed class Discv5Session : IAsyncDisposable
{
    public DiscoveryV5App App { get; }

    private readonly IChannel _boundChannel;
    private readonly MultithreadEventLoopGroup _eventLoopGroup;
    private readonly IDb _discoveryDb;

    public Discv5Session(
        DiscoveryV5App app,
        IChannel boundChannel,
        MultithreadEventLoopGroup eventLoopGroup,
        IDb discoveryDb)
    {
        App = app;
        _boundChannel = boundChannel;
        _eventLoopGroup = eventLoopGroup;
        _discoveryDb = discoveryDb;
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await _boundChannel.CloseAsync();
        await _eventLoopGroup.ShutdownGracefullyAsync(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1));
        _discoveryDb.Dispose();
    }

    public static async Task<Discv5Session> CreateAndStartAsync(
        int port,
        string bootnodes,
        PrivateKey privateKey,
        ILogManager logManager)
    {
        IProtectedPrivateKey protectedKey = new ProtectedPrivateKey(privateKey, "");
        IIPResolver ipResolver = new IPResolver(new NetworkConfig(), logManager);

        INetworkConfig networkConfig = new NetworkConfig
        {
            DiscoveryPort = port,
            P2PPort = port
        };

        IDiscoveryConfig discoveryConfig = new DiscoveryConfig
        {
            Bootnodes = bootnodes
        };

        IDb discoveryDb = new MemDb("discv5-nodes");

        DiscoveryV5App discv5App = new(
            protectedKey,
            ipResolver,
            networkConfig,
            discoveryConfig,
            discoveryDb,
            logManager);

        MultithreadEventLoopGroup eventLoopGroup = new(1);

        Bootstrap bootstrap = new Bootstrap()
            .Group(eventLoopGroup)
            .Option(ChannelOption.Allocator, NethermindBuffers.DiscoveryAllocator)
            .Option(ChannelOption.RcvbufAllocator, new FixedRecvByteBufAllocator(2048 * 2));

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            bootstrap.ChannelFactory(static () => new SocketDatagramChannel(AddressFamily.InterNetwork));
        else
            bootstrap.Channel<SocketDatagramChannel>();

        bootstrap.Handler(new ActionChannelInitializer<IDatagramChannel>(channel =>
        {
            discv5App.InitializeChannel(channel);
        }));

        IChannel boundChannel = await bootstrap.BindAsync(port);

        await discv5App.StartAsync();

        return new Discv5Session(discv5App, boundChannel, eventLoopGroup, discoveryDb);
    }

    public static PrivateKey CreatePrivateKey(string? privateKeyHex)
    {
        if (privateKeyHex is not null)
        {
            return new PrivateKey(privateKeyHex);
        }

        byte[] randomBytes = new byte[32];
        Random.Shared.NextBytes(randomBytes);
        return new PrivateKey(randomBytes);
    }
}

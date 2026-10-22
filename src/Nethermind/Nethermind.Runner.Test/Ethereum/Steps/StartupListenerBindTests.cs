// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
using Nethermind.Init;
using Nethermind.Init.Steps;
using Nethermind.JsonRpc;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Rlpx;
using Nethermind.Runner.Ethereum.Steps;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Ethereum.Steps;

[TestFixture]
public class StartupListenerBindTests
{
    // 192.0.2.1 is an RFC 5737 documentation address, never assigned to a local interface.
    private const string UnassignedIp = "192.0.2.1";

    [Test]
    public void InitializeNetwork_fails_when_a_p2p_listener_cannot_bind([Values] bool discovery, [Values] bool portInUse)
    {
        IRlpxHost rlpxHost = Substitute.For<IRlpxHost>();
        IDiscoveryApp discoveryApp = Substitute.For<IDiscoveryApp>();
        ListenerBindException bindFailure = portInUse
            ? new PortInUseException(new SocketException((int)SocketError.AddressAlreadyInUse), 30303)
            : new ListenerBindException(new SocketException((int)SocketError.AddressNotAvailable), 30303);
        Task failedBind = Task.FromException(bindFailure);
        if (discovery)
            discoveryApp.StartAsync().Returns(failedBind);
        else
            rlpxHost.Init().Returns(failedBind);

        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new SyncConfig { NetworkingEnabled = false }))
            .AddSingleton(rlpxHost)
            .AddSingleton(discoveryApp)
            .AddSingleton(Substitute.For<IStaticNodesManager>())
            .AddSingleton(Substitute.For<ITrustedNodesManager>())
            .AddSingleton(Substitute.For<IPeerPool>())
            .AddSingleton(Substitute.For<IPeerManager>())
            .AddSingleton(Substitute.For<ISessionMonitor>())
            .Build();

        InitializeNetwork step = container.Resolve<InitializeNetwork>();

        Assert.That(() => step.Execute(CancellationToken.None), Throws.TypeOf(bindFailure.GetType()));
    }

    [Test]
    public void StartRpc_fails_when_the_rpc_listener_cannot_bind([Values] bool portInUse)
    {
        using TcpListener occupied = new(IPAddress.Loopback, 0);
        occupied.Start();
        int port = ((IPEndPoint)occupied.LocalEndpoint).Port;

        JsonRpcConfig jsonRpcConfig = new() { Enabled = true, Host = portInUse ? "127.0.0.1" : UnassignedIp, Port = port, JwtSecretFile = "unused" };
        InitConfig initConfig = new() { PipelineWarmupEnabled = false, WebSocketsEnabled = false };
        ConfigProvider configProvider = new(jsonRpcConfig, initConfig);

        INethermindApi api = Substitute.For<INethermindApi>();
        api.ConfigProvider.Returns(configProvider);
        api.Config<IJsonRpcConfig>().Returns(jsonRpcConfig);
        api.Config<IInitConfig>().Returns(initConfig);
        api.Config<IKeyStoreConfig>().Returns(new KeyStoreConfig());
        api.LogManager.Returns(TestLogManager.Instance);
        api.EthereumJsonSerializer.Returns(new EthereumJsonSerializer());

        StartRpc step = new(api, new Lazy<IJsonRpcServiceConfigurer[]>([]), new WebSocketsManager(), Substitute.For<IJsonRpcLocalStats>(), new Lazy<IJsonRpcService>(Substitute.For<IJsonRpcService>()),
            new Lazy<FlatStateActivationPolicy>(() => throw new AssertionException("pipeline warmup is disabled")));

        Assert.That(() => step.Execute(CancellationToken.None),
            Throws.TypeOf(portInUse ? typeof(PortInUseException) : typeof(ListenerBindException)));
    }
}

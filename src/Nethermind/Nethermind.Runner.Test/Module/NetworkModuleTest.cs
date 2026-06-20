// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core.Specs;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Init.Steps;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.Discovery;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V71;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Peers;
using Nethermind.Stats.Model;
using Nethermind.TxPool;
using NSubstitute;
using NSubstitute.Core;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

public class NetworkModuleTest
{
    /// <summary>
    /// Protocol handlers are IDisposable transients created by protocol handler factories — one
    /// per peer connection. Their lifetime is owned by Session.Dispose(), not the container.
    /// If handler creation routes through a tracked container registration, they accumulate
    /// on Autofac's internal dispose stack for the application lifetime, leaking memory
    /// proportional to peer churn.
    ///
    /// This test resolves every registered handler factory, creates a handler, disposes it,
    /// and verifies the GC can collect it — proving the container does not retain a reference.
    /// The handler is created in a separate NoInlining method so the local variable goes out
    /// of scope, making it eligible for collection if no external root (like Autofac) holds it.
    /// </summary>
    [Test]
    public void Protocol_handlers_are_not_retained_by_container_after_dispose()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new ConfigProvider()))
            .Build();

        IReadOnlyList<IProtocolHandlerFactory> factories = container.Resolve<IReadOnlyList<IProtocolHandlerFactory>>();
        Assert.That(factories, Is.Not.Empty, "at least one protocol handler factory should be registered");

        foreach (IProtocolHandlerFactory handlerFactory in factories)
        {
            WeakReference weakRef = CreateDisposeAndTrack(handlerFactory);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.That(
                weakRef.IsAlive,
                Is.False,
                $"{handlerFactory.ProtocolCode} handler must not be retained by the container after Dispose");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateDisposeAndTrack(IProtocolHandlerFactory handlerFactory)
    {
        ISession session = new Session(0, new Node(TestItem.PublicKeyA, "127.0.0.1", 30303),
            Substitute.For<DotNetty.Transport.Channels.IChannel>(),
            Substitute.For<IDisconnectsAnalyzer>(),
            LimboLogs.Instance);

        // Try versions 0-100: P2P factory accepts any version,
        // versioned factories match their specific version (e.g. Eth68 = 68)
        for (int version = 0; version <= 100; version++)
        {
            if (handlerFactory.TryCreate(session, version, out IProtocolHandler handler))
            {
                WeakReference weakRef = new(handler);
                handler.Dispose();
                return weakRef;
            }
        }

        Assert.Fail($"Factory for '{handlerFactory.ProtocolCode}' did not create a handler for any version 0-100");
        return null!;
    }

    [Test]
    public void TestAllSerializerInAssemblyRegistered()
    {
        using IContainer cont = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new ConfigProvider()))
            .Build();

        IDictionary<Type, object> serializersInContainer = cont.Resolve<IReadOnlyList<SerializerInfo>>()
            .ToDictionary((info) => info.MessageType, (info) => info.Serializer);

        foreach ((Type MessageType, Type SerializerTypeInAssembly) in FindSerializersInAssembly(Assembly.GetAssembly(typeof(HelloMessageSerializer))))
        {
            if (!serializersInContainer.TryGetValue(MessageType, out object serializer))
            {
                Console.Out.WriteLine($".AddMessageSerializer<{MessageType}, {SerializerTypeInAssembly}>()");
                continue;
            }
            Assert.That(serializer, Is.TypeOf(SerializerTypeInAssembly));
        }
    }

    [Test]
    public void Registers_eth71_protocol_handler_factory()
    {
        using IContainer cont = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new ConfigProvider()))
            .Build();

        ISession session = Substitute.For<ISession>();

        IProtocolHandler handler = cont.Resolve<IProtocolHandlerFactory[]>()
            .Where(static factory => factory.ProtocolCode == Protocol.Eth)
            .Select(factory =>
            {
                factory.TryCreate(session, EthVersions.Eth71, out IProtocolHandler createdHandler);
                return createdHandler;
            })
            .FirstOrDefault(static handler => handler is Eth71ProtocolHandler);

        Assert.That(handler, Is.TypeOf<Eth71ProtocolHandler>());
        handler?.Dispose();
    }

    [Test]
    public async Task Initialize_network_registers_plugin_capabilities_before_starting_rlpx()
    {
        SubstitutionContext.Current?.ThreadContext?.DequeueAllArgumentSpecifications();

        List<string> callOrder = [];
        IRlpxHost rlpxHost = Substitute.For<IRlpxHost>();
        IStaticNodesManager staticNodesManager = Substitute.For<IStaticNodesManager>();
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        IProtocolsManager protocolsManager = Substitute.For<IProtocolsManager>();
        INethermindPlugin plugin = new RecordingPlugin(() => callOrder.Add("plugin"));

        rlpxHost.Init().Returns(_ =>
        {
            callOrder.Add("rlpx");
            return Task.CompletedTask;
        });
        staticNodesManager.InitAsync().Returns(Task.CompletedTask);
        trustedNodesManager.InitAsync().Returns(Task.CompletedTask);

        TestInitializeNetwork initializeNetwork = new(
            rlpxHost,
            staticNodesManager,
            trustedNodesManager,
            protocolsManager,
            [plugin],
            new NetworkConfig { DisableDiscV4DnsFeeder = true },
            new SyncConfig(),
            LimboLogs.Instance);

        await initializeNetwork.RunInitPeer();

        Assert.That(callOrder, Is.EqualTo(new[] { "plugin", "rlpx" }));
        protocolsManager.DidNotReceive().RemoveSupportedCapability(new Capability(Protocol.NodeData, 1));
    }

    private IEnumerable<(Type MessageType, Type SerializerType)> FindSerializersInAssembly(Assembly assembly)
    {
        foreach (Type type in assembly.GetExportedTypes())
        {
            if (!type.IsClass)
            {
                continue;
            }

            Type[] implementedInterfaces = type.GetInterfaces();
            foreach (Type implementedInterface in implementedInterfaces)
            {
                if (!implementedInterface.IsGenericType)
                {
                    continue;
                }

                ConstructorInfo constructor = type.GetConstructor(Type.EmptyTypes);
                if (constructor is null)
                {
                    continue;
                }

                Type interfaceGenericDefinition = implementedInterface.GetGenericTypeDefinition();

                if (interfaceGenericDefinition == typeof(IZeroMessageSerializer<>).GetGenericTypeDefinition())
                {
                    yield return (implementedInterface.GenericTypeArguments[0], type);
                }
            }
        }
    }

    private sealed class RecordingPlugin(Action onInitNetworkProtocol) : INethermindPlugin
    {
        public string Name => nameof(RecordingPlugin);
        public string Description => nameof(RecordingPlugin);
        public string Author => nameof(RecordingPlugin);
        public bool Enabled => true;

        public Task InitNetworkProtocol()
        {
            onInitNetworkProtocol();
            return Task.CompletedTask;
        }
    }

    private sealed class TestInitializeNetwork(
        IRlpxHost rlpxHost,
        IStaticNodesManager staticNodesManager,
        ITrustedNodesManager trustedNodesManager,
        IProtocolsManager protocolsManager,
        INethermindPlugin[] plugins,
        INetworkConfig networkConfig,
        ISyncConfig syncConfig,
        ILogManager logManager) : InitializeNetwork(
                Substitute.For<ISyncServer>(),
                Substitute.For<ISynchronizer>(),
                Substitute.For<ISyncPeerPool>(),
                new NodeSourceToDiscV4Feeder(Substitute.For<INodeSource>(), Substitute.For<IDiscoveryApp>(), Substitute.For<IProcessExitSource>()),
                Substitute.For<IDiscoveryApp>(),
                new Lazy<IPeerPool>(() => Substitute.For<IPeerPool>()),
                Substitute.For<IBlockTree>(),
                rlpxHost,
                Substitute.For<IPeerManager>(),
                Substitute.For<ISessionMonitor>(),
                staticNodesManager,
                trustedNodesManager,
                Substitute.For<IEnode>(),
                plugins,
                new Lazy<IProtocolsManager>(() => protocolsManager),
                new Lazy<SnapCapabilitySwitcher>(() => null!),
                networkConfig,
                syncConfig,
                Substitute.For<IInitConfig>(),
                logManager)
    {
        public Task RunInitPeer() => InitPeer();
    }
}

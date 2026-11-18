// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Autofac;
using DotNetty.Transport.Channels;
using Nethermind.Core.Test.Threading;
using Nethermind.Network.Contract.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Init.Modules;
using Nethermind.Network;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V71;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Logging;
using Nethermind.Stats.Model;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

public class NetworkModuleTest
{
    [Test]
    public void Network_module_registers_the_system_clock_by_default()
    {
        using IContainer container = new ContainerBuilder().AddModule(new NetworkModule(new ConfigProvider())).Build();
        Assert.That(container.Resolve<TimeProvider>(), Is.SameAs(TimeProvider.System));
    }

    [Test]
    public void Production_protocol_factories_use_the_injected_pooled_request_clock(
        [Range(66, 72)] int version, [Values] bool registerClockFirst)
    {
        ManualTimeProvider clock = new();
        ContainerBuilder builder = new();
        if (registerClockFirst) builder.AddSingleton<TimeProvider>(clock);
        builder.AddModule(new TestNethermindModule(new ConfigProvider()));
        if (!registerClockFirst) builder.AddSingleton<TimeProvider>(clock);
        using IContainer container = builder.Build();
        Assert.That(container.Resolve<TimeProvider>(), Is.SameAs(clock));
        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyB, "1.2.3.4", 30303));
        session.IsChannelWritable.Returns(true);
        IChannel channel = Substitute.For<IChannel>();
        channel.DisconnectAsync().Returns(Task.CompletedTask);
        using Session receiptOwner = new(0, session.Node, channel,
            Substitute.For<IDisconnectsAnalyzer>(), LimboLogs.Instance);
        try
        {
            session.InboundReceipts.Returns(((ISession)receiptOwner).InboundReceipts);
            session.DeliverMessage(Arg.Any<P2PMessage>()).Returns(call =>
            {
                call.Arg<P2PMessage>().Dispose();
                return 1;
            });
            IReadOnlyList<IProtocolHandlerFactory> factories = container.Resolve<IReadOnlyList<IProtocolHandlerFactory>>();
            IProtocolHandler handler = null;
            foreach (IProtocolHandlerFactory factory in factories)
            {
                if (factory.ProtocolCode == Protocol.Eth && factory.TryCreate(session, version, out handler)) break;
            }
            Assert.That(handler, Is.Not.Null);
            using (handler)
            {
                Assert.That(clock.DueTimes, Is.Empty);
                ((Eth65ProtocolHandler)handler!).HandleMessage(PooledTransactionRequestMessage.New(TestItem.KeccakA));
                Assert.That(clock.DueTimes, Is.EqualTo(new[] { Timeouts.Eth }), "the production handler must publish its pooled-request timer on the injected clock");
            }
            clock.AdvanceAndFireDueTimers(Timeouts.Eth);
            ((Eth65ProtocolHandler)handler!).HandleMessage(PooledTransactionRequestMessage.New(TestItem.KeccakB));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(clock.DueTimes, Has.Count.EqualTo(1), "the disposed owner must not publish another timer");
                session.Received(1).DeliverMessage(Arg.Any<P2PMessage>());
                session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
            }
        }
        finally
        {
            receiptOwner.MarkDisconnected(DisconnectReason.ClientQuitting, DisconnectType.Local, "test complete");
        }
    }

    [Test]
    public void Network_scheduler_capacity_preserves_the_honest_serving_envelope(
        [Values(0, 7679, 7680, 8192)] int capacity, [Values] bool networking)
    {
        ConfigProvider config = new();
        config.GetConfig<IInitConfig>().BackgroundTaskMaxNumber = capacity;
        config.GetConfig<ISyncConfig>().NetworkingEnabled = networking;
        void Build()
        {
            using IContainer container = new ContainerBuilder().AddModule(new NetworkModule(config)).Build();
        }
        if (networking && capacity < 7680)
        {
            InvalidConfigurationException exception = Assert.Throws<InvalidConfigurationException>(Build)!;
            Assert.That(exception.ExitCode, Is.EqualTo(ExitCodes.ForbiddenOptionValue));
        }
        else Assert.DoesNotThrow(Build);
    }

    [Test]
    public void Resolved_network_scheduler_capacity_is_checked_independently_of_config(
        [Values(7679, 7680, 8192, null)] int? capacity, [Values] bool networking, [Values] bool registerBefore)
    {
        ConfigProvider config = new();
        config.GetConfig<ISyncConfig>().NetworkingEnabled = networking;
        IBackgroundTaskScheduler scheduler = Substitute.For<IBackgroundTaskScheduler>();
        scheduler.QueueCapacity.Returns(capacity);
        ContainerBuilder builder = new();
        if (registerBefore) builder.RegisterInstance(scheduler);
        builder.AddModule(new NetworkModule(config));
        if (!registerBefore) builder.RegisterInstance(scheduler);
        IBackgroundTaskScheduler ResolveScheduler()
        {
            using IContainer container = builder.Build();
            return container.Resolve<IBackgroundTaskScheduler>();
        }
        if (networking && capacity < 7680)
        {
            Autofac.Core.DependencyResolutionException exception = Assert.Throws<Autofac.Core.DependencyResolutionException>(
                () => ResolveScheduler())!;
            Assert.That(exception.GetBaseException(), Is.TypeOf<InvalidConfigurationException>());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(exception.ToString(), Does.Contain("must be at least 7680"));
                Assert.That(((InvalidConfigurationException)exception.GetBaseException()).ExitCode, Is.EqualTo(ExitCodes.ForbiddenOptionValue));
            }
        }
        else Assert.That(ResolveScheduler(), Is.SameAs(scheduler));
    }

    [Test]
    public async Task Default_network_scheduler_capacity_preserves_the_honest_serving_envelope()
    {
        ConfigProvider config = new();
        Assert.That(config.GetConfig<IInitConfig>().BackgroundTaskMaxNumber, Is.EqualTo(8192));
        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new NetworkModule(config))
            .AddModule(new BlockProcessingModule(config.GetConfig<IInitConfig>(), config.GetConfig<IBlocksConfig>()));
        IMainProcessingContext processing = Substitute.For<IMainProcessingContext>();
        processing.BranchProcessor.Returns(Substitute.For<IBranchProcessor>());
        builder.RegisterInstance(processing);
        builder.RegisterInstance(Substitute.For<IChainHeadInfoProvider>());
        builder.RegisterInstance<ILogManager>(LimboLogs.Instance);
        await using IContainer container = builder.Build();
        IBackgroundTaskScheduler scheduler = container.Resolve<IBackgroundTaskScheduler>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(scheduler, Is.TypeOf<BackgroundTaskScheduler>());
            Assert.That(scheduler.QueueCapacity, Is.EqualTo(8192));
        }
    }

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
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));

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

                if (interfaceGenericDefinition == typeof(IMessageSerializer<>))
                {
                    yield return (implementedInterface.GenericTypeArguments[0], type);
                }
            }
        }
    }

}

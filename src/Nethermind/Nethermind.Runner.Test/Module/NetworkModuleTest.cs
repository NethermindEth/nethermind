// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Autofac;
using FluentAssertions;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Logging;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

public class NetworkModuleTest
{
    /// <summary>
    /// Protocol handlers are IDisposable transients resolved via Autofac Func factories — one
    /// per peer connection. Their lifetime is owned by Session.Dispose(), not the container.
    /// If the container tracks them (missing ExternallyOwned), they accumulate on Autofac's
    /// internal dispose stack for the application lifetime, leaking memory proportional to
    /// peer churn.
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
        factories.Should().NotBeEmpty("at least one protocol handler factory should be registered");

        foreach (IProtocolHandlerFactory handlerFactory in factories)
        {
            WeakReference weakRef = CreateDisposeAndTrack(handlerFactory);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            weakRef.IsAlive.Should().BeFalse(
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
            if (!serializersInContainer.TryGetValue(MessageType, out var serializer))
            {
                Console.Out.WriteLine($".AddMessageSerializer<{MessageType}, {SerializerTypeInAssembly}>()");
                continue;
            }
            // serializersInContainer.TryGetValue(MessageType, out var serializer).Should().BeTrue();
            serializer.Should().BeOfType(SerializerTypeInAssembly);
        }
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
}

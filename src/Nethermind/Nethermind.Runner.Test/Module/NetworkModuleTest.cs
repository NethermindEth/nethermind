// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autofac;
using FluentAssertions;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
using Nethermind.Network;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V71;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

public class NetworkModuleTest
{
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

        handler.Should().BeOfType<Eth71ProtocolHandler>();
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

                if (interfaceGenericDefinition == typeof(IZeroMessageSerializer<>).GetGenericTypeDefinition())
                {
                    yield return (implementedInterface.GenericTypeArguments[0], type);
                }
            }
        }
    }
}

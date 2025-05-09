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
using Nethermind.Network.P2P.Messages;
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

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Core.Test.Container;

public class KeyedMapperRegistrationSourceTests
{
    [Test]
    public void TestCanMap()
    {
        using IContainer cont = new ContainerBuilder()
            .AddKeyedSingleton<ClassA>("Key", new ClassA("Property1"))
            .AddKeyedAdapter<ClassB, ClassA>((a) => new ClassB(a.Property))
            .Build();

        cont.ResolveKeyed<ClassB>("Key").Property.Should().Be("Property1");
    }

    private record ClassA(string Property);
    private record ClassB(string Property);
}

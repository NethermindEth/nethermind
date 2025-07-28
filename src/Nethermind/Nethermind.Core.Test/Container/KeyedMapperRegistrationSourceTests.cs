// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

    [TestCase(true)]
    [TestCase(false)]
    public void TestDisposeWhenNotOwned(bool shouldDispose)
    {
        bool adapterWasDisposed = false;

        var builder = new ContainerBuilder()
            .AddKeyedSingleton<ClassA>("Key", new ClassA("Property1"));

        if (shouldDispose)
        {
            builder.AddKeyedAdapterWithNewService<ClassB, ClassA>((a) => new ClassB(a.Property, () => adapterWasDisposed = true));
        }
        else
        {
            builder.AddKeyedAdapter<ClassB, ClassA>((a) => new ClassB(a.Property, () => adapterWasDisposed = true));
        }

        IContainer cont = builder.Build();

        cont.ResolveKeyed<ClassB>("Key").Property.Should().Be("Property1");

        cont.Dispose();

        adapterWasDisposed.Should().Be(shouldDispose);
    }

    private record ClassA(string Property);

    private record ClassB(string Property, Action? onDispose = null) : IDisposable
    {
        public void Dispose()
        {
            onDispose?.Invoke();
        }
    }
}

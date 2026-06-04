// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
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

        Assert.That(cont.ResolveKeyed<ClassB>("Key").Property, Is.EqualTo("Property1"));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void TestDisposeWhenNotOwned(bool shouldDispose)
    {
        bool adapterWasDisposed = false;

        ContainerBuilder builder = new ContainerBuilder()
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

        Assert.That(cont.ResolveKeyed<ClassB>("Key").Property, Is.EqualTo("Property1"));

        cont.Dispose();

        Assert.That(adapterWasDisposed, Is.EqualTo(shouldDispose));
    }

    private record ClassA(string Property);

    private record ClassB(string Property, Action? onDispose = null) : IDisposable
    {
        public void Dispose() => onDispose?.Invoke();
    }
}

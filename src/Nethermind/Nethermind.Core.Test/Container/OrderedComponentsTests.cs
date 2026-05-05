// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Autofac;
using FluentAssertions;
using Nethermind.Core.Container;
using NUnit.Framework;

namespace Nethermind.Core.Test.Container;

public class OrderedComponentsTests
{
    [Test]
    public void TestNestedModuleConsistency()
    {
        using IContainer ctx = new ContainerBuilder()
            .AddModule(new ModuleA())
            .AddLast(_ => new Item("4"))
            .Build();

        ctx.Resolve<Item[]>().Select(item => item.Name).Should().BeEquivalentTo(["1", "2", "3", "4"]);
        ctx.Resolve<IEnumerable<Item>>().Select(item => item.Name).Should().BeEquivalentTo(["1", "2", "3", "4"]);
        ctx.Resolve<IReadOnlyList<Item>>().Select(item => item.Name).Should().BeEquivalentTo(["1", "2", "3", "4"]);
    }

    [Test]
    public void TestAddFirst()
    {
        using IContainer ctx = new ContainerBuilder()
            .AddLast(_ => new Item("2"))
            .AddLast(_ => new Item("3"))
            .AddFirst(_ => new Item("1"))
            .Build();

        ctx.Resolve<Item[]>().Select(item => item.Name).Should().BeEquivalentTo(["1", "2", "3"]);
    }

    [Test]
    public void TestDisallowIndividualRegistration()
    {
        Action act = () => new ContainerBuilder()
            .AddLast(_ => new Item("1"))
            .AddSingleton<Item>(_ => new Item("2"))
            .Build();

        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void TestCompositeResolved_EvenWhenNotLastRegistration()
    {
        // AddComposite is called before the last AddLast — composite should still be resolved as IItem
        using IContainer ctx = new ContainerBuilder()
            .AddLast<IItem>(_ => new Item("1"))
            .AddCompositeOrderedComponents<IItem, CompositeItem>()
            .AddLast<IItem>(_ => new Item("2"))
            .Build();

        IItem resolved = ctx.Resolve<IItem>();
        resolved.Should().BeOfType<CompositeItem>();
        ((CompositeItem)resolved).Items.Select(i => i.Name).Should().BeEquivalentTo(["1", "2"]);
    }

    [Test]
    public void TestCompositeResolved_WhenCalledBeforeAnyAddLast()
    {
        // AddComposite is called before any AddLast — should still work
        using IContainer ctx = new ContainerBuilder()
            .AddCompositeOrderedComponents<IItem, CompositeItem>()
            .AddLast<IItem>(_ => new Item("1"))
            .AddLast<IItem>(_ => new Item("2"))
            .Build();

        IItem resolved = ctx.Resolve<IItem>();
        resolved.Should().BeOfType<CompositeItem>();
        ((CompositeItem)resolved).Items.Select(i => i.Name).Should().BeEquivalentTo(["1", "2"]);
    }

    private class ModuleA : Module
    {
        protected override void Load(ContainerBuilder builder) =>
            builder
                .AddModule(new ModuleB())
                .AddLast(_ => new Item("3"));
    }

    private class ModuleB : Module
    {
        protected override void Load(ContainerBuilder builder) =>
            builder
                .AddModule(new ModuleC())
                .AddLast(_ => new Item("2"));
    }


    private class ModuleC : Module
    {
        protected override void Load(ContainerBuilder builder) =>
            builder
                .AddLast(_ => new Item("1"));
    }
    private interface IItem { string Name { get; } }
    private record Item(string Name) : IItem;
    private class CompositeItem(IItem[] items) : IItem
    {
        public IItem[] Items { get; } = items;
        public string Name => string.Join(",", Items.Select(i => i.Name));
    }
}

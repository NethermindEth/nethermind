// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Autofac.Core;
using FluentAssertions;
using Nethermind.Core.Container;
using Nethermind.Core.Exceptions;
using NUnit.Framework;

namespace Nethermind.Core.Test.Container;

public class FallbackToFieldFromApiTests
{

    [Test]
    public void CanResolveFieldWithTypeWhenSetLater()
    {
        ContainerBuilder containerBuilder = new ContainerBuilder();
        containerBuilder.AddSingleton<Api>();
        containerBuilder.RegisterSource(new FallbackToFieldFromApi<Api>());

        IContainer container = containerBuilder.Build();

        Action act = (() => container.Resolve<TargetService>());
        act.Should().Throw<DependencyResolutionException>();

        container.Resolve<Api>().TargetService = new TargetService();
        container.Resolve<TargetService>().Should().NotBeNull();
    }

    [Test]
    public void CanResolveFieldWithKey()
    {
        ContainerBuilder containerBuilder = new ContainerBuilder();
        containerBuilder.AddSingleton<IApi, Api>();
        containerBuilder.RegisterSource(new FallbackToFieldFromApi<IApi>());

        IContainer container = containerBuilder.Build();
        container.Resolve<Api>().NamedTargetService = new NamedTargetService();
        container.TryResolve(out NamedTargetService? _).Should().BeFalse();
        container.TryResolveKeyed(ComponentKey.NodeKey, out NamedTargetService? _).Should().BeFalse();
    }

    [Test]
    public void ThrowExceptionIfTargetIsAlsoRegisterec()
    {
        ContainerBuilder containerBuilder = new ContainerBuilder();
        containerBuilder.AddSingleton<Api>();
        containerBuilder.AddSingleton<TargetService>();
        containerBuilder.RegisterSource(new FallbackToFieldFromApi<Api>());

        IContainer container = containerBuilder.Build();

        Action act = (() => container.Resolve<TargetService>());
        act.Should().Throw<InvalidConfigurationException>();
    }

    public interface IApi
    {
        [ComponentKey(ComponentKey.NodeKey)]
        public NamedTargetService NamedTargetService { get; set; }
        public TargetService TargetService { get; set; }
    }

    public class Api: IApi
    {
        public NamedTargetService NamedTargetService { get; set; } = null!;
        public TargetService TargetService { get; set; } = null!;
    }

    public class TargetService
    {
    }

    public class NamedTargetService
    {
    }

}

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
    public void ThrowExceptionIfTargetIsAlsoRegistered()
    {
        ContainerBuilder containerBuilder = new ContainerBuilder();
        containerBuilder.AddSingleton<Api>();
        containerBuilder.AddSingleton<TargetService>();
        containerBuilder.RegisterSource(new FallbackToFieldFromApi<Api>());

        IContainer container = containerBuilder.Build();

        Action act = (() => container.Resolve<TargetService>());
        act.Should().Throw<InvalidConfigurationException>();
    }

    [TestCase(true)]
    [TestCase(false)]
    public void OnlyRegisterFieldInInterfaceByDefault(bool interfaceOnly)
    {
        ContainerBuilder containerBuilder = new ContainerBuilder();
        containerBuilder.AddSingleton<Api>();
        containerBuilder.RegisterSource(new FallbackToFieldFromApi<Api>(interfaceOnly: interfaceOnly));

        IContainer container = containerBuilder.Build();
        container.Resolve<Api>().TargetService = new TargetService();
        container.Resolve<Api>().TargetServiceInImplementation = new TargetServiceInImplementation();

        container.ResolveOptional<TargetService>().Should().NotBeNull();

        if (interfaceOnly)
        {
            container.ResolveOptional<TargetServiceInImplementation>().Should().BeNull();
        }
        else
        {
            container.ResolveOptional<TargetServiceInImplementation>().Should().NotBeNull();
        }
    }

    public interface IApi
    {
        public TargetService TargetService { get; set; }
    }

    public class Api : IApi
    {
        public NamedTargetService NamedTargetService { get; set; } = null!;
        public TargetService TargetService { get; set; } = null!;
        public TargetServiceInImplementation TargetServiceInImplementation { get; set; } = null!;
    }

    public class TargetService
    {
    }

    public class TargetServiceInImplementation
    {
    }

    public class NamedTargetService
    {
    }

}

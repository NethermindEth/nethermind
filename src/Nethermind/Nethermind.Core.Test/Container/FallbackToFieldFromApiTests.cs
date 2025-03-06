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

    [TestCase(false)]
    [TestCase(true)]
    public void ThrowExceptionIfTargetIsAlsoRegistered(bool allowRedundantRegistrations)
    {
        ContainerBuilder containerBuilder = new ContainerBuilder();
        containerBuilder.AddSingleton<Api>();
        containerBuilder.AddSingleton<TargetService>();
        containerBuilder.RegisterSource(new FallbackToFieldFromApi<Api>(allowRedundantRegistration: allowRedundantRegistrations));

        IContainer container = containerBuilder.Build();

        Action act = (() => container.Resolve<TargetService>());
        if (allowRedundantRegistrations)
        {
            act.Should().NotThrow<InvalidConfigurationException>();
        }
        else
        {
            act.Should().Throw<InvalidConfigurationException>();
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void OnlyRegisterFieldDirectlyDeclared(bool directlyDeclaredOnly)
    {
        ContainerBuilder containerBuilder = new ContainerBuilder();
        containerBuilder.AddSingleton<Api2>();
        containerBuilder.RegisterSource(new FallbackToFieldFromApi<Api2>(directlyDeclaredOnly: directlyDeclaredOnly));

        IContainer container = containerBuilder.Build();
        container.Resolve<Api2>().TargetService = new TargetService();

        if (directlyDeclaredOnly)
        {
            container.ResolveOptional<TargetService>().Should().BeNull();
        }
        else
        {
            container.ResolveOptional<TargetService>().Should().NotBeNull();
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
    }

    public class Api2 : Api
    {
    }

    public class TargetService
    {
    }

    public class NamedTargetService
    {
    }

}

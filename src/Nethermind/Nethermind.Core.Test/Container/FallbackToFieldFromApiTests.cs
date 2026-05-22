// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Autofac.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Exceptions;
using NUnit.Framework;

namespace Nethermind.Core.Test.Container;

public class FallbackToFieldFromApiTests
{

    [Test]
    public void CanResolveFieldWithTypeWhenSetLater()
    {
        ContainerBuilder containerBuilder = new();
        containerBuilder.AddSingleton<Api>();
        containerBuilder.RegisterSource(new FallbackToFieldFromApi<Api>());

        IContainer container = containerBuilder.Build();

        Action act = (() => container.Resolve<TargetService>());
        Assert.That(act, Throws.TypeOf<DependencyResolutionException>());

        container.Resolve<Api>().TargetService = new TargetService();
        Assert.That(container.Resolve<TargetService>(), Is.Not.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ThrowExceptionIfTargetIsAlsoRegistered(bool allowRedundantRegistrations)
    {
        ContainerBuilder containerBuilder = new();
        containerBuilder.AddSingleton<Api>();
        containerBuilder.AddSingleton<TargetService>();
        containerBuilder.RegisterSource(new FallbackToFieldFromApi<Api>(allowRedundantRegistration: allowRedundantRegistrations));

        IContainer container = containerBuilder.Build();

        Action act = (() => container.Resolve<TargetService>());
        if (allowRedundantRegistrations)
        {
            Assert.That(act, Throws.Nothing);
        }
        else
        {
            Assert.That(act, Throws.TypeOf<InvalidConfigurationException>());
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void OnlyRegisterFieldDirectlyDeclared(bool directlyDeclaredOnly)
    {
        ContainerBuilder containerBuilder = new();
        containerBuilder.AddSingleton<Api2>();
        containerBuilder.RegisterSource(new FallbackToFieldFromApi<Api2>(directlyDeclaredOnly: directlyDeclaredOnly));

        IContainer container = containerBuilder.Build();
        container.Resolve<Api2>().TargetService = new TargetService();

        if (directlyDeclaredOnly)
        {
            Assert.That(container.ResolveOptional<TargetService>(), Is.Null);
        }
        else
        {
            Assert.That(container.ResolveOptional<TargetService>(), Is.Not.Null);
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

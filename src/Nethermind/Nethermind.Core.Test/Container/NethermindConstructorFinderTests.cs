// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Autofac.Core;
using Nethermind.Core.Container;
using NUnit.Framework;

namespace Nethermind.Core.Test.Container;

public class NethermindConstructorFinderTests
{
    private class ServiceA;
    private class ServiceB;

    private class ServiceC
    {
        internal bool UsedServiceA = false;

        public ServiceC(ServiceB serviceB)
        {
        }

        [UseConstructorForDependencyInjection]
        public ServiceC(ServiceA serviceA)
        {
            UsedServiceA = true;
        }
    }

    private class ServiceD
    {
        public ServiceD(ServiceB serviceB)
        {
        }

        public ServiceD(ServiceA serviceA)
        {
        }
    }

    [Test]
    public void UseConstructorFinderWhenApplied()
    {
        using IContainer container = new ContainerBuilder()
            .AddSingleton<ServiceA>()
            .AddSingleton<ServiceB>()
            .AddSingleton<ServiceC>()
            .AddSingleton<ServiceD>()
            .Build();

        Assert.That(() => container.Resolve<ServiceC>().UsedServiceA, Is.True);
        Assert.That(() => container.Resolve<ServiceD>(), Throws.InstanceOf<DependencyResolutionException>());
    }
}

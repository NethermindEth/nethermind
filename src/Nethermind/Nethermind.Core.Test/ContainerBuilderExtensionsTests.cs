// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class ContainerBuilderExtensionsTests
{
    [Test]
    public void TestRegisterNamedComponent()
    {
        IContainer sp = new ContainerBuilder()
            .AddScoped<MainComponent>()
            .AddScoped<MainComponentDependency>()
            .RegisterNamedComponentInItsOwnLifetime<MainComponent>("custom", static cfg =>
            {
                // Override it in custom
                cfg.AddScoped<MainComponentDependency, MainComponentDependencySubClass>();
            })
            .Build();

        using (ILifetimeScope scope = sp.BeginLifetimeScope())
        {
            scope.Resolve<MainComponent>().Property.Should().BeOfType<MainComponentDependency>();
        }

        MainComponentDependency customMainComponentDependency = sp.ResolveNamed<MainComponent>("custom").Property;
        sp.ResolveNamed<MainComponent>("custom").Property.Should().BeOfType<MainComponentDependencySubClass>();

        sp.Dispose();

        customMainComponentDependency.WasDisposed.Should().BeTrue();
    }

    private class MainComponent(MainComponentDependency mainComponentDependency, ILifetimeScope scope) : IDisposable
    {
        public MainComponentDependency Property => mainComponentDependency;

        public void Dispose()
        {
            scope.Dispose();
        }
    }

    private class MainComponentDependency : IDisposable
    {
        public bool WasDisposed { get; set; }

        public void Dispose()
        {
            WasDisposed = true;
        }
    }

    private class MainComponentDependencySubClass : MainComponentDependency
    {
    }

    private class InterfaceImplementation : ITestInterface
    {
        public DeclaredService TheService { get; set; } = new DeclaredService();
        public DeclaredButNullService? NullService { get; set; } = null;
        public Ignored IgnoredService { get; set; } = new Ignored();
        public DeclaredInBase BaseService { get; set; } = new DeclaredInBase();
    }

    private interface ITestInterface : ITestInterfaceBase
    {
        DeclaredService TheService { get; set; }
        DeclaredButNullService? NullService { get; set; }

        [SkipServiceCollection]
        Ignored IgnoredService { get; set; }
    }

    private interface ITestInterfaceBase
    {
        DeclaredInBase BaseService { get; set; }
    }

    private class DeclaredInBase { }
    private class DeclaredService { }
    private class DeclaredButNullService { }
    private class Ignored { }

    private class DisposableService : IDisposable
    {
        public bool WasDisposed { get; set; } = false;

        public void Dispose()
        {
            WasDisposed = true;
        }
    }
}

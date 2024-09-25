// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class ServiceCollectionExtensionsTests
{
    [Test]
    public void AddPropertiesFrom_CanAddProperties()
    {
        ITestInterface interfaceImplementation = new InterfaceImplementation();
        IServiceProvider sp = new ServiceCollection()
            .AddPropertiesFrom(interfaceImplementation)
            .BuildServiceProvider();

        sp.GetService<DeclaredService>().Should().NotBeNull();
        sp.GetService<DeclaredInBase>().Should().BeNull();
        sp.GetService<Ignored>().Should().BeNull();
        sp.GetService<DeclaredButNullService>().Should().BeNull();
    }

    [Test]
    public void TestForwardDependency_ShouldNotDispose()
    {
        ServiceProvider sp1 = new ServiceCollection()
            .AddSingleton<DisposableService>()
            .BuildServiceProvider();

        ServiceProvider sp2 = new ServiceCollection()
            .ForwardServiceAsSingleton<DisposableService>(sp1)
            .BuildServiceProvider();

        DisposableService disposableService = sp2.GetRequiredService<DisposableService>();
        disposableService.WasDisposed.Should().BeFalse();

        sp2.Dispose();
        disposableService.WasDisposed.Should().BeFalse();

        sp1.Dispose();
        disposableService.WasDisposed.Should().BeTrue();
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

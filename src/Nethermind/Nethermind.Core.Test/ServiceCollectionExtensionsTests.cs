// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
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
        IContainer sp = new ContainerBuilder()
            .AddPropertiesFrom(interfaceImplementation)
            .Build();

        sp.ResolveOptional<DeclaredService>().Should().NotBeNull();
        sp.ResolveOptional<DeclaredInBase>().Should().BeNull();
        sp.ResolveOptional<Ignored>().Should().BeNull();
        sp.ResolveOptional<DeclaredButNullService>().Should().BeNull();
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

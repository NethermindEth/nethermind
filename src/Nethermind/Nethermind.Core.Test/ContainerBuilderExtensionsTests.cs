// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
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
            Assert.That(scope.Resolve<MainComponent>().Property, Is.TypeOf<MainComponentDependency>());
        }

        MainComponentDependency customMainComponentDependency = sp.ResolveNamed<MainComponent>("custom").Property;
        Assert.That(sp.ResolveNamed<MainComponent>("custom").Property, Is.TypeOf<MainComponentDependencySubClass>());

        sp.Dispose();

        Assert.That(customMainComponentDependency.WasDisposed, Is.True);
    }

    private class MainComponent(MainComponentDependency mainComponentDependency, ILifetimeScope scope) : IDisposable
    {
        public MainComponentDependency Property => mainComponentDependency;

        public void Dispose() => scope.Dispose();
    }

    private class MainComponentDependency : IDisposable
    {
        public bool WasDisposed { get; set; }

        public void Dispose() => WasDisposed = true;
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

        public void Dispose() => WasDisposed = true;
    }

    // Documents the Autofac behavior the BAL withdrawal wiring relies on: a decorator registered in
    // an ancestor scope is applied to resolutions in a descendant scope — even when the descendant
    // re-registers the service. This mirrors the block-producer env decorating IWithdrawalProcessor
    // with BlockProductionWithdrawalProcessor: the BAL env (a child scope, via
    // AutofacBalProcessingEnvFactory) resolves an already-decorated IWithdrawalProcessor when
    // building, so BlockAccessListManager must NOT add its own production wrap (that would
    // double-wrap). It also means re-registering IWithdrawalProcessor in the child would not escape
    // the decorator.
    [Test]
    public void Decorator_in_ancestor_scope_applies_to_descendant_scope()
    {
        IContainer root = new ContainerBuilder()
            .AddScoped<IDecoratedProbe, ProbeBase>()
            .Build();

        // Producer-like scope: adds the decorator (as GlobalWorldStateBlockProducerEnvFactory does).
        using ILifetimeScope producer = root.BeginLifetimeScope(b => b.AddDecorator<IDecoratedProbe, ProbeDecorator>());
        Assert.That(producer.Resolve<IDecoratedProbe>(), Is.TypeOf<ProbeDecorator>(), "producer scope decorates");

        // BAL-env-like child scope resolving the inherited service is still decorated.
        using ILifetimeScope balEnv = producer.BeginLifetimeScope();
        Assert.That(balEnv.Resolve<IDecoratedProbe>(), Is.TypeOf<ProbeDecorator>(),
            "the descendant resolution inherits the ancestor decorator, so the BAL env's withdrawal processor is already production-wrapped when building");

        // Even a descendant re-registration is re-decorated (so re-registering would not escape it).
        using ILifetimeScope reRegistered = producer.BeginLifetimeScope(b => b.AddScoped<IDecoratedProbe, ProbeReRegistered>());
        Assert.That(reRegistered.Resolve<IDecoratedProbe>(), Is.TypeOf<ProbeDecorator>(),
            "a descendant re-registration is still re-decorated by the ancestor decorator");
    }

    private interface IDecoratedProbe;
    private class ProbeBase : IDecoratedProbe;
    private class ProbeReRegistered : IDecoratedProbe;
    private class ProbeDecorator(IDecoratedProbe inner) : IDecoratedProbe
    {
        public IDecoratedProbe Inner => inner;
    }
}

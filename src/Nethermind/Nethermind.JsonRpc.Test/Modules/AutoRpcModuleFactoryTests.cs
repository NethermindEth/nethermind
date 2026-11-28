// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Autofac.Core;
using Nethermind.JsonRpc.Modules;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

[TestFixture]
public class AutoRpcModuleFactoryTests
{
    [Test]
    public void Failed_resolution_disposes_dependencies_and_retry_retains_only_successful_scope()
    {
        int disposed = 0;
        int attempts = 0;
        ContainerBuilder builder = new();
        builder.Register(_ => new Dependency(() => disposed++)).InstancePerLifetimeScope();
        builder.Register<IRpcModule>(context =>
        {
            context.Resolve<Dependency>();
            if (++attempts == 1) throw new InvalidOperationException("Failed module initialization");
            return new Module();
        }).InstancePerLifetimeScope();

        IContainer root = builder.Build();
        try
        {
            AutoRpcModuleFactory<IRpcModule> factory = new(root);
            using (Assert.EnterMultipleScope())
            {
                Assert.Throws<DependencyResolutionException>(() => factory.Create());
                Assert.That(disposed, Is.EqualTo(1));
                Assert.That(factory.Create(), Is.TypeOf<Module>());
                Assert.That(disposed, Is.EqualTo(1));
            }
        }
        finally
        {
            root.Dispose();
        }
        Assert.That(disposed, Is.EqualTo(2));
    }

    private sealed class Dependency(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    private sealed class Module : IRpcModule
    {
    }
}

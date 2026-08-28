// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Nethermind.Core.ServiceStopper;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class ServiceStopperTests
{
    [Test]
    public async Task CanStopServices()
    {
        await using IContainer container = new ContainerBuilder()
            .AddServiceStopper()
            .AddSingleton<Service1>()
            .AddSingleton<Service2>()
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .Build();

        Service1 service1 = container.Resolve<Service1>();
        Service2 service2 = container.Resolve<Service2>();
        await container.Resolve<IServiceStopper>().StopAllServices();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(service1.Stopped, Is.True);
            Assert.That(service2.Stopped, Is.True);
        }
    }

    [Test]
    public async Task StopSingletonOnly()
    {
        await using IContainer container = new ContainerBuilder()
            .AddServiceStopper()
            .AddSingleton<Service1>()
            .Add<Service2>()
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .Build();

        Service1 service1 = container.Resolve<Service1>();
        Service2 service2 = container.Resolve<Service2>();
        await container.Resolve<IServiceStopper>().StopAllServices();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(service1.Stopped, Is.True);
            Assert.That(service2.Stopped, Is.False);
        }
    }

    internal class Service1 : IStoppableService
    {
        internal bool Stopped { get; set; }

        public Task StopAsync()
        {
            Stopped = true;
            return Task.CompletedTask;
        }
    }

    internal class Service2 : IStoppableService
    {
        internal bool Stopped { get; set; }

        public Task StopAsync()
        {
            Stopped = true;
            return Task.CompletedTask;
        }
    }
}

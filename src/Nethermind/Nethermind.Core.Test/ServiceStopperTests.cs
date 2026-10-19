// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
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

    [Test]
    public async Task StopsEveryConcurrentRegistrationOnce([Values] bool registerAfterStop)
    {
        // Init steps run in parallel and register stoppables from several threads at once.
        const int count = 10_000;
        await using IContainer container = new ContainerBuilder()
            .AddServiceStopper()
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .Build();
        IServiceStopper stopper = container.Resolve<IServiceStopper>();
        CountingService[] services = Enumerable.Range(0, count).Select(static _ => new CountingService()).ToArray();

        if (registerAfterStop) await stopper.StopAllServices();
        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount) },
            i => stopper.AddStoppable(services[i]));
        if (!registerAfterStop) await stopper.StopAllServices();

        Assert.That(() => services.Select(static s => s.StopCount), Is.All.EqualTo(1).After(5000, 10));
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

    private class CountingService : IStoppableService
    {
        private int _stopCount;

        internal int StopCount => Volatile.Read(ref _stopCount);

        public Task StopAsync()
        {
            Interlocked.Increment(ref _stopCount);
            return Task.CompletedTask;
        }
    }
}

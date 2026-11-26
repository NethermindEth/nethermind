// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Init.Modules;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

public class BlockProcessingModuleTests
{
    [Test]
    public void Runner_is_created_only_when_production_is_enabled_and_a_producer_exists([Values] bool enabled, [Values] bool hasProducer)
    {
        bool producing = enabled && hasProducer;
        IBlockProducer producer = Substitute.For<IBlockProducer>();
        IBlockProducerRunner runner = Substitute.For<IBlockProducerRunner, IDisposable>();
        IBlockProducerFactory producerFactory = Substitute.For<IBlockProducerFactory>();
        IBlockProducerRunnerFactory runnerFactory = Substitute.For<IBlockProducerRunnerFactory>();
        producerFactory.InitBlockProducer().Returns(hasProducer ? producer : null);
        runnerFactory.InitBlockProducerRunner(producer).Returns(runner);

        using (IContainer container = new ContainerBuilder()
            .AddModule(new BlockProcessingModule(new InitConfig(), new BlocksConfig()))
            .AddSingleton<IBlockProductionPolicy>(enabled ? AlwaysStartBlockProductionPolicy.Instance : NeverStartBlockProductionPolicy.Instance)
            .AddSingleton(producerFactory)
            .AddSingleton(runnerFactory)
            .Build())
        {
            IBlockProducerRunner resolved = container.Resolve<IBlockProducerRunner>();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(resolved, producing ? Is.SameAs(runner) : Is.TypeOf<NoBlockProducerRunner>());
                Assert.That(container.Resolve<IBlockProducerRunner>(), Is.SameAs(resolved));
                producerFactory.Received(enabled ? 1 : 0).InitBlockProducer();
                runnerFactory.Received(producing ? 1 : 0).InitBlockProducerRunner(Arg.Any<IBlockProducer>());
            }
        }

        ((IDisposable)runner).Received(producing ? 1 : 0).Dispose();
    }

    [Test]
    public void Runner_uses_the_registered_producer_and_each_producer_is_disposed_once([Values] bool overridden)
    {
        IBlockProducer factoryProducer = Substitute.For<IBlockProducer, IDisposable>();
        IBlockProducer overrideProducer = Substitute.For<IBlockProducer, IDisposable>();
        IBlockProducer expected = overridden ? overrideProducer : factoryProducer;
        IBlockProducerFactory producerFactory = Substitute.For<IBlockProducerFactory>();
        IBlockProducerRunnerFactory runnerFactory = Substitute.For<IBlockProducerRunnerFactory>();
        producerFactory.InitBlockProducer().Returns(factoryProducer);

        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new BlockProcessingModule(new InitConfig(), new BlocksConfig()))
            .AddSingleton<IBlockProductionPolicy>(AlwaysStartBlockProductionPolicy.Instance)
            .AddSingleton(producerFactory)
            .AddSingleton(runnerFactory);
        if (overridden) builder.AddSingleton<IBlockProducer>(_ => overrideProducer);

        using (IContainer container = builder.Build())
        {
            container.Resolve<IBlockProducerRunner>();
            runnerFactory.Received(1).InitBlockProducerRunner(expected);
        }

        using (Assert.EnterMultipleScope())
        {
            ((IDisposable)factoryProducer).Received(1).Dispose();
            ((IDisposable)overrideProducer).Received(overridden ? 1 : 0).Dispose();
        }
    }
}

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
    public void Runner_resolved_before_startup_obeys_the_production_policy([Values] bool enabled)
    {
        IBlockProducer producer = Substitute.For<IBlockProducer>();
        IBlockProducerRunner runner = Substitute.For<IBlockProducerRunner, IDisposable>();
        IBlockProducerFactory producerFactory = Substitute.For<IBlockProducerFactory>();
        IBlockProducerRunnerFactory runnerFactory = Substitute.For<IBlockProducerRunnerFactory>();
        producerFactory.InitBlockProducer().Returns(producer);
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
                Assert.That(resolved, enabled ? Is.SameAs(runner) : Is.TypeOf<NoBlockProducerRunner>());
                Assert.That(container.Resolve<IBlockProducerRunner>(), Is.SameAs(resolved));
                producerFactory.Received(enabled ? 1 : 0).InitBlockProducer();
                runnerFactory.Received(enabled ? 1 : 0).InitBlockProducerRunner(Arg.Any<IBlockProducer>());
            }
        }

        ((IDisposable)runner).DidNotReceive().Dispose();
    }
}

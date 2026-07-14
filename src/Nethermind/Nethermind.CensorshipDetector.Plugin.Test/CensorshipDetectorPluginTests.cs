// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.CensorshipDetector.Plugin.Test;

public class CensorshipDetectorPluginTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void Plugin_enabled_follows_config(bool enabled)
    {
        CensorshipDetectorPlugin plugin = new(new CensorshipDetectorConfig { Enabled = enabled });

        Assert.That(plugin.Enabled, Is.EqualTo(enabled));
    }

    [Test]
    public void Module_registers_one_detector_and_orders_initialization()
    {
        IBranchProcessor branchProcessor = Substitute.For<IBranchProcessor>();
        IMainProcessingContext mainProcessingContext = Substitute.For<IMainProcessingContext>();
        mainProcessingContext.BranchProcessor.Returns(branchProcessor);
        ITransactionComparerProvider comparerProvider = Substitute.For<ITransactionComparerProvider>();
        comparerProvider.GetDefaultComparer().Returns(Substitute.For<IComparer<Transaction>>());

        IContainer container = new ContainerBuilder()
            .AddModule(new CensorshipDetectorModule())
            .AddSingleton(Substitute.For<IBlockTree>())
            .AddSingleton(Substitute.For<ITxPool>())
            .AddSingleton(comparerProvider)
            .AddSingleton(mainProcessingContext)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<ICensorshipDetectorConfig>(new CensorshipDetectorConfig())
            .Build();

        CensorshipDetector detector = container.Resolve<CensorshipDetector>();
        IBuilderOverridePolicy policy = container.Resolve<IBuilderOverridePolicy>();
        StepInfo step = new(typeof(InitializeCensorshipDetector));
        branchProcessor.Received(1).BlockProcessing += Arg.Any<EventHandler<BlockEventArgs>>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(policy, Is.SameAs(detector));
            Assert.That(step.Dependencies, Is.EqualTo(new[] { typeof(InitializeBlockchain) }));
            Assert.That(step.Dependents, Is.EqualTo(new[] { typeof(StartBlockProcessor) }));
        }

        container.Dispose();
        branchProcessor.Received(1).BlockProcessing -= Arg.Any<EventHandler<BlockEventArgs>>();
    }
}

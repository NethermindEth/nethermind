// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Autofac;
using FluentAssertions;
using Nethermind.Api;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Producers;

public class DevBlockProducerTests
{
    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Test()
    {
        ISpecProvider specProvider = MainnetSpecProvider.Instance;
        BlockTree blockTree = Build.A.BlockTree()
            .WithoutSettingHead
            .TestObject;

        ManualTimestamper timestamper = new ManualTimestamper();
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton<ITimestamper>(timestamper)
            .AddSingleton<IBlockValidator>(Always.Valid)
            .AddSingleton<IBlockProducerTxSourceFactory, EmptyTxSourceFactory>()
            .AddScoped<IBlockProducerFactory, TestBlockProcessingModule.AutoBlockProducerFactory<DevBlockProducer>>()
            .AddSingleton<ISpecProvider>(specProvider)
            .AddSingleton<IBlockTree>(blockTree)
            .Build();

        IManualBlockProductionTrigger trigger = container.Resolve<IManualBlockProductionTrigger>();

        container.Resolve<IMainProcessingContext>().BlockchainProcessor.Start();
        container.Resolve<IBlockProducerRunner>().Start();

        AutoResetEvent autoResetEvent = new(false);

        blockTree.NewHeadBlock += (_, _) => autoResetEvent.Set();
        blockTree.SuggestBlock(Build.A.Block.Genesis.TestObject);

        autoResetEvent.WaitOne(1000).Should().BeTrue("genesis");

        trigger.BuildBlock();
        autoResetEvent.WaitOne(1000).Should().BeTrue("1");
        blockTree.Head!.Number.Should().Be(1);
    }

    private class EmptyTxSourceFactory : IBlockProducerTxSourceFactory
    {
        public ITxSource Create()
        {
            return EmptyTxSource.Instance;
        }
    }
}

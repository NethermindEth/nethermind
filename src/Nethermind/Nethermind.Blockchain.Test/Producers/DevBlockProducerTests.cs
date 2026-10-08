// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Producers;

[Parallelizable(ParallelScope.All)]
public class DevBlockProducerTests
{
    private const int NewHeadWaitMs = 5_000;

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task Test()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Cancun.Instance))
            .AddSingleton<IBlockValidator>(Always.Valid)
            .AddSingleton<IBlockProducerTxSourceFactory, EmptyTxSourceFactory>()
            .AddScoped<IBlockProducerFactory, TestBlockProcessingModule.AutoBlockProducerFactory<DevBlockProducer>>()
            .Build();

        IBlockTree blockTree = container.Resolve<IBlockTree>();
        AutoResetEvent autoResetEvent = new(false);
        blockTree.NewHeadBlock += (_, _) => autoResetEvent.Set();

        IManualBlockProductionTrigger trigger = container.Resolve<IManualBlockProductionTrigger>();

        container.Resolve<IMainProcessingContext>().BlockProcessingQueue.Start();
        container.Resolve<IBlockProducerRunner>().Start();

        blockTree.SuggestBlock(Build.A.Block.Genesis.TestObject);

        Assert.That(autoResetEvent.WaitOne(NewHeadWaitMs), Is.True, "genesis");

        Assert.That(await trigger.BuildBlock(), Is.Not.Null, "produced");
        Assert.That(autoResetEvent.WaitOne(NewHeadWaitMs), Is.True, "1");
        Assert.That(blockTree.Head!.Number, Is.EqualTo(1));
    }

    private class EmptyTxSourceFactory : IBlockProducerTxSourceFactory
    {
        public ITxSource Create() => EmptyTxSource.Instance;
    }
}

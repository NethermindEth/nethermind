// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus;
using Nethermind.Consensus.Ethash;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Core.Test.Modules;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
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
        Assert.That(blockTree.Head.ParentBeaconBlockRoot, Is.EqualTo(Hash256.Zero));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task Does_not_produce_empty_blocks_for_a_tx_it_cannot_include([Values] bool nonceGap)
    {
        using NethDevTestBlockchain chain = await NethDevTestBlockchain.Create();
        BlockHeader genesis = chain.BlockTree.Head!.Header;
        IReleaseSpec spec = chain.SpecProvider.GetSpec(genesis);
        UInt256 nextBaseFee = spec.BaseFeeCalculator.Calculate(genesis, spec);

        // Without a nonce gap: below the next base fee, but above the base fee after one empty block.
        Transaction stuckTx = Build.A.Transaction
            .WithNonce(nonceGap ? 1UL : 0UL)
            .WithGasPrice(nonceGap ? nextBaseFee * 2 : nextBaseFee - 1)
            .To(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Assert.That(chain.TxPool.SubmitTx(stuckTx, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));

        await Task.Delay(1_000);
        Assert.That(chain.BlockTree.Head!.Number, Is.Zero, "empty blocks were produced for the stuck tx");

        Task stuckTxIncluded = chain.WaitForNewHeadWhere(b => b.Transactions.Any(tx => tx.Hash == stuckTx.Hash));

        Block? requested = await chain.Container.Resolve<IManualBlockProductionTrigger>().BuildBlock();
        Assert.That(requested?.Transactions, Is.Empty, "a requested block can be empty");

        if (nonceGap)
        {
            Transaction gapFiller = Build.A.Transaction
                .WithNonce(0)
                .WithGasPrice(nextBaseFee * 2)
                .To(TestItem.AddressB)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Assert.That(chain.TxPool.SubmitTx(gapFiller, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
        }

        await stuckTxIncluded;
    }

    private class EmptyTxSourceFactory : IBlockProducerTxSourceFactory
    {
        public ITxSource Create() => EmptyTxSource.Instance;
    }

    private sealed class NethDevTestBlockchain : BasicTestBlockchain
    {
        private NethDevBlockProducerFactory Factory => Container.Resolve<NethDevBlockProducerFactory>();

        public static async Task<NethDevTestBlockchain> Create()
        {
            NethDevTestBlockchain chain = new();
            await chain.Build(static builder => builder
                .AddModule(new NethDevPlugin(new ChainSpec()).Module)
                .AddSingleton<ISpecProvider>(new TestSpecProvider(London.Instance))
                .AddSingleton<IGenesisPostProcessor>(new FunctionalGenesisPostProcessor(static genesis => genesis.Header.BaseFeePerGas = 1.GWei)));
            return chain;
        }

        protected override IBlockProducer CreateTestBlockProducer() => Factory.InitBlockProducer();

        protected override IBlockProducerRunner CreateBlockProducerRunner() => Factory.InitBlockProducerRunner(BlockProducer);
    }
}

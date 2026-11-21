// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Ethash;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Events;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Core.Test.Modules;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State.Proofs;
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

#pragma warning disable NUnit2045 // The successful prerequisite is required before accessing its result.
        Assert.That(autoResetEvent.WaitOne(NewHeadWaitMs), Is.True, "genesis");
#pragma warning restore NUnit2045

        Assert.That(await trigger.BuildBlock(), Is.Not.Null, "produced");
        Assert.That(autoResetEvent.WaitOne(NewHeadWaitMs), Is.True, "1");
        Assert.That(blockTree.Head!.Number, Is.EqualTo(1));
        Assert.That(blockTree.Head.ParentBeaconBlockRoot, Is.EqualTo(Hash256.Zero));
    }

    private static IEnumerable<TestCaseData> WithdrawalsCases()
    {
        yield return new TestCaseData(London.Instance, London.Instance, null, null)
            .SetName("Produced block has no withdrawals before Shanghai");
        yield return new TestCaseData(Shanghai.Instance, Shanghai.Instance, null, Array.Empty<Withdrawal>())
            .SetName("Produced block has empty withdrawals on Shanghai");
        yield return new TestCaseData(London.Instance, Shanghai.Instance, null, Array.Empty<Withdrawal>())
            .SetName("Produced block has empty withdrawals on the first Shanghai block");
        yield return new TestCaseData(Shanghai.Instance, Shanghai.Instance, new[] { TestItem.WithdrawalA_1Eth }, new[] { TestItem.WithdrawalA_1Eth })
            .SetName("Produced block keeps supplied withdrawals");
    }

    [TestCaseSource(nameof(WithdrawalsCases)), MaxTime(Timeout.MaxTestTime)]
    public async Task Produced_block_has_the_withdrawals_its_fork_requires(IReleaseSpec parentSpec, IReleaseSpec childSpec, Withdrawal[]? supplied, Withdrawal[]? expected)
    {
        TestSpecProvider specProvider = new(parentSpec) { NextForkSpec = childSpec, ForkOnBlockNumber = 1 };
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton<ISpecProvider>(specProvider)
            .AddSingleton<IBlockValidator>(Always.Valid)
            .AddSingleton<IBlockProducerTxSourceFactory, EmptyTxSourceFactory>()
            .AddScoped<IBlockProducerFactory, TestBlockProcessingModule.AutoBlockProducerFactory<DevBlockProducer>>()
            .Build();

        IBlockTree blockTree = container.Resolve<IBlockTree>();
        AutoResetEvent autoResetEvent = new(false);
        blockTree.NewHeadBlock += (_, _) => autoResetEvent.Set();

        container.Resolve<IMainProcessingContext>().BlockProcessingQueue.Start();
        container.Resolve<IBlockProducerRunner>().Start();

        blockTree.SuggestBlock(Build.A.Block.Genesis.TestObject);
        Assert.That(autoResetEvent.WaitOne(NewHeadWaitMs), Is.True, "genesis");

        BlockHeader parent = blockTree.Head!.Header;
        PayloadAttributes? payloadAttributes = supplied is null ? null : new PayloadAttributes { Timestamp = parent.Timestamp + 1, Withdrawals = supplied };
        Block? block = await container.Resolve<IManualBlockProductionTrigger>().BuildBlock(payloadAttributes: payloadAttributes);
        Assert.That(block, Is.Not.Null, "produced");
        BlockValidator blockValidator = new(container.Resolve<ITxValidator>(), container.Resolve<IHeaderValidator>(),
            container.Resolve<IUnclesValidator>(), specProvider, LimboLogs.Instance);
        Assert.That(autoResetEvent.WaitOne(NewHeadWaitMs), Is.True, "1");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(block!.Withdrawals, Is.EqualTo(expected));
            Assert.That(block.Header.WithdrawalsRoot, Is.EqualTo(expected is null ? null : WithdrawalTrie.CalculateRoot(expected)));
            Assert.That(BlockValidator.ValidateWithdrawalsHashMatches(block, out _), Is.True, "body matches the header");
            Assert.That(blockValidator.ValidateSuggestedBlock(block, parent, out string? error), Is.True, error);
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task Produces_empty_blocks_while_they_lower_the_base_fee_to_a_tx_fee()
    {
        using NethDevTestBlockchain chain = await NethDevTestBlockchain.Create(1.GWei);

        // Below the next base fee, but above the base fee after one empty block.
        Transaction lowFeeTx = SubmitTx(chain, 0, chain.NextBaseFee() - 1);
        Block? includingBlock = null;
        await chain.WaitForNewHeadWhere(b =>
        {
            if (b.Transactions.All(tx => tx.Hash != lowFeeTx.Hash)) return false;
            includingBlock = b;
            return true;
        });

        Assert.That(includingBlock!.Number, Is.EqualTo(2));
    }

    public enum StuckTx
    {
        FeeBelowBaseFeeFloor,
        NonceGap,
        PremiumBelowMinGasPrice
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task Does_not_produce_empty_blocks_for_a_tx_it_cannot_include([Values] StuckTx stuckTxKind)
    {
        // An empty block cannot lower the base fee below this floor.
        using NethDevTestBlockchain chain = await NethDevTestBlockchain.Create(7.Wei, stuckTxKind is StuckTx.PremiumBelowMinGasPrice ? 1.Wei : 0);
        UInt256 nextBaseFee = chain.NextBaseFee();

        Transaction stuckTx = stuckTxKind switch
        {
            StuckTx.FeeBelowBaseFeeFloor => SubmitTx(chain, 0, nextBaseFee - 1),
            // The pool drops a gapped tx on a new head, so an empty block would also lose this tx.
            StuckTx.NonceGap => SubmitTx(chain, 1, nextBaseFee * 2),
            _ => SubmitTx(chain, 0, nextBaseFee),
        };

        await Task.Delay(1_000);
        Assert.That(chain.BlockTree.Head!.Number, Is.Zero, "empty blocks were produced for the stuck tx");

        if (stuckTxKind is StuckTx.NonceGap)
        {
            Task stuckTxIncluded = chain.WaitForNewHeadWhere(b => b.Transactions.Any(tx => tx.Hash == stuckTx.Hash));
            SubmitTx(chain, 0, nextBaseFee * 2);
            await stuckTxIncluded;
        }

        ulong requestedNumber = chain.BlockTree.Head!.Number + 1;
        Task requestedBlockAdded = chain.WaitForNewHeadWhere(b => b.Number == requestedNumber);
        Block? requested = await chain.Container.Resolve<IManualBlockProductionTrigger>().BuildBlock();
        Assert.That(requested?.Transactions, Is.Empty, "a requested block can be empty");
        await requestedBlockAdded;
    }

    private static Transaction SubmitTx(NethDevTestBlockchain chain, ulong nonce, UInt256 gasPrice)
    {
        Transaction tx = Build.A.Transaction
            .WithNonce(nonce)
            .WithGasPrice(gasPrice)
            .To(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Assert.That(chain.TxPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
        return tx;
    }

    private class EmptyTxSourceFactory : IBlockProducerTxSourceFactory
    {
        public ITxSource Create() => EmptyTxSource.Instance;
    }

    private sealed class NethDevTestBlockchain(UInt256 minGasPrice) : BasicTestBlockchain
    {
        private NethDevBlockProducerFactory Factory => Container.Resolve<NethDevBlockProducerFactory>();

        public static async Task<NethDevTestBlockchain> Create(UInt256 genesisBaseFee, UInt256 minGasPrice = default)
        {
            NethDevTestBlockchain chain = new(minGasPrice);
            await chain.Build(builder => builder
                .AddModule(new NethDevPlugin(new ChainSpec()).Module)
                // The default test override returns a new spec instance per head, which the pool treats as a fork:
                // the revalidation it queues runs after TxPoolHeadChanged and drops a gapped tx submitted meanwhile.
                // One stable instance keeps the pool from revalidating. EIP-3607 is off so AddressA, which has code, can send.
                // Shanghai, so the blocks that include transactions also get the empty withdrawals list.
                .AddSingleton<ISpecProvider>(new TestSpecProvider(new OverridableReleaseSpec(Shanghai.Instance) { IsEip3607Enabled = false }) { AllowTestChainOverride = false })
                .AddSingleton<IGenesisPostProcessor>(new FunctionalGenesisPostProcessor(genesis => genesis.Header.BaseFeePerGas = genesisBaseFee))
                .ConfigureTestConfiguration(conf => conf.SuggestGenesisOnStart = false));

            // The pool processes heads asynchronously and drops a gapped tx on each, genesis included,
            // so tests must not submit before the pool has seen genesis.
            Task genesisPoolHead = Wait.ForEventCondition<Block>(chain.CancellationToken,
                h => chain.TxPool.TxPoolHeadChanged += h,
                h => chain.TxPool.TxPoolHeadChanged -= h,
                b => b.IsGenesis);
            chain.MainProcessingContext.GenesisLoader.Load();
            await genesisPoolHead;
            return chain;
        }

        public UInt256 NextBaseFee()
        {
            BlockHeader head = BlockTree.Head!.Header;
            IReleaseSpec spec = SpecProvider.GetSpec(head);
            return spec.BaseFeeCalculator.Calculate(head, spec);
        }

        protected override IEnumerable<IConfig> CreateConfigs() => [new BlocksConfig { MinGasPrice = minGasPrice }];

        protected override IBlockProducer CreateTestBlockProducer() => Factory.InitBlockProducer();

        protected override IBlockProducerRunner CreateBlockProducerRunner() => Factory.InitBlockProducerRunner(BlockProducer);
    }
}

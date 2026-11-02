// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Transactions;

/// <summary>
/// <see cref="BaseFeeTxFilter"/> and <see cref="MinGasPriceTxFilter"/> read the next block's base fee from the
/// <see cref="TxFilterContext"/> they are given. With a base fee supplied, as block building supplies it, they must
/// decide, and word their rejections, exactly as with one the context computes from the parent and spec.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class StandardTxFilterBaseFeeTests
{
    private const ulong GasLimit = 30_000_000;

    // Both parents used exactly the gas target, so the next base fee equals theirs: 1 gwei and 2 gwei.
    private static readonly BlockHeader ParentAt1Gwei = Parent(10, 1.GWei);
    private static readonly BlockHeader ParentAt2Gwei = Parent(11, 2.GWei);

    private static readonly ReleaseSpec London = new() { IsEip1559Enabled = true };

    // A spec whose base-fee rule differs from London's for the same parent, as the next block's spec does at a fork
    // boundary: here a 3 gwei floor.
    private static readonly ReleaseSpec ForkWithFloor = new() { IsEip1559Enabled = true, Eip1559BaseFeeMinValue = 3.GWei };

    private static readonly ReleaseSpec BeforeLondon = new() { IsEip1559Enabled = false };

    private static readonly (BlockHeader Parent, IReleaseSpec Spec)[] Steps =
    [
        (ParentAt1Gwei, London),
        (ParentAt2Gwei, London),
        (ParentAt1Gwei, ForkWithFloor),
        (ParentAt2Gwei, ForkWithFloor),
        (ParentAt1Gwei, BeforeLondon),
        (ParentAt2Gwei, BeforeLondon),
    ];

    private static readonly Transaction[] Transactions =
    [
        Tx(maxFee: (UInt256)1_500_000_000, priorityFee: 1.GWei),
        Tx(maxFee: (UInt256)2_500_000_000, priorityFee: 2.GWei),
        Tx(maxFee: 4.GWei, priorityFee: (UInt256)500_000_000),
        Tx(maxFee: 10.GWei, priorityFee: 10.GWei),
        Build.A.Transaction.WithGasPrice(2.GWei).TestObject,
        Build.A.Transaction.WithGasPrice((UInt256)500_000_000).TestObject,
        Build.A.Transaction.WithType(TxType.EIP1559).WithMaxFeePerGas(1).WithMaxPriorityFeePerGas(1).WithIsServiceTransaction(true).TestObject,
    ];

    public static IEnumerable<TestCaseData> Filters()
    {
        yield return new TestCaseData((Func<ITxFilter>)(static () => new BaseFeeTxFilter())).SetArgDisplayNames("BaseFee");
        yield return new TestCaseData((Func<ITxFilter>)(static () => new MinGasPriceTxFilter(new BlocksConfig { MinGasPrice = 1.GWei })))
            .SetArgDisplayNames("MinGasPrice");
        yield return new TestCaseData((Func<ITxFilter>)(static () => new MinGasPriceTxFilter(new BlocksConfig { MinGasPrice = 1.GWei }) { IncludeRejectionMessage = false }))
            .SetArgDisplayNames("MinGasPriceWithoutMessage");
        yield return new TestCaseData((Func<ITxFilter>)(static () => new CustomFloorFilter(new MinGasPriceTxFilter(new BlocksConfig()), (UInt256)600_000_000)))
            .SetArgDisplayNames("MinGasPriceCustomFloor");
        yield return new TestCaseData((Func<ITxFilter>)(static () => new CompositeTxFilter(new MinGasPriceTxFilter(new BlocksConfig { MinGasPrice = 1.GWei }), new BaseFeeTxFilter())))
            .SetArgDisplayNames("Composite");
    }

    [TestCaseSource(nameof(Filters))]
    public void Supplied_base_fee_decides_and_words_rejections_as_a_computed_one(Func<ITxFilter> create)
    {
        ITxFilter filter = create();

        foreach ((BlockHeader parent, IReleaseSpec spec) in Steps)
        {
            foreach (Transaction tx in Transactions)
            {
                string[] results = Decide(filter, tx, parent, spec);
                Assert.That(results, Is.All.EqualTo(results[0]), $"parent {parent.Number}, spec floor {spec.Eip1559BaseFeeMinValue}, 1559 {spec.IsEip1559Enabled}");
            }
        }
    }

    [Test]
    public void Supplied_base_fee_through_the_standard_pipeline_decides_as_a_computed_one()
    {
        ITxFilterPipeline pipeline = TxFilterPipelineBuilder.CreateStandardFilteringPipeline(NullLogManager.Instance, new BlocksConfig { MinGasPrice = 1.GWei });

        foreach ((BlockHeader parent, IReleaseSpec spec) in Steps)
        {
            foreach (Transaction tx in Transactions)
            {
                TxFilterContext computed = new(parent, spec);
                TxFilterContext given = new(parent, spec, BaseFeeCalculator.Calculate(parent, spec));
                Assert.That(pipeline.Execute(tx, in given), Is.EqualTo(pipeline.Execute(tx, in computed)),
                    $"parent {parent.Number}, spec floor {spec.Eip1559BaseFeeMinValue}, 1559 {spec.IsEip1559Enabled}");
            }
        }
    }

    [Test]
    public void Different_parent_gives_its_own_base_fee()
    {
        BaseFeeTxFilter baseFeeFilter = new();
        MinGasPriceTxFilter minGasPriceFilter = new(new BlocksConfig { MinGasPrice = 1.GWei });
        CustomFloorFilter customFloorFilter = new(new MinGasPriceTxFilter(new BlocksConfig()), (UInt256)600_000_000);
        Transaction tx = Tx(maxFee: (UInt256)2_500_000_000, priorityFee: 2.GWei);
        Transaction lowMaxFeeTx = Tx(maxFee: (UInt256)1_500_000_000, priorityFee: 1.GWei);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Decide(baseFeeFilter, lowMaxFeeTx, ParentAt1Gwei, London), Is.All.EqualTo(Accepted));
            Assert.That(Decide(baseFeeFilter, lowMaxFeeTx, ParentAt2Gwei, London), Is.All.EqualTo(
                $"{AcceptTxResult.FeeTooLow}, MaxFeePerGas too low. MaxFeePerGas: 1500000000, BaseFee: 2000000000, MaxPriorityFeePerGas:1000000000, Block number: 12"));

            Assert.That(Decide(minGasPriceFilter, tx, ParentAt1Gwei, London), Is.All.EqualTo(Accepted));
            Assert.That(Decide(minGasPriceFilter, tx, ParentAt2Gwei, London), Is.All.EqualTo(
                $"{AcceptTxResult.FeeTooLow}, EffectivePriorityFeePerGas too low 500000000 < 1000000000, BaseFee: 2000000000"));

            Assert.That(Decide(customFloorFilter, tx, ParentAt1Gwei, London), Is.All.EqualTo(Accepted));
            Assert.That(Decide(customFloorFilter, tx, ParentAt2Gwei, London), Is.All.EqualTo(
                $"{AcceptTxResult.FeeTooLow}, EffectivePriorityFeePerGas too low 500000000 < 600000000, BaseFee: 2000000000"));
        }
    }

    [Test]
    public void Spec_of_the_next_block_gives_the_base_fee_at_a_fork_boundary()
    {
        BaseFeeTxFilter baseFeeFilter = new();
        MinGasPriceTxFilter minGasPriceFilter = new(new BlocksConfig { MinGasPrice = 1.GWei });
        Transaction tx = Tx(maxFee: (UInt256)2_500_000_000, priorityFee: 2.GWei);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Decide(baseFeeFilter, tx, ParentAt1Gwei, London), Is.All.EqualTo(Accepted));
            Assert.That(Decide(baseFeeFilter, tx, ParentAt1Gwei, ForkWithFloor), Is.All.EqualTo(
                $"{AcceptTxResult.FeeTooLow}, MaxFeePerGas too low. MaxFeePerGas: 2500000000, BaseFee: 3000000000, MaxPriorityFeePerGas:2000000000, Block number: 11"));

            Assert.That(Decide(minGasPriceFilter, tx, ParentAt1Gwei, London), Is.All.EqualTo(Accepted));
            Assert.That(Decide(minGasPriceFilter, tx, ParentAt1Gwei, ForkWithFloor), Is.All.EqualTo(
                $"{AcceptTxResult.FeeTooLow}, EffectivePriorityFeePerGas too low 0 < 1000000000, BaseFee: 3000000000"));
        }
    }

    [Test]
    public void Spec_without_EIP1559_ignores_the_base_fee_and_does_not_compute_it()
    {
        CountingBaseFeeCalculator calculator = new();
        ReleaseSpec spec = new() { IsEip1559Enabled = false, BaseFeeCalculator = calculator };
        MinGasPriceTxFilter minGasPriceFilter = new(new BlocksConfig { MinGasPrice = 1.GWei });
        Transaction tx = Build.A.Transaction.WithGasPrice((UInt256)500_000_000).TestObject;
        Transaction lowMaxFeeTx = Tx(maxFee: 1, priorityFee: 1);

        TxFilterContext computed = new(ParentAt1Gwei, spec);
        // Block building supplies what the calculator returns for any spec: the parent's base fee here, 1 gwei.
        TxFilterContext given = new(ParentAt1Gwei, spec, ParentAt1Gwei.BaseFeePerGas);

        using (Assert.EnterMultipleScope())
        {
            string rejection = $"{AcceptTxResult.FeeTooLow}, EffectivePriorityFeePerGas too low 500000000 < 1000000000, BaseFee: 0";
            Assert.That(minGasPriceFilter.IsAllowed(tx, in computed).ToString(), Is.EqualTo(rejection));
            Assert.That(minGasPriceFilter.IsAllowed(tx, in given).ToString(), Is.EqualTo(rejection));
            Assert.That(new BaseFeeTxFilter().IsAllowed(lowMaxFeeTx, in given).ToString(), Is.EqualTo(Accepted));
            Assert.That(new BaseFeeTxFilter().IsAllowed(lowMaxFeeTx, in computed).ToString(), Is.EqualTo(Accepted));
            Assert.That(calculator.Calls, Is.Zero, "the filters do not read the base fee without EIP-1559");
        }
    }

    [Test]
    public void Null_parent_is_handled_as_before()
    {
        CountingBaseFeeCalculator calculator = new();
        ReleaseSpec countingLondon = new() { IsEip1559Enabled = true, BaseFeeCalculator = calculator };
        Transaction tx = Tx(maxFee: 10.GWei, priorityFee: 1.GWei);
        Transaction serviceTx = Build.A.Transaction.WithType(TxType.EIP1559).WithMaxFeePerGas(1).WithMaxPriorityFeePerGas(1).WithIsServiceTransaction(true).TestObject;
        BaseFeeTxFilter baseFeeFilter = new();
        MinGasPriceTxFilter minGasPriceFilter = new(new BlocksConfig { MinGasPrice = 1.GWei });

        using (Assert.EnterMultipleScope())
        {
            // The calculator is asked about a null parent on every read, as the filters asked it for each transaction.
            TxFilterContext context = new(null!, countingLondon);
            Assert.That(calculator.Calls, Is.Zero, "building a context computes nothing");
            Assert.That((bool)baseFeeFilter.IsAllowed(tx, in context), Is.True);
            Assert.That((bool)minGasPriceFilter.IsAllowed(tx, in context), Is.True);
            Assert.That((bool)minGasPriceFilter.IsAllowed(tx, in context, 1.GWei), Is.True);
            Assert.That(calculator.Calls, Is.EqualTo(3));

            // The default calculator cannot compute from a null parent; the filters throw as they did.
            TxFilterContext london = new(null!, London);
            Assert.Throws<NullReferenceException>(() => baseFeeFilter.IsAllowed(tx, new TxFilterContext(null!, London)));
            Assert.Throws<NullReferenceException>(() => minGasPriceFilter.IsAllowed(tx, new TxFilterContext(null!, London)));
            // A service transaction never needed the base fee.
            Assert.That((bool)baseFeeFilter.IsAllowed(serviceTx, in london), Is.True);
            // Nor does a spec without EIP-1559.
            Assert.That((bool)minGasPriceFilter.IsAllowed(Build.A.Transaction.WithGasPrice(1.GWei).TestObject, new TxFilterContext(null!, BeforeLondon)), Is.True);
        }
    }

    [Test]
    public void Context_computes_nothing_when_no_filter_reads_the_base_fee()
    {
        CountingBaseFeeCalculator calculator = new();
        ReleaseSpec spec = new() { IsEip1559Enabled = true, BaseFeeCalculator = calculator };
        ITxFilterPipeline pipeline = new TxFilterPipelineBuilder(NullLogManager.Instance)
            .WithNullTxFilter()
            .WithHeadTxFilter()
            .WithCustomTxFilter(new CompositeTxFilter(NullTxFilter.Instance))
            .Build;
        Transaction tx = Tx(maxFee: 10.GWei, priorityFee: 1.GWei);
        Transaction serviceTx = Build.A.Transaction.WithType(TxType.EIP1559).WithMaxFeePerGas(1).WithMaxPriorityFeePerGas(1).WithIsServiceTransaction(true).TestObject;

        for (int i = 0; i < 10; i++)
        {
            TxFilterContext context = new(ParentAt1Gwei, spec);
            pipeline.Execute(tx, in context);
            new BaseFeeTxFilter().IsAllowed(serviceTx, in context);
        }

        Assert.That(calculator.Calls, Is.Zero);
    }

    [Test]
    public void Filters_read_the_base_fee_from_the_context()
    {
        CountingBaseFeeCalculator calculator = new();
        ReleaseSpec spec = new() { IsEip1559Enabled = true, BaseFeeCalculator = calculator };
        // A base fee no calculator would give for this parent, so a filter that computed its own would decide otherwise.
        TxFilterContext context = new(ParentAt1Gwei, spec, 7.GWei);
        Transaction tx = Tx(maxFee: 5.GWei, priorityFee: 1.GWei);
        ITxFilter composite = new CompositeTxFilter(new MinGasPriceTxFilter(new BlocksConfig { MinGasPrice = 0 }), new BaseFeeTxFilter());
        ITxFilterPipeline pipeline = TxFilterPipelineBuilder.CreateStandardFilteringPipeline(NullLogManager.Instance, new BlocksConfig { MinGasPrice = 0 });
        string rejection = $"{AcceptTxResult.FeeTooLow}, MaxFeePerGas too low. MaxFeePerGas: 5000000000, BaseFee: 7000000000, MaxPriorityFeePerGas:1000000000, Block number: 11";

        using (Assert.EnterMultipleScope())
        {
            Assert.That(new BaseFeeTxFilter().IsAllowed(tx, in context).ToString(), Is.EqualTo(rejection));
            Assert.That(composite.IsAllowed(tx, in context).ToString(), Is.EqualTo(rejection));
            Assert.That(new MinGasPriceTxFilter(new BlocksConfig { MinGasPrice = 1 }).IsAllowed(tx, in context).ToString(), Is.EqualTo(
                $"{AcceptTxResult.FeeTooLow}, EffectivePriorityFeePerGas too low 0 < 1, BaseFee: 7000000000"));
            Assert.That(pipeline.Execute(tx, in context), Is.False);
            for (int i = 0; i < 100; i++)
            {
                pipeline.Execute(tx, in context);
                composite.IsAllowed(tx, in context);
            }

            Assert.That(calculator.Calls, Is.Zero);

            // Without a supplied base fee, each filter that reads it computes it, as before.
            Assert.That((bool)composite.IsAllowed(tx, new TxFilterContext(ParentAt1Gwei, spec)), Is.True);
            Assert.That(calculator.Calls, Is.EqualTo(2));
        }
    }

    [Test]
    public void Block_building_computes_the_base_fee_once_per_build()
    {
        CountingBaseFeeCalculator calculator = new();
        ReleaseSpec london = new() { IsEip1559Enabled = true, BaseFeeCalculator = calculator };
        ReleaseSpec forkWithFloor = new() { IsEip1559Enabled = true, Eip1559BaseFeeMinValue = 3.GWei, BaseFeeCalculator = calculator };
        // Block 12 is the first block of a fork with a 3 gwei base-fee floor.
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(call => call.Arg<ForkActivation>().BlockNumber >= 12 ? forkWithFloor : london);

        // The parent of block 11 used its whole gas limit, so the base fee rises from 1 to 1.125 gwei.
        BlockHeader fullParent = Build.A.BlockHeader.WithNumber(10).WithGasLimit(GasLimit).WithGasUsed(GasLimit).WithBaseFee(1.GWei).TestObject;
        UInt256 minGasPrice = 50_000_000;
        Transaction[] a = Chain(TestItem.PrivateKeyA, 1, maxFee: 1_100_000_000, priorityFee: 1.GWei);   // max fee below 1.125 gwei
        Transaction[] b = Chain(TestItem.PrivateKeyB, 4, maxFee: 1_200_000_000, priorityFee: 100_000_000); // premium 0.075 gwei
        Transaction[] c = Chain(TestItem.PrivateKeyC, 1, maxFee: 1_160_000_000, priorityFee: 1.GWei);   // premium 0.035 gwei
        Transaction[] d = Chain(TestItem.PrivateKeyD, 4, maxFee: 10.GWei, priorityFee: 1.GWei);
        ITxPool txPool = Substitute.For<ITxPool>();
        txPool.GetPendingForProduction(Arg.Any<BlockHeader>(), Arg.Any<bool>(), Arg.Any<UInt256>())
            .Returns(new PendingTransactionsView(
                new Dictionary<AddressAsKey, Transaction[]>
                {
                    [a[0].SenderAddress!] = a,
                    [b[0].SenderAddress!] = b,
                    [c[0].SenderAddress!] = c,
                    [d[0].SenderAddress!] = d,
                },
                new Dictionary<AddressAsKey, Transaction[]>(),
                isRevalidated: true));
        BlocksConfig blocksConfig = new() { MinGasPrice = minGasPrice };
        TxPoolTxSource txSource = new(txPool, specProvider, new TransactionComparerProvider(specProvider, Substitute.For<IBlockFinder>()),
            LimboLogs.Instance, TxFilterPipelineBuilder.CreateStandardFilteringPipeline(LimboLogs.Instance, blocksConfig), blocksConfig,
            new SpecChangeTxValidator(specProvider.ChainId));

        Transaction[] firstBuild = txSource.GetTransactions(fullParent, Target(fullParent), GasLimit).ToArray();
        int afterFirstBuild = calculator.Calls;
        // The parent of block 12 used its gas target at 1 gwei, but block 12's spec raises the base fee to 3 gwei.
        BlockHeader targetParent = Parent(11, 1.GWei);
        Transaction[] secondBuild = txSource.GetTransactions(targetParent, Target(targetParent), GasLimit).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstBuild, Is.EquivalentTo(b.Concat(d)));
            Assert.That(afterFirstBuild, Is.EqualTo(1), "once by the tx source, never by the filters");
            Assert.That(secondBuild, Is.EquivalentTo(d));
            Assert.That(calculator.Calls, Is.EqualTo(2));
        }
    }

    [Test]
    public void Block_building_checks_with_the_spec_of_the_block_at_EIP1559_activation()
    {
        // EIP-1559 activates at block 11: the parent's spec does not have it, the block being built starts at 1 gwei.
        ReleaseSpec beforeLondon = new() { IsEip1559Enabled = false };
        ReleaseSpec activation = new() { IsEip1559Enabled = true, Eip1559TransitionBlock = 11 };
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(call => call.Arg<ForkActivation>().BlockNumber >= 11 ? activation : beforeLondon);
        BlockHeader parent = Build.A.BlockHeader.WithNumber(10).WithGasLimit(GasLimit).WithGasUsed(GasLimit / 2).TestObject;
        Transaction[] belowBaseFee = LegacyChain(TestItem.PrivateKeyA, 2, gasPrice: 500_000_000);
        Transaction[] aboveBaseFee = LegacyChain(TestItem.PrivateKeyB, 2, gasPrice: 2.GWei);
        ITxPool txPool = Substitute.For<ITxPool>();
        txPool.GetPendingForProduction(Arg.Any<BlockHeader>(), Arg.Any<bool>(), Arg.Any<UInt256>())
            .Returns(new PendingTransactionsView(
                new Dictionary<AddressAsKey, Transaction[]>
                {
                    [belowBaseFee[0].SenderAddress!] = belowBaseFee,
                    [aboveBaseFee[0].SenderAddress!] = aboveBaseFee,
                },
                new Dictionary<AddressAsKey, Transaction[]>(),
                isRevalidated: true));
        BlocksConfig blocksConfig = new();
        TxPoolTxSource txSource = new(txPool, specProvider, new TransactionComparerProvider(specProvider, Substitute.For<IBlockFinder>()),
            LimboLogs.Instance, TxFilterPipelineBuilder.CreateStandardFilteringPipeline(LimboLogs.Instance, blocksConfig), blocksConfig,
            new SpecChangeTxValidator(specProvider.ChainId));

        Transaction[] selected = txSource.GetTransactions(parent, Target(parent), GasLimit).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(BaseFeeCalculator.Calculate(parent, activation), Is.EqualTo(1.GWei));
            Assert.That(selected, Is.EquivalentTo(aboveBaseFee));
        }
    }

    [Test]
    public void Tx_pool_admission_follows_the_new_head()
    {
        MinGasPriceTxFilter shared = new(new BlocksConfig { MinGasPrice = 1.GWei });
        Block head = Build.A.Block.WithHeader(ParentAt1Gwei).TestObject;
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(_ => head);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(London);
        TxFilterAdapter adapter = new(blockTree, shared, LimboLogs.Instance, specProvider);

        foreach (BlockHeader newHead in new[] { ParentAt1Gwei, ParentAt2Gwei, ParentAt1Gwei, ParentAt2Gwei })
        {
            head = Build.A.Block.WithHeader(newHead).TestObject;
            foreach (Transaction tx in Transactions)
            {
                TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>(), London);
                AcceptTxResult actual = adapter.Accept(tx, ref state, TxHandlingOptions.None);
                TxFilterContext context = new(head.Header, London);
                Assert.That(actual.ToString(), Is.EqualTo(shared.IsAllowed(tx, in context).ToString()), $"head {head.Number}");
            }
        }
    }

    [Test]
    public void Tx_pool_admission_checks_with_the_spec_of_the_head()
    {
        // EIP-1559 activates at block 11 and the head is block 10, so admission checks with the head's spec, as before.
        ReleaseSpec beforeLondon = new() { IsEip1559Enabled = false };
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(call => call.Arg<ForkActivation>().BlockNumber >= 11 ? London : beforeLondon);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithHeader(ParentAt1Gwei).TestObject);
        TxFilterAdapter adapter = new(blockTree, new BaseFeeTxFilter(), LimboLogs.Instance, specProvider);
        Transaction tx = Tx(maxFee: (UInt256)500_000_000, priorityFee: 1);
        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>(), beforeLondon);

        Assert.That(adapter.Accept(tx, ref state, TxHandlingOptions.None).ToString(), Is.EqualTo(Accepted));
    }

    private const string Accepted = nameof(AcceptTxResult.Accepted);

    /// <summary>The result with a context that computes the base fee, then with one given the base fee as block
    /// building computes it, for any spec.</summary>
    private static string[] Decide(ITxFilter filter, Transaction tx, BlockHeader parent, IReleaseSpec spec)
    {
        TxFilterContext computed = new(parent, spec);
        TxFilterContext given = new(parent, spec, BaseFeeCalculator.Calculate(parent, spec));
        return
        [
            filter.IsAllowed(tx, in computed).ToString(),
            filter.IsAllowed(tx, in given).ToString(),
        ];
    }

    private static BlockHeader Parent(ulong number, UInt256 baseFee) => Build.A.BlockHeader
        .WithNumber(number)
        .WithGasLimit(GasLimit)
        .WithGasUsed(GasLimit / Eip1559Constants.DefaultElasticityMultiplier)
        .WithBaseFee(baseFee)
        .TestObject;

    private static BlockHeader Target(BlockHeader parent) => Build.A.BlockHeader
        .WithParentHash(parent.Hash!)
        .WithNumber(parent.Number + 1)
        .WithGasLimit(GasLimit)
        .TestObject;

    private static Transaction Tx(UInt256 maxFee, UInt256 priorityFee) => Build.A.Transaction
        .WithType(TxType.EIP1559)
        .WithMaxFeePerGas(maxFee)
        .WithMaxPriorityFeePerGas(priorityFee)
        .TestObject;

    private static Transaction[] Chain(PrivateKey sender, int count, UInt256 maxFee, UInt256 priorityFee) =>
        Enumerable.Range(0, count).Select(nonce => Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithNonce((ulong)nonce)
            .WithMaxFeePerGas(maxFee)
            .WithMaxPriorityFeePerGas(priorityFee)
            .WithGasLimit(21_000)
            .SignedAndResolved(sender)
            .TestObject).ToArray();

    private static Transaction[] LegacyChain(PrivateKey sender, int count, UInt256 gasPrice) =>
        Enumerable.Range(0, count).Select(nonce => Build.A.Transaction
            .WithNonce((ulong)nonce)
            .WithGasPrice(gasPrice)
            .WithGasLimit(21_000)
            .SignedAndResolved(sender)
            .TestObject).ToArray();

    private sealed class CustomFloorFilter(MinGasPriceTxFilter inner, UInt256 floor) : ITxFilter
    {
        public AcceptTxResult IsAllowed(Transaction tx, in TxFilterContext context) =>
            inner.IsAllowed(tx, in context, floor);
    }

    private sealed class CountingBaseFeeCalculator : IBaseFeeCalculator
    {
        private readonly DefaultBaseFeeCalculator _inner = new();
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public UInt256 Calculate(BlockHeader parent, IEip1559Spec specFor1559)
        {
            Interlocked.Increment(ref _calls);
            // The default calculator needs a parent; a null one stands for a caller without a head.
            return parent is null ? UInt256.Zero : _inner.Calculate(parent, specFor1559);
        }
    }
}

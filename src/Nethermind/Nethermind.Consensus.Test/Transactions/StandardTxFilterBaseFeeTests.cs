// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Transactions;

/// <summary>
/// <see cref="BaseFeeTxFilter"/> and <see cref="MinGasPriceTxFilter"/> compute the parent's base fee once per parent
/// and spec. A filter that is reused across parents, specs and threads must decide, and word its rejection, exactly as
/// a filter that computes the base fee for every transaction.
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

    private static readonly (BlockHeader Parent, IReleaseSpec Spec)[] Steps =
    [
        (ParentAt1Gwei, London),
        (ParentAt2Gwei, London),
        (ParentAt1Gwei, London),
        (ParentAt1Gwei, ForkWithFloor),
        (ParentAt2Gwei, ForkWithFloor),
        (ParentAt1Gwei, London),
    ];

    private static readonly Transaction[] Transactions =
    [
        Tx(maxFee: (UInt256)1_500_000_000, priorityFee: 1.GWei),
        Tx(maxFee: (UInt256)2_500_000_000, priorityFee: 2.GWei),
        Tx(maxFee: 4.GWei, priorityFee: (UInt256)500_000_000),
        Tx(maxFee: 10.GWei, priorityFee: 10.GWei),
        Build.A.Transaction.WithGasPrice(2.GWei).TestObject,
    ];

    public static IEnumerable<TestCaseData> Filters()
    {
        yield return new TestCaseData((Func<ITxFilter>)(static () => new BaseFeeTxFilter())).SetArgDisplayNames("BaseFee");
        yield return new TestCaseData((Func<ITxFilter>)(static () => new MinGasPriceTxFilter(new BlocksConfig { MinGasPrice = 1.GWei })))
            .SetArgDisplayNames("MinGasPrice");
        yield return new TestCaseData((Func<ITxFilter>)(static () => new MinGasPriceTxFilter(new BlocksConfig { MinGasPrice = 1.GWei }) { IncludeRejectionMessage = false }))
            .SetArgDisplayNames("MinGasPriceWithoutMessage");
        yield return new TestCaseData((Func<ITxFilter>)(static () => new CustomFloorFilter(new MinGasPriceTxFilter(new BlocksConfig()), (UInt256)500_000_000)))
            .SetArgDisplayNames("MinGasPriceCustomFloor");
    }

    [TestCaseSource(nameof(Filters))]
    public void Reused_filter_decides_as_a_fresh_filter_across_parents_and_specs(Func<ITxFilter> create)
    {
        ITxFilter shared = create();

        foreach ((BlockHeader parent, IReleaseSpec spec) in Steps)
        {
            foreach (Transaction tx in Transactions)
            {
                AcceptTxResult expected = create().IsAllowed(tx, parent, spec);
                AcceptTxResult actual = shared.IsAllowed(tx, parent, spec);
                Assert.That(actual.ToString(), Is.EqualTo(expected.ToString()), $"parent {parent.Number}, spec floor {spec.Eip1559BaseFeeMinValue}");
            }
        }
    }

    [Test]
    public void Different_parent_computes_the_base_fee_again()
    {
        BaseFeeTxFilter baseFeeFilter = new();
        MinGasPriceTxFilter minGasPriceFilter = new(new BlocksConfig { MinGasPrice = 1.GWei });
        Transaction tx = Tx(maxFee: (UInt256)2_500_000_000, priorityFee: 2.GWei);
        Transaction lowMaxFeeTx = Tx(maxFee: (UInt256)1_500_000_000, priorityFee: 1.GWei);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((bool)baseFeeFilter.IsAllowed(lowMaxFeeTx, ParentAt1Gwei, London), Is.True);
            Assert.That(baseFeeFilter.IsAllowed(lowMaxFeeTx, ParentAt2Gwei, London).ToString(), Is.EqualTo(
                $"{AcceptTxResult.FeeTooLow}, MaxFeePerGas too low. MaxFeePerGas: 1500000000, BaseFee: 2000000000, MaxPriorityFeePerGas:1000000000, Block number: 12"));
            Assert.That((bool)baseFeeFilter.IsAllowed(lowMaxFeeTx, ParentAt1Gwei, London), Is.True);

            Assert.That((bool)minGasPriceFilter.IsAllowed(tx, ParentAt1Gwei, London), Is.True);
            Assert.That(minGasPriceFilter.IsAllowed(tx, ParentAt2Gwei, London).ToString(), Is.EqualTo(
                $"{AcceptTxResult.FeeTooLow}, EffectivePriorityFeePerGas too low 500000000 < 1000000000, BaseFee: 2000000000"));
            Assert.That((bool)minGasPriceFilter.IsAllowed(tx, ParentAt1Gwei, London), Is.True);
        }
    }

    [Test]
    public void Spec_change_at_a_fork_boundary_computes_the_base_fee_again()
    {
        BaseFeeTxFilter baseFeeFilter = new();
        MinGasPriceTxFilter minGasPriceFilter = new(new BlocksConfig { MinGasPrice = 1.GWei });
        Transaction tx = Tx(maxFee: (UInt256)2_500_000_000, priorityFee: 2.GWei);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((bool)baseFeeFilter.IsAllowed(tx, ParentAt1Gwei, London), Is.True);
            Assert.That(baseFeeFilter.IsAllowed(tx, ParentAt1Gwei, ForkWithFloor).ToString(), Is.EqualTo(
                $"{AcceptTxResult.FeeTooLow}, MaxFeePerGas too low. MaxFeePerGas: 2500000000, BaseFee: 3000000000, MaxPriorityFeePerGas:2000000000, Block number: 11"));

            Assert.That((bool)minGasPriceFilter.IsAllowed(tx, ParentAt1Gwei, London), Is.True);
            Assert.That(minGasPriceFilter.IsAllowed(tx, ParentAt1Gwei, ForkWithFloor).ToString(), Is.EqualTo(
                $"{AcceptTxResult.FeeTooLow}, EffectivePriorityFeePerGas too low 0 < 1000000000, BaseFee: 3000000000"));
            Assert.That((bool)minGasPriceFilter.IsAllowed(tx, ParentAt1Gwei, London), Is.True);
        }
    }

    [Test]
    public void Base_fee_is_computed_once_per_parent_and_spec()
    {
        CountingBaseFeeCalculator calculator = new();
        ReleaseSpec spec = new() { IsEip1559Enabled = true, BaseFeeCalculator = calculator };
        ReleaseSpec nextSpec = new() { IsEip1559Enabled = true, BaseFeeCalculator = calculator };
        ITxFilterPipeline pipeline = TxFilterPipelineBuilder.CreateStandardFilteringPipeline(NullLogManager.Instance, new BlocksConfig());
        Transaction tx = Tx(maxFee: 10.GWei, priorityFee: 1.GWei);

        for (int i = 0; i < 100; i++) pipeline.Execute(tx, ParentAt1Gwei, spec);
        int afterOneParent = calculator.Calls;
        for (int i = 0; i < 100; i++) pipeline.Execute(tx, ParentAt2Gwei, spec);
        int afterTwoParents = calculator.Calls;
        for (int i = 0; i < 100; i++) pipeline.Execute(tx, ParentAt2Gwei, nextSpec);

        using (Assert.EnterMultipleScope())
        {
            // One computation per filter: the min gas price filter and the base fee filter.
            Assert.That(afterOneParent, Is.EqualTo(2));
            Assert.That(afterTwoParents, Is.EqualTo(4));
            Assert.That(calculator.Calls, Is.EqualTo(6));
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
                AcceptTxResult expected = new MinGasPriceTxFilter(new BlocksConfig { MinGasPrice = 1.GWei }).IsAllowed(tx, head.Header, London);
                Assert.That(actual.ToString(), Is.EqualTo(expected.ToString()), $"head {head.Number}");
            }
        }
    }

    [TestCaseSource(nameof(Filters))]
    public void Concurrent_callers_with_different_parents_and_specs_get_their_own_base_fee(Func<ITxFilter> create)
    {
        ITxFilter shared = create();
        string[][] expected = Steps.Select(step => Transactions.Select(tx => create().IsAllowed(tx, step.Parent, step.Spec).ToString()).ToArray()).ToArray();
        int mismatches = 0;

        Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = 4 }, worker =>
        {
            for (int i = 0; i < 20_000; i++)
            {
                int step = (i + worker) % Steps.Length;
                int txIndex = (i / Steps.Length + worker) % Transactions.Length;
                (BlockHeader parent, IReleaseSpec spec) = Steps[step];
                if (shared.IsAllowed(Transactions[txIndex], parent, spec).ToString() != expected[step][txIndex])
                {
                    Interlocked.Increment(ref mismatches);
                }
            }
        });

        Assert.That(mismatches, Is.Zero);
    }

    private static BlockHeader Parent(ulong number, UInt256 baseFee) => Build.A.BlockHeader
        .WithNumber(number)
        .WithGasLimit(GasLimit)
        .WithGasUsed(GasLimit / Eip1559Constants.DefaultElasticityMultiplier)
        .WithBaseFee(baseFee)
        .TestObject;

    private static Transaction Tx(UInt256 maxFee, UInt256 priorityFee) => Build.A.Transaction
        .WithType(TxType.EIP1559)
        .WithMaxFeePerGas(maxFee)
        .WithMaxPriorityFeePerGas(priorityFee)
        .TestObject;

    private sealed class CustomFloorFilter(MinGasPriceTxFilter inner, UInt256 floor) : ITxFilter
    {
        public AcceptTxResult IsAllowed(Transaction tx, BlockHeader parentHeader, IReleaseSpec spec) =>
            inner.IsAllowed(tx, parentHeader, floor, spec);
    }

    private sealed class CountingBaseFeeCalculator : IBaseFeeCalculator
    {
        private readonly DefaultBaseFeeCalculator _inner = new();
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public UInt256 Calculate(BlockHeader parent, IEip1559Spec specFor1559)
        {
            Interlocked.Increment(ref _calls);
            return _inner.Calculate(parent, specFor1559);
        }
    }
}

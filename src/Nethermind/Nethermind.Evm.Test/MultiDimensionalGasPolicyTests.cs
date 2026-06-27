// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class MultiDimensionalGasPolicyTests
{
    private static ulong SumUsed(in MultiDimensionalGasPolicy gas) =>
        gas.Used(MultiGasDimension.Computation)
        + gas.Used(MultiGasDimension.StorageAccessRead)
        + gas.Used(MultiGasDimension.StorageAccessWrite)
        + gas.Used(MultiGasDimension.StorageGrowth)
        + gas.Used(MultiGasDimension.HistoryGrowth);

    [Test]
    public void Consume_attributes_to_Computation()
    {
        MultiDimensionalGasPolicy gas = MultiDimensionalGasPolicy.FromULong(1000);
        MultiDimensionalGasPolicy.Consume(ref gas, 100);

        Assert.That(MultiDimensionalGasPolicy.GetRemainingGas(in gas), Is.EqualTo(900UL));
        Assert.That(gas.Used(MultiGasDimension.Computation), Is.EqualTo(100UL));
        Assert.That(SumUsed(in gas), Is.EqualTo(100UL));
    }

    [Test]
    public void ConsumeLogEmission_attributes_to_HistoryGrowth()
    {
        MultiDimensionalGasPolicy gas = MultiDimensionalGasPolicy.FromULong(100_000);
        MultiDimensionalGasPolicy.ConsumeLogEmission(ref gas, topicCount: 2, dataSize: 10);

        ulong expected = GasCostOf.Log + 2 * GasCostOf.LogTopic + 10 * GasCostOf.LogData;
        Assert.That(gas.Used(MultiGasDimension.HistoryGrowth), Is.EqualTo(expected));
        Assert.That(gas.Used(MultiGasDimension.Computation), Is.EqualTo(0UL));
        Assert.That(SumUsed(in gas), Is.EqualTo(expected));
    }

    [Test]
    public void ConsumeNewAccountCreation_attributes_to_StorageGrowth()
    {
        MultiDimensionalGasPolicy gas = MultiDimensionalGasPolicy.FromULong(100_000);
        MultiDimensionalGasPolicy.ConsumeNewAccountCreation<OffFlag>(ref gas);

        Assert.That(gas.Used(MultiGasDimension.StorageGrowth), Is.EqualTo(GasCostOf.NewAccount));
        Assert.That(gas.Used(MultiGasDimension.Computation), Is.EqualTo(0UL));
    }

    [Test]
    public void ConsumeStorageWrite_slot_creation_is_StorageGrowth_reset_is_StorageAccessWrite()
    {
        MultiDimensionalGasPolicy creation = MultiDimensionalGasPolicy.FromULong(1_000_000);
        MultiDimensionalGasPolicy.ConsumeStorageWrite<OffFlag, OnFlag>(ref creation, Cancun.Instance);
        Assert.That(creation.Used(MultiGasDimension.StorageGrowth), Is.EqualTo(GasCostOf.SSet));
        Assert.That(creation.Used(MultiGasDimension.StorageAccessWrite), Is.EqualTo(0UL));

        MultiDimensionalGasPolicy reset = MultiDimensionalGasPolicy.FromULong(1_000_000);
        MultiDimensionalGasPolicy.ConsumeStorageWrite<OffFlag, OffFlag>(ref reset, Cancun.Instance);
        Assert.That(reset.Used(MultiGasDimension.StorageAccessWrite), Is.EqualTo(Cancun.Instance.GasCosts.SStoreResetCost));
        Assert.That(reset.Used(MultiGasDimension.StorageGrowth), Is.EqualTo(0UL));
    }

    [Test]
    public void Used_always_sums_to_spent_gas()
    {
        const ulong initial = 1_000_000;
        MultiDimensionalGasPolicy gas = MultiDimensionalGasPolicy.FromULong(initial);
        MultiDimensionalGasPolicy.Consume(ref gas, 21_000);
        MultiDimensionalGasPolicy.UpdateGas(ref gas, 2_600);
        MultiDimensionalGasPolicy.ConsumeStateGas(ref gas, 5_000);
        MultiDimensionalGasPolicy.ConsumeLogEmission(ref gas, 1, 32);

        ulong spent = initial - MultiDimensionalGasPolicy.GetRemainingGas(in gas);
        Assert.That(SumUsed(in gas), Is.EqualTo(spent));
    }

    [Test]
    public void Remaining_mirrors_EthereumGasPolicy_over_a_charge_sequence()
    {
        EthereumGasPolicy eth = EthereumGasPolicy.FromULong(1_000_000);
        MultiDimensionalGasPolicy multi = MultiDimensionalGasPolicy.FromULong(1_000_000);

        ReadOnlySpan<ulong> charges = [21_000, 3, 2_600, 100, 20_000, 375, 9_000];
        foreach (ulong c in charges)
        {
            EthereumGasPolicy.Consume(ref eth, c);
            MultiDimensionalGasPolicy.Consume(ref multi, c);
            Assert.That(MultiDimensionalGasPolicy.GetRemainingGas(in multi),
                Is.EqualTo(EthereumGasPolicy.GetRemainingGas(in eth)));
        }
    }

    private static ulong Combine<T>(ulong regular, ulong state) where T : struct, IGasPolicy<T> =>
        T.CombineBlockGas(regular, state);

    [Test]
    public void CombineBlockGas_is_a_policy_concern_max_for_Ethereum_sum_for_multidim()
    {
        // EIP-8037: a block is full when its bottleneck dimension is full → max.
        Assert.That(Combine<EthereumGasPolicy>(100, 50), Is.EqualTo(100UL));
        // Multi-gas instrumentation sums the dimensions back to the legacy total → sum.
        Assert.That(Combine<MultiDimensionalGasPolicy>(100, 50), Is.EqualTo(150UL));
    }

    [Test]
    public void UpdateGas_failure_burns_remaining_and_flags_out_of_gas()
    {
        MultiDimensionalGasPolicy gas = MultiDimensionalGasPolicy.FromULong(50);
        bool ok = MultiDimensionalGasPolicy.UpdateGas(ref gas, 100);

        Assert.That(ok, Is.False);
        Assert.That(MultiDimensionalGasPolicy.GetRemainingGas(in gas), Is.EqualTo(0UL));
        Assert.That(MultiDimensionalGasPolicy.IsOutOfGas(in gas), Is.True);
        Assert.That(SumUsed(in gas), Is.EqualTo(50UL));
    }
}

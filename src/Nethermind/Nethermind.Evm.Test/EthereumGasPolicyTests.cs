// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class EthereumGasPolicyTests
{
    // Locks the ConsumeDataCopyGas contract: the policy computes base access cost + per-word copy
    // cost internally, so any multidimensional policy can rely on (and re-categorize) the same total.
    [TestCase(false, 0UL, TestName = "CODECOPY/CALLDATACOPY/RETURNDATACOPY, empty")]
    [TestCase(false, 5UL, TestName = "CODECOPY/CALLDATACOPY/RETURNDATACOPY, 5 words")]
    [TestCase(true, 0UL, TestName = "EXTCODECOPY, empty")]
    [TestCase(true, 10UL, TestName = "EXTCODECOPY, 10 words")]
    public void ConsumeDataCopyGas_charges_base_access_plus_per_word_copy(bool isExternalCode, ulong words)
    {
        const ulong initial = 1_000_000;
        EthereumGasPolicy gas = EthereumGasPolicy.FromULong(initial);
        EthereumGasPolicy.ConsumeDataCopyGas(ref gas, Cancun.Instance, isExternalCode, words);

        ulong baseCost = isExternalCode ? Cancun.Instance.GasCosts.ExtCodeCost : GasCostOf.VeryLow;
        ulong expected = baseCost + GasCostOf.Memory * words;
        Assert.That(initial - EthereumGasPolicy.GetRemainingGas(in gas), Is.EqualTo(expected));
    }

    [Test]
    public void CreateAvailableFromIntrinsic_returns_out_of_gas_when_gas_limit_below_intrinsic()
    {
        // gasLimit sits between intrinsic regular and intrinsic regular + state reservoir, so the
        // regular-only static check would pass but the reservoir is unaffordable.
        EthereumGasPolicy intrinsic = new() { Value = 30_000, StateReservoir = 183_600 };

        EthereumGasPolicy available = EthereumGasPolicy.CreateAvailableFromIntrinsic(30_000, in intrinsic, Amsterdam.Instance);

        Assert.That(EthereumGasPolicy.IsOutOfGas(in available), Is.True);
        Assert.That(EthereumGasPolicy.GetRemainingGas(in available), Is.EqualTo(0UL));
        Assert.That(EthereumGasPolicy.GetStateReservoir(in available), Is.EqualTo(0UL));
    }

    [Test]
    public void RevertRefundToHalt_saturates_state_gas_used_instead_of_wrapping()
    {
        EthereumGasPolicy parent = new() { StateGasUsed = 100 };
        EthereumGasPolicy child = new() { StateGasUsed = 250 };

        EthereumGasPolicy.RevertRefundToHalt(ref parent, in child);

        Assert.That(EthereumGasPolicy.GetStateGasUsed(in parent), Is.EqualTo(0UL));
        Assert.That(EthereumGasPolicy.GetStateReservoir(in parent), Is.EqualTo(250UL));
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
using NUnit.Framework;

namespace Nethermind.Evm.ZkEvm.Test;

/// <summary>Tests for the gas policy members only the guest has: a fixed charge that subtracts first and tests the sign, and the setter dispatch writes carried gas back with.</summary>
public class GuestFixedGasChargeTests
{
    [TestCase(0UL, false, 0UL)]
    [TestCase(GasCostOf.VeryLow - 1, false, 0UL)]
    [TestCase(GasCostOf.VeryLow, true, 0UL)]
    [TestCase(GasCostOf.VeryLow + 1, true, 1UL)]
    [TestCase((ulong)long.MaxValue, true, (ulong)long.MaxValue - GasCostOf.VeryLow)]
    public void Charge_matches_the_unsigned_compare(ulong gas, bool affordable, ulong left)
    {
        EthereumGasPolicy policy = EthereumGasPolicy.FromULong(gas);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(EthereumGasPolicy.UpdateGas<VeryLowGasCost>(ref policy), Is.EqualTo(affordable));
            Assert.That(policy.Value, Is.EqualTo(left));
        }
    }

    [Test]
    public void SetRemainingGas_overwrites_remaining_and_preserves_state_gas([Values(0UL, 1000UL)] ulong value)
    {
        EthereumGasPolicy gas = EthereumGasPolicy.FromFrameLimits(500, 700);
        long reservoir = EthereumGasPolicy.GetStateReservoir(in gas);
        EthereumGasPolicy.SetRemainingGas(ref gas, value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(EthereumGasPolicy.GetRemainingGas(in gas), Is.EqualTo(value));
            Assert.That(EthereumGasPolicy.GetStateReservoir(in gas), Is.EqualTo(reservoir));
        }
    }
}

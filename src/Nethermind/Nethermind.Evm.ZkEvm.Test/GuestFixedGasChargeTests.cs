// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
using NUnit.Framework;

namespace Nethermind.Evm.ZkEvm.Test;

/// <summary>Tests for the guest's fixed gas charge, which subtracts first and tests the sign of the difference.</summary>
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
}

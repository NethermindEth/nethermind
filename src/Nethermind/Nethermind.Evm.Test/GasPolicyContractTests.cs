// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm.GasPolicy;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture(typeof(EthereumGasPolicy))]
public class GasPolicyContractTests<TGasPolicy> where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    [TestCase(0UL)]
    [TestCase(1UL)]
    [TestCase(1_000_000UL)]
    public void FromULong_round_trips_remaining_gas(ulong value)
    {
        TGasPolicy gas = TGasPolicy.FromULong(value);
        Assert.That(TGasPolicy.GetRemainingGas(in gas), Is.EqualTo(value));
    }

    [TestCase(0UL, 0UL, true, 0UL)]
    [TestCase(0UL, 1UL, false, 0UL)]
    [TestCase(100UL, 100UL, true, 0UL)]
    [TestCase(100UL, 101UL, false, 0UL)]
    [TestCase(1000UL, 100UL, true, 900UL)]
    public void UpdateGas_reports_affordability(ulong available, ulong cost, bool expectedSuccess, ulong expectedRemaining)
    {
        TGasPolicy gas = TGasPolicy.FromULong(available);
        bool success = TGasPolicy.UpdateGas(ref gas, cost);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(success, Is.EqualTo(expectedSuccess));
            Assert.That(TGasPolicy.GetRemainingGas(in gas), Is.EqualTo(expectedRemaining));
        }
    }

    [Test]
    public void TryConsume_leaves_gas_untouched_when_unaffordable()
    {
        TGasPolicy gas = TGasPolicy.FromULong(100);
        Assert.That(TGasPolicy.TryConsume(ref gas, 101), Is.False);
        Assert.That(TGasPolicy.GetRemainingGas(in gas), Is.EqualTo(100UL));
    }

    [Test]
    public void TryConsume_reduces_when_affordable()
    {
        TGasPolicy gas = TGasPolicy.FromULong(100);
        Assert.That(TGasPolicy.TryConsume(ref gas, 40), Is.True);
        Assert.That(TGasPolicy.GetRemainingGas(in gas), Is.EqualTo(60UL));
    }

    [TestCase(10UL, 20UL)]
    [TestCase(20UL, 10UL)]
    [TestCase(15UL, 15UL)]
    public void Max_is_commutative_and_returns_larger_remaining(ulong a, ulong b)
    {
        TGasPolicy ga = TGasPolicy.FromULong(a);
        TGasPolicy gb = TGasPolicy.FromULong(b);
        ulong forward = TGasPolicy.GetRemainingGas(TGasPolicy.Max(in ga, in gb));
        ulong backward = TGasPolicy.GetRemainingGas(TGasPolicy.Max(in gb, in ga));
        Assert.That(forward, Is.EqualTo(Math.Max(a, b)));
        Assert.That(forward, Is.EqualTo(backward));
    }

    [TestCase(100UL, 40UL)]
    [TestCase(40UL, 100UL)]
    [TestCase(0UL, 0UL)]
    public void CombineBlockGas_is_at_least_each_dimension(ulong execution, ulong state)
    {
        ulong combined = TGasPolicy.CombineBlockGas(execution, state);
        Assert.That(combined, Is.GreaterThanOrEqualTo(execution));
        Assert.That(combined, Is.GreaterThanOrEqualTo(state));
    }

    [Test]
    public void Refund_adds_child_remaining_to_parent()
    {
        TGasPolicy parent = TGasPolicy.FromULong(500);
        TGasPolicy child = TGasPolicy.FromULong(300);
        TGasPolicy.Refund(ref parent, in child);
        Assert.That(TGasPolicy.GetRemainingGas(in parent), Is.EqualTo(800UL));
    }

    [Test]
    public void TryReserveChildGas_retains_at_least_one_sixty_fourth()
    {
        const ulong available = 6400;
        TGasPolicy gas = TGasPolicy.FromULong(available);
        Assert.That(TGasPolicy.TryReserveChildGas(ref gas, Cancun.Instance, out ulong childGas), Is.True);
        Assert.That(childGas, Is.EqualTo(available - available / 64));
        Assert.That(TGasPolicy.GetRemainingGas(in gas), Is.GreaterThanOrEqualTo(available / 64));
    }
}

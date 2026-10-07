// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

public class PeerValidationGasBudgetTests
{
    private long _now;

    private PeerValidationGasBudget Budget(ulong gasPerSecond = 30_000_000, ulong maxVerifyGas = 0) =>
        new(gasPerSecond, maxVerifyGas, () => _now);

    private void Advance(double seconds) => _now += (long)(seconds * Stopwatch.Frequency);

    [Test]
    public void TryReserve_SpendsTheBurstThenDefers()
    {
        PeerValidationGasBudget budget = Budget();

        int reserved = 0;
        while (budget.TryReserve(300_000)) reserved++;

        Assert.That(reserved, Is.EqualTo(100), "a 30M gas burst holds exactly one hundred 300k reservations");
    }

    [Test]
    public void TryReserve_RefillsAtTheConfiguredRate()
    {
        PeerValidationGasBudget budget = Budget();
        while (budget.TryReserve(300_000)) { }

        Advance(0.1);

        int reserved = 0;
        while (budget.TryReserve(300_000)) reserved++;
        Assert.That(reserved, Is.EqualTo(10), "0.1 s refills 3M gas, ten 300k reservations");
    }

    [Test]
    public void TryReserve_GasPerSecondDoesNotDependOnTheCap()
    {
        PeerValidationGasBudget small = Budget();
        PeerValidationGasBudget large = Budget();
        while (small.TryReserve(300_000)) { }
        while (large.TryReserve(500_000)) { }

        Advance(1);

        ulong smallGas = 0, largeGas = 0;
        while (small.TryReserve(300_000)) smallGas += 300_000;
        while (large.TryReserve(500_000)) largeGas += 500_000;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(smallGas, Is.EqualTo(30_000_000));
            Assert.That(largeGas, Is.EqualTo(30_000_000), "a larger cap buys fewer transactions, not more gas");
        }
    }

    [Test]
    public void Refund_MakesAdmittedValidationFree()
    {
        PeerValidationGasBudget budget = Budget(gasPerSecond: 1_000_000);

        for (int i = 0; i < 50; i++)
        {
            Assert.That(budget.TryReserve(500_000), Is.True, $"admitted transaction {i} was charged");
            budget.Refund(500_000);
        }
    }

    [Test]
    public void Refill_NeverExceedsTheBurst()
    {
        PeerValidationGasBudget budget = Budget(gasPerSecond: 1_000_000);
        Advance(60);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(budget.TryReserve(1_000_000), Is.True);
            Assert.That(budget.TryReserve(1), Is.False, "an idle peer must not bank more than one burst");
        }
    }

    [Test]
    public void TryReserve_BudgetBelowOneTransaction_StillAdmitsOnePerRefill()
    {
        PeerValidationGasBudget budget = Budget(gasPerSecond: 200_000);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(budget.TryReserve(300_000), Is.True, "a full bucket must admit a transaction larger than itself");
            Assert.That(budget.TryReserve(300_000), Is.False);
            Advance(1);
            Assert.That(budget.TryReserve(300_000), Is.True, "one second refills a full bucket");
        }
    }

    [Test]
    public void TryReserve_AboveMaxVerifyGas_IsNotCharged()
    {
        PeerValidationGasBudget budget = Budget(gasPerSecond: 300_000, maxVerifyGas: 300_000);
        Assert.That(budget.TryReserve(300_000), Is.True);

        Assert.That(budget.TryReserve(300_001), Is.True,
            "the pool rejects a transaction above MAX_VERIFY_GAS before validating it, so the budget must let it through");
    }
}

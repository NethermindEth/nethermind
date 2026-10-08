// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.TxPool.Test;

public class PeerValidationSharesTests
{
    private const long HeadBudget = 1_000;

    private readonly object _attacker = new();
    private readonly object _honest = new();

    [Test]
    public void HasShare_ALonePeer_GetsTheWholeHeadBudget()
    {
        PeerValidationShares shares = new(HeadBudget);
        Assert.That(shares.HasShare(_attacker, headGeneration: 1), Is.True);

        shares.Charge(_attacker, headGeneration: 1, ticks: HeadBudget - 1);
        Assert.That(shares.HasShare(_attacker, headGeneration: 1), Is.True);

        shares.Charge(_attacker, headGeneration: 1, ticks: 1);
        Assert.That(shares.HasShare(_attacker, headGeneration: 1), Is.False);
    }

    [Test]
    public void HasShare_ASecondActivePeer_HalvesTheShare()
    {
        PeerValidationShares shares = new(HeadBudget);
        shares.HasShare(_attacker, headGeneration: 1);
        shares.Charge(_attacker, headGeneration: 1, ticks: HeadBudget / 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(shares.HasShare(_honest, headGeneration: 1), Is.True, "a newly active peer starts with its share");
            Assert.That(shares.HasShare(_attacker, headGeneration: 1), Is.False, "two active peers split the head budget");
        }
    }

    [Test]
    public void HasShare_ANewHead_ResetsTheShares()
    {
        PeerValidationShares shares = new(HeadBudget);
        shares.HasShare(_attacker, headGeneration: 1);
        shares.Charge(_attacker, headGeneration: 1, ticks: HeadBudget);

        Assert.That(shares.HasShare(_attacker, headGeneration: 2), Is.True);
    }

    [Test]
    public void Charge_ForAPassedHead_IsIgnored()
    {
        PeerValidationShares shares = new(HeadBudget);
        shares.HasShare(_attacker, headGeneration: 2);

        shares.Charge(_attacker, headGeneration: 1, ticks: HeadBudget);

        Assert.That(shares.HasShare(_attacker, headGeneration: 2), Is.True, "work charged to a stale head must not spend the current share");
    }
}

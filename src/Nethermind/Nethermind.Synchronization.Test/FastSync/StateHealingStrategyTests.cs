// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.SnapSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync;

[Parallelizable(ParallelScope.All)]
public class StateHealingStrategyTests
{
    [TestCase(true, true, true, true, true, TestName = "BALs at the pivot and BAL healing available")]
    [TestCase(true, true, true, false, false, TestName = "Pivot predates block access lists")]
    [TestCase(true, false, true, true, false, TestName = "BAL healing disabled")]
    [TestCase(true, true, false, true, false, TestName = "State backend cannot BAL heal")]
    [TestCase(false, true, true, true, false, TestName = "Snap sync disabled")]
    public void Decides_the_heal_path_from_the_pivot(bool snapSync, bool balHealing, bool balHealingSupported, bool balPivot, bool expected)
    {
        StateHealingStrategy strategy = CreateStrategy(snapSync, balHealing, balHealingSupported);
        int fired = 0;
        strategy.Changed += () => fired++;

        strategy.SetPivot(Pivot(balPivot));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(strategy.CanBalHeal, Is.EqualTo(expected));
            Assert.That(fired, Is.EqualTo(expected ? 1 : 0));
        }
    }

    [Test]
    public void Withholds_the_decision_until_a_pivot_is_set() =>
        Assert.That(CreateStrategy().CanBalHeal, Is.False);

    [Test]
    public void Keeps_bal_healing_on_once_decided()
    {
        StateHealingStrategy strategy = CreateStrategy();
        int fired = 0;
        strategy.Changed += () => fired++;

        strategy.SetPivot(Pivot(balPivot: true));
        // snap/2 drops GetTrieNodes, so a later pivot must not send this node back to requesting them.
        strategy.SetPivot(Pivot(balPivot: false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(strategy.CanBalHeal, Is.True);
            Assert.That(fired, Is.EqualTo(1));
        }
    }

    private static StateHealingStrategy CreateStrategy(bool snapSync = true, bool balHealing = true, bool balHealingSupported = true)
    {
        IBalHealing healing = Substitute.For<IBalHealing>();
        healing.IsAvailable.Returns(balHealingSupported);
        return new StateHealingStrategy(
            new SyncConfig { SnapSync = snapSync, BalHealing = balHealing },
            new Lazy<IBalHealing>(healing),
            LimboLogs.Instance);
    }

    private static BlockHeader Pivot(bool balPivot) =>
        Build.A.BlockHeader.WithNumber(100).WithBlockAccessListHash(balPivot ? TestItem.KeccakA : null).TestObject;
}

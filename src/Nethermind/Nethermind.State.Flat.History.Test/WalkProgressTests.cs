// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Logging;
using Nethermind.State.Flat.History.Walk;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

[TestFixture]
public class WalkProgressTests
{
    [Test]
    public void Eta_LongMainnetWalk_DoesNotOverflow()
    {
        // 512 items x 10,000 units at 27% done after 69 h: elapsed ticks * remaining exceeds long.MaxValue (#13760).
        const long total = 512L * 10_000;
        const long done = total * 27 / 100;
        TimeSpan elapsed = TimeSpan.FromHours(69);
        Assert.That(() => elapsed * (total - done), Throws.TypeOf<OverflowException>(), "the old multiply-first order overflows here");

        Assert.That(WalkProgress.Eta(elapsed, total - done, done), Is.EqualTo("7d 18h"));
    }

    [Test]
    public void Eta_BeyondTimeSpanRange_IsUnknown() =>
        Assert.That(WalkProgress.Eta(TimeSpan.FromDays(365), long.MaxValue, 1), Is.EqualTo("n/a"));

    [TestCase(0)]
    [TestCase(-1)]
    public void Eta_WithoutProgressThisRun_IsUnknown(double doneThisRun) =>
        Assert.That(WalkProgress.Eta(TimeSpan.FromHours(1), 100, doneThisRun), Is.EqualTo("n/a"));

    [Test]
    public void A_storage_range_replaying_a_contract_reports_the_block_it_reached()
    {
        const int item = HistoryWalkRun.AccountPartitions + 0x7b;
        using WalkProgress progress = new(LimboLogs.Instance.GetClassLogger<WalkProgressTests>(), HistoryWalkRun.WorkItems, 0, 26_000_000);
        progress.ScanningKeySpace(item, 345, 1_000);
        progress.Replaying(item, 14_203_112);

        Assert.That(progress.Report(), Does.Contain("storage 0x7b replay 34.5% (block 14,203,112 / 26,000,000)"),
            "the key-space share stays put while one contract replays, so the block it reached is the only sign the range is moving");
    }

    [Test]
    public void A_storage_range_back_to_scanning_drops_the_replay_block()
    {
        const int item = HistoryWalkRun.AccountPartitions + 0x7b;
        using WalkProgress progress = new(LimboLogs.Instance.GetClassLogger<WalkProgressTests>(), HistoryWalkRun.WorkItems, 0, 26_000_000);
        progress.Replaying(item, 14_203_112);
        progress.ScanningKeySpace(item, 400, 1_000);

        string report = progress.Report();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(report, Does.Contain("storage 0x7b scan 40.0%"));
            Assert.That(report, Does.Not.Contain("(block"), "a finished contract's replay block must not stick to the scan that follows it");
        }
    }

    [Test]
    public void An_account_partition_replay_keeps_reporting_its_share_without_a_block()
    {
        using WalkProgress progress = new(LimboLogs.Instance.GetClassLogger<WalkProgressTests>(), HistoryWalkRun.WorkItems, 0, 1_000);
        progress.Replaying(0x05, 500);

        Assert.That(progress.Report(), Does.Contain("accounts 0x05 replay 50.0%").And.Not.Contain("(block"));
    }
}

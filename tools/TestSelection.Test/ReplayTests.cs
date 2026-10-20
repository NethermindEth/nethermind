// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.TestSelection.Journal;
using Nethermind.TestSelection.Replay;
using NUnit.Framework;

namespace Nethermind.TestSelection.Test;

[TestFixture]
public class ReplayTests
{
    private long _run;

    [SetUp]
    public void SetUp()
    {
        _run = long.Parse(Fixtures.RunWithoutHoldout(start: 3));
        Assert.That(Fixtures.Unsampled(_run.ToString(), "test_mul", "Cancun"), Is.True, "precondition: the sample skips test_mul");
    }

    [Test]
    public void Replay_ReportsHiddenFailures_AndLeavesOutRunsThatUsedTheSelection()
    {
        string[] evm = [$"{Fixtures.Evm}/VirtualMachine.cs"];
        string[] jsonRpc = [$"{Fixtures.JsonRpc}/Eth.cs"];
        JournalEntry[] journal =
        [
            Fixtures.Entry(1, 0, JournalEntry.Master, evm, [Fixtures.EestFailure("test_add")], JournalEntry.Push),
            Fixtures.Entry(2, 8, "b2", evm, [Fixtures.EestFailure("test_add")]),
            Fixtures.Entry(_run, 9, "b3", jsonRpc, [Fixtures.EestFailure("test_mul")]),
            Fixtures.Entry(_run + 1, 9, "b4", jsonRpc, [Fixtures.EestFailure("test_mul")], selected: true),
        ];

        ReplayReport report = new SelectionReplay(CostsTests.Inventory, Fixtures.Projects).Run(journal, Fixtures.Now.AddDays(7));

        Assert.That(report.Measured, Is.EqualTo(2), "the selected run cannot show what it skipped");
        Assert.That(report.Eest.Hidden.Select(static hidden => hidden.Entry.Run), Is.EqualTo(new[] { _run }), "JsonRpc never failed with test_mul");
    }

    [Test]
    public void Replay_ReportsNoEnvironmentFailureAsHidden()
    {
        JournalEntry[] journal =
        [
            Fixtures.Entry(_run, 9, "b3", [$"{Fixtures.JsonRpc}/Eth.cs"], [Fixtures.EestFailure("test_mul")]),
            Fixtures.Entry(_run + 1, 9, JournalEntry.Master, [], [Fixtures.EestFailure("test_mul")], JournalEntry.Push),
        ];
        Assert.That(new SelectionReplay(CostsTests.Inventory, Fixtures.Projects).Run(journal, Fixtures.Now).Eest.Hidden, Is.Empty,
            "test_mul failed on master at the same time");
    }

    [Test]
    public void MarkdownCell_KeepsBranchNamesFromBreakingTheTable() =>
        Assert.That(ReplayReport.MarkdownCell("a|b`c<script>"), Is.EqualTo("a\\|b'c&lt;script>"), "names come from pull requests");
}

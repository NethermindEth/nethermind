// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.TestSelection.Journal;
using NUnit.Framework;

namespace Nethermind.TestSelection.Test;

[TestFixture]
public class JournalTests
{
    [TestCase("journal.jsonl", TestName = "Plain")]
    [TestCase("journal.jsonl.gz", TestName = "Gzipped")]
    public void Journal_WrittenAndRead_KeepsEntriesInTimeOrder(string name)
    {
        using TempDirectory directory = new();
        JournalEntry later = Fixtures.Entry(2, 1, "b", ["a.cs"], [Fixtures.EestFailure("test_add")]);
        JournalEntry earlier = Fixtures.Entry(1, 0, "a", ["b.cs"], selected: true);

        RunJournal.Write(directory.File(name), [later, earlier]);
        IReadOnlyList<JournalEntry> read = RunJournal.Read(directory.File(name));

        Assert.That(read.Select(static entry => entry.Run), Is.EqualTo(new long[] { 1, 2 }), "the journal is read oldest first");
        Assert.That(read[0].Selected, Is.True, "the selected flag survives");
        Assert.That(read[1].Failed.Tests, Is.EqualTo(later.Failed.Tests), "the failures survive");
    }

    [Test]
    public void Journal_WhenMissing_IsEmpty()
    {
        using TempDirectory directory = new();
        Assert.That(RunJournal.Read(directory.File("missing.jsonl")), Is.Empty, "a new journal starts empty");
    }

    [Test]
    public void Journal_WithNullFailuresFromOlderWriters_ReadsAsNoFailures()
    {
        using TempDirectory directory = new();
        File.WriteAllText(directory.File("journal.jsonl"),
            """{"run": 7, "at": "2026-10-01T00:00:00+00:00", "event": "push", "branch": "master", "files": ["a"], "failed": null}""" + "\n");

        JournalEntry entry = RunJournal.Read(directory.File("journal.jsonl"))[0];

        Assert.That(entry.Failed.Tests, Is.Empty, "a successful run has no failures");
        Assert.That(entry.Selected, Is.False, "a missing selected flag means the run ran everything");
    }

    [Test]
    public void Failures_SplitEestTestsAndProjects_IgnoringHive()
    {
        Failures failures = new([Fixtures.EestFailure("test_add", "Prague"), "Nethermind.Evm.Test|Add", "Hive|rpc-compat"], ["EEST"]);

        Assert.That(failures.EestTests(), Is.EquivalentTo(new[] { new EestTest("test_add", "Prague") }), "EEST names test and fork");
        Assert.That(failures.Projects(), Is.EquivalentTo(new[] { "Nethermind.Evm.Test" }), "Hive is no test project");
    }
}

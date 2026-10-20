// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.TestSelection.Journal;
using NUnit.Framework;

namespace Nethermind.TestSelection.Test;

[TestFixture]
public class EnvironmentFilterTests
{
    private const string Flaky = "Nethermind.Evm.Test|Flaky";

    [TestCase(new string[0], new double[0], TestName = "Alone")]
    [TestCase(new[] { "other" }, new[] { 1.0 }, TestName = "OneOtherBranch")]
    [TestCase(new[] { "master", "a" }, new[] { 30.0, -30.0 }, TestName = "OutsideTheWindow")]
    public void Failure_OfTheChangeAlone_IsKept(string[] otherBranches, double[] hours) =>
        Assert.That(IsKept(otherBranches, hours), Is.True, "a failure the change caused teaches the model");

    [TestCase(new[] { "master" }, new[] { 5.0 }, TestName = "OnMaster")]
    [TestCase(new[] { "a", "b" }, new[] { -5.0, 20.0 }, TestName = "OnTwoOtherBranches")]
    [TestCase(new[] { "mine", "a", "b" }, new[] { 1.0, 1.0, 2.0 }, TestName = "TwoOthersBesidesItsOwnBranch")]
    public void Failure_SharedWithItsEnvironment_IsDropped(string[] otherBranches, double[] hours) =>
        Assert.That(IsKept(otherBranches, hours), Is.False, "a flaky test, an outage or a broken base");

    [Test]
    public void InfraFailures_AreKept()
    {
        JournalEntry entry = Failing(1, 0, "mine") with { Failed = new Failures([], ["Hive"]) };
        Assert.That(EnvironmentFilter.WithoutEnvironment([entry])[0].Failed.Infra, Is.EqualTo(new[] { "Hive" }), "infra is kept apart, not filtered");
    }

    private static bool IsKept(string[] otherBranches, double[] hours)
    {
        List<JournalEntry> journal = [Failing(1, 0, "mine")];
        journal.AddRange(otherBranches.Select((branch, i) => Failing(i + 2, hours[i], branch)));
        journal.Sort(static (left, right) => left.At.CompareTo(right.At));
        return EnvironmentFilter.WithoutEnvironment(journal).Single(static entry => entry.Run == 1).Failed.Tests.Count > 0;
    }

    [Test]
    public void Failure_SharedWithAPullRequestFromABranchNamedMaster_IsKept()
    {
        JournalEntry mine = Failing(1, 0, "mine");
        JournalEntry fork = Failing(2, 1, JournalEntry.Master) with { Event = JournalEntry.PullRequest };
        Assert.That(EnvironmentFilter.WithoutEnvironment([mine, fork])[0].Failed.Tests, Is.Not.Empty, "a fork can name its branch master");
    }

    // A run on master is a push; every other branch runs pull requests.
    private static JournalEntry Failing(long run, double hours, string branch) =>
        new(run, Fixtures.Now.AddHours(hours), branch == JournalEntry.Master ? JournalEntry.Push : JournalEntry.PullRequest, branch, [], new Failures([Flaky], []));
}

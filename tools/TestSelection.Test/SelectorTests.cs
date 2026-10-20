// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.TestSelection.Journal;
using Nethermind.TestSelection.Selection;
using NUnit.Framework;

namespace Nethermind.TestSelection.Test;

[TestFixture]
public class SelectorTests
{
    private string _run = null!;

    [SetUp]
    public void SetUp() => _run = Fixtures.RunWithoutHoldout();

    [TestCase(true, TestName = "EverythingChanged")]
    [TestCase(false, TestName = "HarnessChanged")]
    public void Decide_OnBuildWideOrHarnessChange_RunsEverything(bool everything)
    {
        Change change = everything ? Change.Everything : Change.Of(new HashSet<string> { Projects.DirectoryOf("Ethereum.Test.Base") });
        Assert.That(Decide(new Selector(), change), Is.Null, "the change can alter every result");
    }

    [Test]
    public void Decide_ForAHoldoutRun_RunsEverything()
    {
        string holdout = Enumerable.Range(1, 1000).Select(static run => run.ToString()).First(static run => Sampling.IsSampled($"holdout|{run}", Selector.HoldoutRate));
        Assert.That(new Selector().Decide(holdout, Fixtures.Now, Change.Of(new HashSet<string> { Fixtures.Evm }), new HashSet<string>()), Is.Null,
            "holdout runs keep the history unbiased by the selection");
    }

    [Test]
    public void Decide_RunsDirectTestsAndTheTestsLinkedByHistory()
    {
        Selector selector = new();
        selector.Observe(Fixtures.Now.AddDays(-3), Change.Of(new HashSet<string> { Fixtures.Evm }),
            new HashSet<EestTest> { new("test_add", "Cancun") }, new HashSet<string> { "Nethermind.JsonRpc.Test" });

        RunSelection selection = Decide(selector, Change.Of(new HashSet<string> { Fixtures.Evm }))!;

        Assert.That(selection.Projects, Is.EquivalentTo(new[] { "Nethermind.Evm.Test", "Nethermind.JsonRpc.Test" }), "direct references and the history link");
        Assert.That(selection.Tests, Is.EquivalentTo(new[] { "test_add" }), "the EEST test failed with Evm before");
    }

    [TestCase(-3, Selector.ForkLinkMinTests, true, TestName = "EnoughRecentTests")]
    [TestCase(-3, Selector.ForkLinkMinTests - 1, false, TestName = "TooFewTests")]
    [TestCase(-20, Selector.ForkLinkMinTests, false, TestName = "OutsideTheWindow")]
    public void Decide_RunsAForkWhoseTestsTheChangeBrokeRecently(int days, int tests, bool runsFork)
    {
        Selector selector = new();
        selector.Observe(Fixtures.Now.AddDays(days), Change.Of(new HashSet<string> { Fixtures.Evm }),
            Enumerable.Range(0, tests).Select(static i => new EestTest($"test_{i}", "Prague")).ToHashSet(), new HashSet<string>());

        Assert.That(Decide(selector, Change.Of(new HashSet<string> { Fixtures.Evm }))!.Forks.Contains("Prague"), Is.EqualTo(runsFork),
            "a fork under development breaks in new places");
    }

    [Test]
    public void Decide_LeavesTheLegacySuiteThatEestCoversToFullRuns()
    {
        string legacy = Projects.DirectoryOf("Ethereum.Legacy.Blockchain.Test");
        Selector selector = new();
        selector.Links.LinkProject(Fixtures.Evm, "Ethereum.Legacy.Blockchain.Test");
        selector.Links.LinkProject(legacy, "Ethereum.Legacy.Blockchain.Test");

        Assert.That(Decide(selector, Change.Of(new HashSet<string> { Fixtures.Evm }))!.Projects, Does.Not.Contain("Ethereum.Legacy.Blockchain.Test"),
            "EEST covers what the legacy suite would catch");
        Assert.That(Decide(selector, Change.Of(new HashSet<string> { legacy }))!.Projects, Does.Contain("Ethereum.Legacy.Blockchain.Test"),
            "a change to the suite itself still runs it");
    }

    [TestCase(Selector.MassTests, true, TestName = "AtTheTestLimit")]
    [TestCase(Selector.MassTests + 1, false, TestName = "BeyondTheTestLimit")]
    public void Observe_LearnsTestLinksUpToTheMassFailureLimit(int tests, bool learns)
    {
        Selector selector = new();
        selector.Observe(Fixtures.Now, Change.Of(new HashSet<string> { Fixtures.Evm }),
            Enumerable.Range(0, tests).Select(static i => new EestTest($"test_{i}", "Prague")).ToHashSet(), new HashSet<string>());
        Assert.That(selector.Links.Tests.Count > 0, Is.EqualTo(learns), "a mass failure would tie the change to everything");
    }

    [TestCase(Selector.MassProjects, true, TestName = "AtTheProjectLimit")]
    [TestCase(Selector.MassProjects + 1, false, TestName = "BeyondTheProjectLimit")]
    public void Observe_LearnsProjectLinksUpToTheMassFailureLimit(int projects, bool learns)
    {
        Selector selector = new();
        selector.Observe(Fixtures.Now, Change.Of(new HashSet<string> { Fixtures.Evm }), new HashSet<EestTest>(),
            Enumerable.Range(0, projects).Select(static i => $"P{i}.Test").ToHashSet());
        Assert.That(selector.Links.TestProjects.Count > 0, Is.EqualTo(learns), "a mass failure would tie the change to everything");
    }

    [Test]
    public void Observe_OfAChangeThatTouchedEverything_LearnsNothing()
    {
        Selector selector = new();
        selector.Observe(Fixtures.Now, Change.Everything, new HashSet<EestTest> { new("test_add", "Cancun") }, new HashSet<string> { "Nethermind.Evm.Test" });
        Assert.That(selector.Links.Tests.Count + selector.Links.TestProjects.Count, Is.Zero, "no project is to blame");
    }

    [Test]
    public void Selection_SamplesAboutTwoPercentOfTheOtherProjects_Reproducibly()
    {
        RunSelection selection = Decide(new Selector(), Change.Of(new HashSet<string> { Fixtures.Evm }))!;
        string[] others = [.. Enumerable.Range(0, 2000).Select(static i => $"Nethermind.Other{i}.Test")];

        string[] kept = [.. others.Where(selection.KeepsProject)];

        Assert.That(kept.Length / (double)others.Length, Is.InRange(0.01, 0.03), "the sample rate is 2%");
        Assert.That(kept, Is.EqualTo(others.Where(project => Sampling.IsSampled($"{selection.Run}|{project}"))), "the draw depends on the run and the project only");
    }

    [Test]
    public void Trained_LearnsNothingFromTheEnvironment()
    {
        JournalEntry changed = Fixtures.Entry(1, 0, "mine", [$"{Fixtures.Evm}/A.cs"], ["Nethermind.JsonRpc.Test|Flaky"]);
        JournalEntry onMaster = Fixtures.Entry(2, 0, JournalEntry.Master, [], ["Nethermind.JsonRpc.Test|Flaky"], JournalEntry.Push);

        Assert.That(Selector.Trained([changed, onMaster], Fixtures.Projects).Links.TestProjects, Is.Empty, "master failed the same test at the same time");
        Assert.That(Selector.Trained([changed], Fixtures.Projects).Links.TestProjects[Fixtures.Evm], Is.EquivalentTo(new[] { "Nethermind.JsonRpc.Test" }),
            "alone, the failure is the change's");
    }

    private RunSelection? Decide(Selector selector, Change change) =>
        selector.Decide(_run, Fixtures.Now, change, Fixtures.Projects.DirectTests(change.Projects));
}

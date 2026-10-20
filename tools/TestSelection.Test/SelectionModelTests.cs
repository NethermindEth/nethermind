// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.TestSelection.Selection;
using NUnit.Framework;

namespace Nethermind.TestSelection.Test;

[TestFixture]
public class SelectionModelTests
{
    [Test]
    public void Model_WrittenAndRead_KeepsEveryLink()
    {
        using TempDirectory directory = new();
        SelectorLinks links = new();
        links.LinkTest(Fixtures.Evm, "test_add");
        links.LinkProject(Fixtures.Evm, "Nethermind.JsonRpc.Test");
        links.LinkFork(Fixtures.Evm, "Prague", Fixtures.Now, "test_mul");

        new SelectionModel(Fixtures.Now, Fixtures.Now.AddDays(-40), links).Write(directory.File("model.json.gz"));
        SelectionModel read = SelectionModel.Read(directory.File("model.json.gz"));

        Assert.That(read.Links.Tests[Fixtures.Evm], Is.EquivalentTo(new[] { "test_add" }), "test links survive");
        Assert.That(read.Links.TestProjects[Fixtures.Evm], Is.EquivalentTo(new[] { "Nethermind.JsonRpc.Test" }), "project links survive");
        Assert.That(read.Links.Forks[(Fixtures.Evm, "Prague")], Is.EqualTo(new[] { (Fixtures.Now, "test_mul") }), "fork links survive with their time");
        Assert.That(read.HistoryFrom, Is.EqualTo(Fixtures.Now.AddDays(-40)), "the history start survives");
    }

    [TestCase(0, 40, null, TestName = "FreshWithEnoughHistory")]
    [TestCase(2, 40, "the model was built", TestName = "Stale")]
    [TestCase(0, 10, "the model holds history from", TestName = "Young")]
    public void TrustProblem_RejectsAStaleOrYoungModel(int builtDaysAgo, int historyDays, string? problem)
    {
        using TempDirectory directory = new();
        new SelectionModel(Fixtures.Now.AddDays(-builtDaysAgo), Fixtures.Now.AddDays(-historyDays), new SelectorLinks()).Write(directory.File("model.json"));

        string? found = SelectionModel.TrustProblem(directory.File("model.json"), Fixtures.Now);

        Assert.That(found, problem is null ? Is.Null : Does.StartWith(problem), "only a fresh model with 28 days of history is trusted");
    }

    [Test]
    public void TrustProblem_WithoutAModel_IsReported()
    {
        using TempDirectory directory = new();
        Assert.That(SelectionModel.TrustProblem(directory.File("missing.json"), Fixtures.Now), Is.EqualTo("no selection model"), "no model, no selection");
    }
}

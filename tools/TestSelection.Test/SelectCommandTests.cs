// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using Nethermind.TestSelection.Commands;
using Nethermind.TestSelection.Costs;
using Nethermind.TestSelection.Files;
using Nethermind.TestSelection.Selection;
using NUnit.Framework;

namespace Nethermind.TestSelection.Test;

[TestFixture]
public class SelectCommandTests
{
    private string _run = null!;

    [SetUp]
    public void SetUp() => _run = Fixtures.RunSamplingNone("Nethermind.Evm.Test", "Nethermind.JsonRpc.Test");

    [Test]
    public void Outputs_OnAPullRequest_KeepTheProjectsTheChangeAffects()
    {
        RunSelection selection = new(_run, new HashSet<string>(), new HashSet<string>(), new HashSet<string> { "Nethermind.Evm.Test" });

        Dictionary<string, string> outputs = Parse(SelectProjectsCommand.Outputs(Workflow(), new PullRequestDecision(selection, "")));

        Assert.That(JsonNode.Parse(outputs["matrices"])!["tests"]!["project"]!.ToJsonString(), Is.EqualTo("""["Nethermind.Evm.Test"]"""), "only the affected project");
        Assert.That((outputs["any"], outputs["selected"]), Is.EqualTo(("true", "true")), "a job runs and the run used the selection");
    }

    [Test]
    public void Outputs_WithNoAffectedProject_SkipEveryJob()
    {
        RunSelection selection = new(_run, new HashSet<string>(), new HashSet<string>(), new HashSet<string>());
        Dictionary<string, string> outputs = Parse(SelectProjectsCommand.Outputs(Workflow(), new PullRequestDecision(selection, "")));
        Assert.That(outputs, Is.EquivalentTo(new Dictionary<string, string> { ["matrices"] = """{"tests":null}""", ["any"] = "false", ["selected"] = "true" }),
            "a skipped job needs a null matrix");
    }

    [Test]
    public void Outputs_WithoutASelection_KeepEveryMatrixWhole()
    {
        Dictionary<string, string> outputs = Parse(SelectProjectsCommand.Outputs(Workflow(), new PullRequestDecision(null, "not a pull request")));
        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(outputs["matrices"]), Workflow()), Is.True, "push, merge queue and ci-full run everything");
        Assert.That((outputs["any"], outputs["selected"]), Is.EqualTo(("true", "false")), "nothing was selected");
    }

    [TestCase(2, 40, TestName = "Stale")]
    [TestCase(0, 10, TestName = "Young")]
    public void Decide_WithAnUntrustedModel_RunsEverything(int builtDaysAgo, int historyDays)
    {
        using TempDirectory directory = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        new SelectionModel(now.AddDays(-builtDaysAgo), now.AddDays(-historyDays), new SelectorLinks()).Write(directory.File("model.json"));
        File.WriteAllText(directory.File("changed.txt"), $"{Fixtures.Evm}/VirtualMachine.cs\n");

        PullRequestDecision decision = PullRequestSelection.Decide(RepositoryRoot.Find(), directory.File("model.json"), directory.File("changed.txt"), _run, now);

        Assert.That(decision.Selection, Is.Null, $"an untrusted model selects nothing: {decision.Reason}");
    }

    [Test]
    public void Decide_WithATrustedModel_RunsTheTestsOfTheChangedProjects()
    {
        using TempDirectory directory = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SelectorLinks links = new();
        links.LinkProject(Fixtures.Evm, "Nethermind.JsonRpc.Test");
        new SelectionModel(now, now.AddDays(-40), links).Write(directory.File("model.json"));
        File.WriteAllText(directory.File("changed.txt"), $"{Fixtures.Evm}/VirtualMachine.cs\n");

        PullRequestDecision decision = PullRequestSelection.Decide(RepositoryRoot.Find(), directory.File("model.json"), directory.File("changed.txt"), _run, now);

        Assert.That(decision.Selection!.Projects, Is.SupersetOf(new[] { "Nethermind.Evm.Test", "Nethermind.JsonRpc.Test" }),
            "the repository's Evm.Test references Evm, and the model links JsonRpc.Test to it");
    }

    [Test]
    public void WriteEest_WithoutASelection_WritesTheFullMatrix_AndReadsNoCosts()
    {
        using TempDirectory directory = new();
        string result = SelectEestCommand.Write(new PullRequestDecision(null, "no selection model"), CostsTests.NethtestMatrix,
            static () => throw new AssertionException("the costs are not read"), directory.File("out.json"), directory.File("filters"));

        Assert.That(JsonNode.DeepEquals(JsonFiles.ReadNode(directory.File("out.json")), CostsTests.NethtestMatrix), Is.True, "every EEST test runs");
        Assert.That(result, Does.Contain("no selection model"), "the reason is reported");
    }

    [Test]
    public void WriteEest_WithASelection_WritesFiltersAndTheReducedMatrix()
    {
        using TempDirectory directory = new();
        RunSelection addOnly = new(_run, new HashSet<string> { "test_add" }, new HashSet<string>(), new HashSet<string>());
        Assert.That(Fixtures.Unsampled(_run, "test_mul", "Cancun") && Fixtures.Unsampled(_run, "test_addmod", "Cancun"), Is.True, "precondition: the sample skips them");

        SelectEestCommand.Write(new PullRequestDecision(addOnly, ""), CostsTests.NethtestMatrix, static () => CostsTests.Inventory, directory.File("out.json"), directory.File("filters"));

        Assert.That(JsonFiles.ReadNode(directory.File("out.json"))["include"]!.AsArray().Count, Is.EqualTo(2), "one blockTest chunk and the unknown txTest lane");
        Assert.That(File.Exists(Path.Combine(directory.File("filters"), "blockTest-sequential.txt")), Is.True, "the lane with a selected test gets a filter");
    }

    [Test]
    public void WriteFilters_WritesOneFilePerLaneWithASelectedTest()
    {
        using TempDirectory directory = new();
        RunSelection addOnly = new(_run, new HashSet<string> { "test_add" }, new HashSet<string>(), new HashSet<string>());
        Assert.That(Fixtures.Unsampled(_run, "test_mul", "Cancun"), Is.True, "precondition: the sample skips the state lane");

        SelectEestCommand.WriteFilters(directory.Path, CostsTests.Inventory.Lanes(addOnly));

        Assert.That(Directory.GetFiles(directory.Path).Select(Path.GetFileName), Is.EqualTo(new[] { "blockTest-sequential.txt" }), "the state lane selects nothing");
    }

    [Test]
    public void WriteFilters_RefusesALaneThatIsNoPlainName()
    {
        using TempDirectory directory = new();
        Dictionary<string, LaneSelection> lanes = new() { ["../../escape|x"] = new LaneSelection() };
        Assert.Throws<InvalidDataException>(() => SelectEestCommand.WriteFilters(directory.Path, lanes), "a lane name becomes a file name");
    }

    private static JsonObject Workflow() => new() { ["tests"] = TestMatrixTests.Matrix };

    private static Dictionary<string, string> Parse(string outputs) =>
        outputs.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static line => line.Split('=', 2)).ToDictionary(static parts => parts[0], static parts => parts[1]);
}

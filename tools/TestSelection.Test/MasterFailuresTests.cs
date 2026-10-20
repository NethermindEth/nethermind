// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Text.Json.Nodes;
using Nethermind.TestSelection.Collection;
using Nethermind.TestSelection.GitHub;
using Nethermind.TestSelection.Journal;
using Nethermind.TestSelection.MasterFailures;
using Nethermind.TestSelection.Selection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.TestSelection.Test;

[TestFixture]
public class MasterFailuresTests
{
    private const string EvmFailure = "Nethermind.Evm.Test|Add";
    private static readonly DateTimeOffset GreenAt = Fixtures.Now;

    [Test]
    public void Regressions_KeepNewTestFailures_AndLeaveOutHiveAndInfra()
    {
        Failures failed = new([EvmFailure, "Hive|rpc-compat"], ["EEST"]);
        Assert.That(Attribution.Regressions(failed, [], GreenAt), Is.EqualTo(new[] { EvmFailure }), "Hive and infra are not regressions");
    }

    [Test]
    public void Regressions_OfATestTwoBranchesFailedInTheDayBefore_AreFlaky()
    {
        JournalEntry[] journal = [Failing("a", -3), Failing("b", -20)];
        Assert.That(Attribution.Regressions(new Failures([EvmFailure], []), journal, GreenAt), Is.Empty, "the test was failing before the merges");
    }

    [Test]
    public void Regressions_FailuresAfterTheMergesOrLongBefore_DoNotMakeATestFlaky()
    {
        JournalEntry[] journal = [Failing("a", 1), Failing("b", 2), Failing("c", -3), Failing("d", -30), Failing(JournalEntry.Master, -2)];
        Assert.That(Attribution.Regressions(new Failures([EvmFailure], []), journal, GreenAt), Is.EqualTo(new[] { EvmFailure }),
            "a broken master breaks the branches after it");
    }

    [Test]
    public void Suspects_TheMergeThatChangedWhatTheFailingTestReferencesWins() =>
        Assert.That(Suspects([Merge(1, $"{Fixtures.Core}/A.cs"), Merge(2, $"{Fixtures.Evm}/B.cs"), Merge(3, "README.md")], [EvmFailure]),
            Is.EqualTo(new[] { (2, Attribution.Reasons[0]) }), "Evm.Test references Evm");

    [Test]
    public void Suspects_TheHistoryLinksAMergeToTheFailure()
    {
        SelectorLinks links = new();
        links.LinkProject(Fixtures.JsonRpc, "Nethermind.Evm.Test");
        Assert.That(Suspects([Merge(1, $"{Fixtures.Core}/A.cs"), Merge(2, $"{Fixtures.JsonRpc}/B.cs")], [EvmFailure], links),
            Is.EqualTo(new[] { (2, Attribution.Reasons[1]) }), "JsonRpc broke Evm.Test before");
    }

    [Test]
    public void Suspects_TheHistoryLinksAMergeToAnEestFailure()
    {
        SelectorLinks links = new();
        links.LinkTest(Fixtures.Core, "test_add");
        Assert.That(Suspects([Merge(1, $"{Fixtures.JsonRpc}/A.cs"), Merge(2, $"{Fixtures.Core}/B.cs")], [Fixtures.EestFailure("test_add", "Prague")], links),
            Is.EqualTo(new[] { (2, Attribution.Reasons[1]) }), "Core broke test_add before");
    }

    [Test]
    public void Suspects_AMergeThatChangedATransitiveDependencyIsTheNextSuspect() =>
        Assert.That(Suspects([Merge(1, $"{Fixtures.Core}/A.cs"), Merge(2, $"{Fixtures.JsonRpc}/B.cs"), Merge(3, "Directory.Packages.props")], [EvmFailure]),
            Is.EqualTo(new[] { (1, Attribution.Reasons[2]) }), "Evm.Test reaches Core through Evm");

    [Test]
    public void Suspects_MergesWithTheSameLinkAreAllSuspects() =>
        Assert.That(Suspects([Merge(1, $"{Fixtures.Evm}/A.cs"), Merge(2, $"{Fixtures.EvmTest}/B.cs")], [EvmFailure]).Select(static s => s.Number),
            Is.EqualTo(new[] { 1, 2 }), "both changed what the failing test builds from");

    [Test]
    public void Suspects_OfAnEestFailure_AHarnessChangeComesFirst_AndAnyProjectChangeNext()
    {
        string[] eest = [Fixtures.EestFailure("test_add", "Prague")];
        Assert.That(Suspects([Merge(1, $"{Fixtures.JsonRpc}/A.cs"), Merge(2, $"{Projects.DirectoryOf("Ethereum.Test.Base")}/Runner.cs")], eest),
            Is.EqualTo(new[] { (2, Attribution.Reasons[0]) }), "the harness can alter any EEST result");
        Assert.That(Suspects([Merge(1, $"{Fixtures.JsonRpc}/A.cs"), Merge(3, "README.md")], eest), Is.EqualTo(new[] { (1, Attribution.Reasons[2]) }),
            "EEST runs the whole client");
    }

    [Test]
    public void Suspects_ABuildWideChangeIsTheLastSuspect() =>
        Assert.That(Suspects([Merge(1, "Directory.Packages.props"), Merge(2, "README.md")], [EvmFailure]), Is.EqualTo(new[] { (1, Attribution.Reasons[3]) }),
            "a package bump can break anything");

    [Test]
    public void Suspects_ASingleMergeIsTheSuspect_AndUnlinkedMergesNameNobody()
    {
        Assert.That(Suspects([Merge(1, "README.md")], [EvmFailure]), Is.EqualTo(new[] { (1, Attribution.OnlyMerge) }), "nothing else changed");
        Assert.That(Suspects([Merge(1, "README.md"), Merge(2, "docs/a.md")], [EvmFailure]), Is.Empty, "no merge explains the failure");
    }

    [Test]
    public void IssueBody_HoldsTheMarkerTheTestsAndTheSuspects()
    {
        string[] tests = [.. Enumerable.Range(0, IssueText.MaxListedTests + 2).Select(static i => $"Nethermind.Evm.Test|T{i}")];
        string body = IssueText.Body("Nethermind tests", "https://run", new string('b', 40), new string('a', 40), tests, [new Suspect(Merge(7, "x"), Attribution.Reasons[0])]);

        Assert.That(body, Does.Contain(IssueText.Marker("Nethermind tests")), "later failures find the issue by its marker");
        Assert.That(body, Does.Contain($"- #7 by @author7: {Attribution.Reasons[0]}"), "the suspect and why");
        Assert.That(body, Does.Contain("`Nethermind.Evm.Test|T0`").And.Not.Contain($"T{IssueText.MaxListedTests}`"), "the list is cut");
        Assert.That(body, Does.Contain("and 2 more"), "the cut is counted");
        Assert.That(IssueText.Body("W", "u", "b", "a", tests[..1], []), Does.Contain("No merge since the last green run"), "no suspect is said");
    }

    [Test]
    public async Task Report_OfARegression_OpensAnIssueAssignedToTheSuspect()
    {
        Reporter reporter = new("failure", [EvmFailure]);
        string result = await reporter.ReportAsync();

        await reporter.Issues.Received(1).CreateAsync(Arg.Is<string>(title => title.Contains("Nethermind.Evm.Test Add")), Arg.Is<string>(body => body.Contains("#5")),
            Arg.Is<IReadOnlyCollection<string>>(authors => authors.SequenceEqual(new[] { "author5" })), Arg.Any<CancellationToken>());
        Assert.That(result, Does.StartWith("opened an issue"), "the regression is reported once");
    }

    [Test]
    public async Task Report_OfAJobThatFailedWithoutAFailingTest_OpensNothing()
    {
        Reporter reporter = new("failure", []);
        string result = await reporter.ReportAsync();
        Assert.That(reporter.Issues.ReceivedCalls().Count(static call => call.GetMethodInfo().Name is "CreateAsync" or "CommentAsync"), Is.Zero,
            $"an auth or runner failure is infra, no regression: {result}");
    }

    [Test]
    public async Task Report_OfANewFailureWhileAnIssueIsOpen_CommentsOnIt()
    {
        Reporter reporter = new("failure", [EvmFailure], openIssue: 3);
        await reporter.ReportAsync();
        await reporter.Issues.Received(1).CommentAsync(3, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await reporter.Issues.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
    }

    [TestCase(3, TestName = "WithAnOpenIssue")]
    [TestCase(null, TestName = "WithoutAnOpenIssue")]
    public async Task Report_OfAGreenRun_ClosesTheOpenIssue(int? openIssue)
    {
        Reporter reporter = new("success", [], openIssue);
        await reporter.ReportAsync();
        await reporter.Issues.Received(openIssue is null ? 0 : 1).CloseAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LastGreen_IsTheNewestSuccessfulMasterPushBeforeTheFailure()
    {
        IGitHubApi api = Substitute.For<IGitHubApi>();
        api.Repository.Returns("o/r");
        api.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(JsonNode.Parse("""
            {"workflow_runs": [{"id": 3, "created_at": "2026-10-01T12:00:00Z"}, {"id": 2, "created_at": "2026-10-01T10:00:00Z"}, {"id": 1, "created_at": "2026-10-01T08:00:00Z"}]}
            """)!);
        MasterFailureReporter reporter = new(api, null!, null!, Fixtures.Projects, new SelectorLinks());

        JsonNode? green = await reporter.LastGreenAsync(JsonNode.Parse("""{"workflow_id": 7, "created_at": "2026-10-01T11:00:00Z"}""")!, CancellationToken.None);

        Assert.That(green!["id"]!.GetValue<int>(), Is.EqualTo(2), "a run created after the failure cannot be its base");
        await api.Received().GetAsync(Arg.Is<string>(path => path.Contains("branch=master&event=push&status=success")), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Create_WhenTheAuthorCannotBeAssigned_StillOpensTheIssue()
    {
        IGitHubApi api = Substitute.For<IGitHubApi>();
        api.Repository.Returns("o/r");
        api.SendAsync(HttpMethod.Post, "repos/o/r/issues", Arg.Is<JsonNode>(body => body["assignees"] != null), Arg.Any<CancellationToken>())
            .ThrowsAsync(new GitHubException("invalid assignee", HttpStatusCode.UnprocessableEntity));

        await new IssueTracker(api).CreateAsync("title", "body", ["dependabot[bot]"], CancellationToken.None);

        await api.Received(1).SendAsync(HttpMethod.Post, "repos/o/r/issues", Arg.Is<JsonNode>(body => body["assignees"] == null), Arg.Any<CancellationToken>());
    }

    private static JournalEntry Failing(string branch, double hours) =>
        new(Random.Shared.NextInt64(), GreenAt.AddHours(hours), JournalEntry.PullRequest, branch, [], new Failures([EvmFailure], []));

    private static Merge Merge(int number, params string[] files) => new(new string('0', 40 - number.ToString().Length) + number, number, $"author{number}", files);

    private static (int? Number, string Reason)[] Suspects(Merge[] merges, string[] tests, SelectorLinks? links = null) =>
        [.. Attribution.Suspects(merges, tests, Fixtures.Projects, links ?? new SelectorLinks()).Select(static s => (s.Merge.Number, s.Reason))];

    /// <summary>A reporter over a fake GitHub: one master run of the given conclusion and failures, green an hour before.</summary>
    private sealed class Reporter
    {
        private readonly MasterFailureReporter _reporter;

        public Reporter(string conclusion, string[] failures, int? openIssue = null)
        {
            IGitHubApi api = Substitute.For<IGitHubApi>();
            api.Repository.Returns("o/r");
            api.GetAsync("repos/o/r/actions/runs/9", Arg.Any<CancellationToken>()).Returns(new JsonObject
            {
                ["id"] = 9L,
                ["name"] = "Nethermind tests",
                ["conclusion"] = conclusion,
                ["html_url"] = "https://run",
                ["head_sha"] = new string('b', 40),
                ["workflow_id"] = 1,
                ["created_at"] = GreenAt.AddHours(1).ToString("O"),
            });
            api.GetAsync(Arg.Is<string>(path => path.Contains("/workflows/")), Arg.Any<CancellationToken>()).Returns(new JsonObject
            {
                ["workflow_runs"] = new JsonArray(new JsonObject { ["head_sha"] = new string('a', 40), ["created_at"] = GreenAt.ToString("O") }),
            });
            api.GetAsync(Arg.Is<string>(path => path.Contains("/compare/")), Arg.Any<CancellationToken>()).Returns(new JsonObject
            {
                ["commits"] = new JsonArray(new JsonObject { ["sha"] = "c5" }, new JsonObject { ["sha"] = "c6" }),
            });
            api.GetAsync("repos/o/r/commits/c5/pulls", Arg.Any<CancellationToken>()).Returns(JsonNode.Parse("""[{"number": 5, "user": {"login": "author5"}}]""")!);
            api.GetAsync("repos/o/r/commits/c6/pulls", Arg.Any<CancellationToken>()).Returns(JsonNode.Parse("""[{"number": 6, "user": {"login": "author6"}}]""")!);
            api.GetPagesAsync("repos/o/r/commits/c5", Arg.Any<CancellationToken>()).Returns(_ => Fixtures.Pages(JsonNode.Parse($$"""{"files": [{"filename": "{{Fixtures.Evm}}/A.cs"}]}""")!));
            api.GetPagesAsync("repos/o/r/commits/c6", Arg.Any<CancellationToken>()).Returns(_ => Fixtures.Pages(JsonNode.Parse("""{"files": [{"filename": "README.md"}]}""")!));
            api.GetPagesAsync(Arg.Is<string>(path => path.Contains("/jobs")), Arg.Any<CancellationToken>()).Returns(_ => Fixtures.Pages(new JsonObject
            {
                ["jobs"] = new JsonArray(new JsonObject { ["id"] = 1L, ["name"] = "Run Nethermind.Evm.Test (ubuntu-latest)", ["conclusion"] = "failure" }),
            }));
            api.GetTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(string.Join('\n', failures.Select(static failure => $"2026-10-01T00:00:00Z   failed {failure[(failure.IndexOf('|') + 1)..]}")));

            Issues.OpenIssueForAsync("Nethermind tests", Arg.Any<CancellationToken>()).Returns(openIssue);
            _reporter = new MasterFailureReporter(api, new RunFailures(new ActionsApi(api)), Issues, Fixtures.Projects, new SelectorLinks());
        }

        public IIssueTracker Issues { get; } = Substitute.For<IIssueTracker>();

        public Task<string> ReportAsync() => _reporter.ReportAsync(9, [], CancellationToken.None);
    }
}

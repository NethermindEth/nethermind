// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using Nethermind.TestSelection.Collection;
using Nethermind.TestSelection.Costs;
using Nethermind.TestSelection.GitHub;
using Nethermind.TestSelection.Journal;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TestSelection.Test;

[TestFixture]
public class CollectionTests
{
    [Test]
    public async Task Windows_OfAnEmptyJournal_BackfillOneStep()
    {
        List<(DateTimeOffset Since, DateTimeOffset Until)> windows = await WindowsAsync([]);
        Assert.That(windows, Is.EqualTo(new[] { (Fixtures.Now - RunCollector.BackfillStep, Fixtures.Now) }), "a new journal grows one step per call");
    }

    [Test]
    public async Task Windows_OfAShortJournal_OverlapTheNewestAndExtendTheOldest()
    {
        JournalEntry oldest = Fixtures.Entry(1, -10, JournalEntry.Master, [], @event: JournalEntry.Push);
        JournalEntry newest = Fixtures.Entry(2, -0.1, JournalEntry.Master, [], @event: JournalEntry.Push);

        List<(DateTimeOffset Since, DateTimeOffset Until)> windows = await WindowsAsync([oldest, newest]);

        Assert.That(windows[0], Is.EqualTo((newest.At - RunCollector.Overlap, Fixtures.Now)), "the overlap catches runs that ended late");
        Assert.That(windows[1], Is.EqualTo((oldest.At - RunCollector.BackfillStep, oldest.At)), "the journal grows back towards the retention");
    }

    [TestCase("success", "push", true, TestName = "GreenPush")]
    [TestCase("failure", "pull_request", true, TestName = "RedPullRequest")]
    [TestCase("cancelled", "push", false, TestName = "Cancelled")]
    [TestCase("success", "schedule", false, TestName = "Schedule")]
    public void IsCollectable_OnlyFinishedPullRequestsAndPushes(string conclusion, string @event, bool collectable) =>
        Assert.That(RunCollector.IsCollectable(new JsonObject { ["conclusion"] = conclusion, ["event"] = @event }), Is.EqualTo(collectable),
            "a cancelled run shows nothing about the change");

    [Test]
    public void PathsOf_ListsBothPathsOfARenamedFile()
    {
        JsonArray files = [new JsonObject { ["filename"] = "b/x.cs", ["previous_filename"] = "a/x.cs" }, new JsonObject { ["filename"] = "c/y.cs" }];
        Assert.That(ChangedFiles.PathsOf(files), Is.EqualTo(new[] { "b/x.cs", "a/x.cs", "c/y.cs" }), "the project a file left changed too");
    }

    [Test]
    public void NethtestCases_CountCasesPerLaneTestAndFork()
    {
        using TempDirectory directory = new();
        string lane = Path.Combine(directory.Path, "nethtest-results-blockTest-sequential-1of2");
        Directory.CreateDirectory(lane);
        File.WriteAllText(Path.Combine(lane, "nethtest-results.json"), """
            [{"name": "test_add[fork_Cancun-blockchain_test-a]"}, {"name": "test_add[fork_Cancun-blockchain_test-b]"},
             {"name": "test_add[fork_Prague]"}, {"name": "legacy_case", "fork": "Berlin"}]
            """);

        Dictionary<string, Dictionary<string, Dictionary<string, int>>> cases = InventoryBuilder.NethtestCases(directory.Path);

        Assert.That(cases["blockTest|sequential"]["test_add"], Is.EquivalentTo(new Dictionary<string, int> { ["Cancun"] = 2, ["Prague"] = 1 }), "cases per fork");
        Assert.That(cases["blockTest|sequential"]["legacy_case"], Is.EquivalentTo(new Dictionary<string, int> { ["Berlin"] = 1 }), "a case without a fork in its name");
    }

    [Test]
    public void NethtestSeconds_SumTheRunStepPerLane_AndCostsSpreadThemOverCases()
    {
        static JsonObject Job(string chunk, string step) => new()
        {
            ["name"] = $"EEST nethtest / nethtest blockTest sequential {chunk}",
            ["steps"] = new JsonArray(new JsonObject { ["name"] = step, ["started_at"] = "2026-10-01T00:00:00Z", ["completed_at"] = "2026-10-01T00:01:30Z" }),
        };
        Dictionary<string, double> seconds = InventoryBuilder.NethtestSeconds([Job("1of3", "Run nethtest and parse results"), Job("2of3", "Run nethtest and parse results"), Job("3of3", "Other")]);
        Dictionary<string, Dictionary<string, Dictionary<string, int>>> cases = new() { ["blockTest|sequential"] = new() { ["test_add"] = new() { ["Cancun"] = 3 }, ["test_mul"] = new() { ["Cancun"] = 1 } } };

        Assert.That(seconds, Is.EquivalentTo(new Dictionary<string, double> { ["blockTest|sequential"] = 180 }), "only the run step counts");
        Assert.That(InventoryBuilder.EestCosts(cases, seconds)["blockTest|sequential"]["test_add"]["Cancun"], Is.EqualTo(135).Within(1e-9), "3 of 4 cases");
    }

    [Test]
    public async Task ModelArtifact_DownloadsTheNewestTrustedArtifact_ByTimeNotId()
    {
        IGitHubApi api = Substitute.For<IGitHubApi>();
        api.Repository.Returns("o/r");
        static JsonObject Artifact(long id, string at, long head) => new()
        {
            ["id"] = id,
            ["created_at"] = at,
            ["expired"] = false,
            ["workflow_run"] = new JsonObject { ["id"] = id * 10, ["head_branch"] = "master", ["head_repository_id"] = head, ["repository_id"] = 1L },
        };
        api.GetPagesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Fixtures.Pages(new JsonObject
        {
            ["artifacts"] = new JsonArray(Artifact(9, "2026-10-01T00:00:00Z", 1), Artifact(5, "2026-10-02T00:00:00Z", 1), Artifact(7, "2026-10-03T00:00:00Z", 2)),
        }));
        api.DownloadAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(call => WriteEmptyZip(call.Arg<Stream>()));
        using TempDirectory directory = new();

        long? run = await new ModelArtifact(new ActionsApi(api)).DownloadAsync(directory.Path, CancellationToken.None);

        Assert.That(run, Is.EqualTo(50), "the newest trusted artifact; the fork's artifact 7 never counts");
    }

    private static async Task<List<(DateTimeOffset Since, DateTimeOffset Until)>> WindowsAsync(IReadOnlyList<JournalEntry> journal)
    {
        IGitHubApi api = Substitute.For<IGitHubApi>();
        api.Repository.Returns("o/r");
        List<(DateTimeOffset Since, DateTimeOffset Until)> windows = [];
        api.GetPagesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            string[] bounds = call.Arg<string>().Split("created=")[1].Split('&')[0].Split("..");
            windows.Add((DateTimeOffset.Parse(bounds[0]), DateTimeOffset.Parse(bounds[1])));
            return Fixtures.Pages(new JsonObject { ["workflow_runs"] = new JsonArray() });
        });

        await new RunCollector(new ActionsApi(api), null!, null!, ["a.yml"]).WindowsAsync(journal, Fixtures.Now, CancellationToken.None);
        return Merge(windows);
    }

    // RunsAsync queries day by day; consecutive day windows are one collection window.
    private static List<(DateTimeOffset Since, DateTimeOffset Until)> Merge(List<(DateTimeOffset Since, DateTimeOffset Until)> days)
    {
        List<(DateTimeOffset Since, DateTimeOffset Until)> merged = [];
        foreach ((DateTimeOffset since, DateTimeOffset until) in days)
        {
            if (merged.Count > 0 && merged[^1].Until.AddSeconds(1) == since)
            {
                merged[^1] = (merged[^1].Since, until);
            }
            else
            {
                merged.Add((since, until));
            }
        }

        return merged;
    }

    private static Task WriteEmptyZip(Stream stream)
    {
        using (System.IO.Compression.ZipArchive _ = new(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
        }

        return Task.CompletedTask;
    }
}

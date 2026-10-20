// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Text.Json.Nodes;
using Nethermind.TestSelection.GitHub;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TestSelection.Test;

[TestFixture]
public class GitHubTests
{
    [Test]
    public async Task Client_FollowsTheLinkHeaderThroughEveryPage()
    {
        using GitHubClient client = Client(
            Respond(HttpStatusCode.OK, """{"n": 1}""", link: "<https://api.github.com/page2>; rel=\"next\""),
            Respond(HttpStatusCode.OK, """{"n": 2}"""));
        List<int> pages = [];
        await foreach (JsonNode page in client.GetPagesAsync("page1", CancellationToken.None))
        {
            pages.Add(page["n"]!.GetValue<int>());
        }

        Assert.That(pages, Is.EqualTo(new[] { 1, 2 }), "every page is read");
    }

    [Test]
    public async Task Client_RetriesServerErrors_AndWaitsOutTheRateLimit()
    {
        List<TimeSpan> waits = [];
        using GitHubClient client = Client(waits,
            Respond(HttpStatusCode.BadGateway, "{}"),
            Respond(HttpStatusCode.Forbidden, "{}", remaining: 0),
            Respond(HttpStatusCode.OK, """{"ok": true}"""));

        JsonNode result = await client.GetAsync("x", CancellationToken.None);

        Assert.That(result["ok"]!.GetValue<bool>(), Is.True, "a rate limit is waited out, never an empty result");
        Assert.That(waits.Count, Is.EqualTo(2), "one wait per failed attempt");
    }

    [Test]
    public async Task Client_DoesNotRetryANotFound()
    {
        using GitHubClient client = Client(Respond(HttpStatusCode.NotFound, "{}"), Respond(HttpStatusCode.OK, "{}"));
        GitHubException error = (await Assert.ThrowsAsync<GitHubException>(() => client.GetAsync("x", CancellationToken.None)))!;
        Assert.That(error.Status, Is.EqualTo(HttpStatusCode.NotFound), "the caller decides what a missing resource means");
    }

    [TestCase(GitHubClient.BulkReserveShare, 1, TestName = "BulkCommand")]
    [TestCase(0.0, 0, TestName = "PullRequestCommand")]
    public async Task Client_BelowTheReserve_WaitsForTheResetOnlyInBulkCommands(double reserve, int waitCount)
    {
        List<TimeSpan> waits = [];
        long reset = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        using GitHubClient client = Client(waits, reserve,
            Respond(HttpStatusCode.OK, "{}", remaining: 100, limit: 1000, reset: reset),
            Respond(HttpStatusCode.OK, "{}"));

        await client.GetAsync("a", CancellationToken.None);
        await client.GetAsync("b", CancellationToken.None);

        Assert.That(waits.Count(static wait => wait > TimeSpan.FromMinutes(9)), Is.EqualTo(waitCount),
            "collect keeps budget for other jobs; a pull request's selection never waits for it");
    }

    [Test]
    public async Task Client_WhenServerErrorsPersist_FailsAfterTheLastAttemptWithoutWaiting()
    {
        List<TimeSpan> waits = [];
        using GitHubClient client = Client(waits, 0, [.. Enumerable.Range(0, 4).Select(static _ => (object)Respond(HttpStatusCode.BadGateway, "bad gateway"))]);

        GitHubException error = (await Assert.ThrowsAsync<GitHubException>(() => client.GetTextAsync("logs", CancellationToken.None)))!;

        Assert.That(error.Status, Is.EqualTo(HttpStatusCode.BadGateway), "the error body is never returned as a log");
        Assert.That(waits, Is.EqualTo(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15) }), "no wait after the last attempt");
    }

    [Test]
    public async Task Client_RetriesATransportError_AndFailsAsAGitHubError()
    {
        using GitHubClient retried = Client([], 0, new HttpRequestException("connection reset"), Respond(HttpStatusCode.OK, """{"ok": true}"""));
        Assert.That((await retried.GetAsync("x", CancellationToken.None))["ok"]!.GetValue<bool>(), Is.True, "a reset connection is retried");

        using GitHubClient broken = Client([], 0, [.. Enumerable.Range(0, 4).Select(static _ => (object)new IOException("broken pipe"))]);
        await Assert.ThrowsAsync<GitHubException>(() => broken.GetAsync("x", CancellationToken.None), "the collector skips only this run");
    }

    [Test]
    public async Task Client_DoesNotRetryAWriteThatMayHaveHappened()
    {
        QueuedHandler handler = new([Respond(HttpStatusCode.BadGateway, "{}"), Respond(HttpStatusCode.OK, "{}")]);
        using GitHubClient client = new(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") }, "o/r", 0, static (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<GitHubException>(() => client.SendAsync(HttpMethod.Post, "issues", new JsonObject(), CancellationToken.None), "a 502 may follow a created issue");
        Assert.That(handler.Sent, Is.EqualTo(1), "the issue is never opened twice");
    }

    [Test]
    public async Task Client_DoesNotRetryAPlainForbidden()
    {
        using GitHubClient client = Client(Respond(HttpStatusCode.Forbidden, """{"message": "Resource not accessible by integration"}"""), Respond(HttpStatusCode.OK, "{}"));
        await Assert.ThrowsAsync<GitHubException>(() => client.GetAsync("x", CancellationToken.None), "missing permission does not heal");
    }

    [Test]
    public async Task Client_WaitsWhatRetryAfterAsks()
    {
        List<TimeSpan> waits = [];
        HttpResponseMessage limited = Respond(HttpStatusCode.TooManyRequests, "{}");
        limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
        using GitHubClient client = Client(waits, 0, limited, Respond(HttpStatusCode.OK, "{}"));

        await client.GetAsync("x", CancellationToken.None);

        Assert.That(waits, Is.EqualTo(new[] { TimeSpan.FromSeconds(7) }), "GitHub says when to come back");
    }

    [Test]
    public async Task Runs_WhenAWindowHitsTheCap_SplitIt()
    {
        IGitHubApi api = Substitute.For<IGitHubApi>();
        api.Repository.Returns("o/r");
        List<string> windows = [];
        api.GetPagesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            string window = call.Arg<string>().Split("created=")[1].Split('&')[0];
            windows.Add(window);
            string[] bounds = window.Split("..");
            int count = DateTimeOffset.Parse(bounds[1]) - DateTimeOffset.Parse(bounds[0]) > TimeSpan.FromHours(6) ? ActionsApi.ListRunsCap : 10;
            return Fixtures.Pages(new JsonObject { ["workflow_runs"] = new JsonArray([.. Enumerable.Range(0, count).Select(static i => (JsonNode)new JsonObject { ["id"] = i })]) });
        });

        List<JsonNode> runs = await new ActionsApi(api).RunsAsync(["a.yml"], Fixtures.Now, Fixtures.Now.AddHours(12).AddSeconds(-1), CancellationToken.None);

        Assert.That(windows.Count, Is.EqualTo(3), "the capped 12 h window is split into two 6 h windows");
        Assert.That(runs.Count, Is.EqualTo(20), "the split windows return every run");
    }

    [Test]
    public async Task Jobs_WhenALargePageFails_ReadSmallPages()
    {
        IGitHubApi api = Substitute.For<IGitHubApi>();
        api.Repository.Returns("o/r");
        api.GetPagesAsync(Arg.Is<string>(path => path.Contains("per_page=100")), Arg.Any<CancellationToken>()).Returns(_ => Failing());
        api.GetPagesAsync(Arg.Is<string>(path => path.Contains("per_page=30")), Arg.Any<CancellationToken>())
            .Returns(_ => Fixtures.Pages(new JsonObject { ["jobs"] = new JsonArray(new JsonObject { ["id"] = 1 }) }));

        Assert.That((await new ActionsApi(api).JobsAsync(7, CancellationToken.None)).Count, Is.EqualTo(1), "runs with hundreds of jobs break large pages");
    }

    [TestCase(false, "master", 1L, 1L, true, TestName = "MasterRunOfThisRepository")]
    [TestCase(true, "master", 1L, 1L, false, TestName = "Expired")]
    [TestCase(false, "feature", 1L, 1L, false, TestName = "Branch")]
    [TestCase(false, "master", 2L, 1L, false, TestName = "ForkNamedMaster")]
    [TestCase(false, "master", null, null, false, TestName = "UnknownRepository")]
    public void ModelArtifact_TrustsOnlyMasterRunsOfThisRepository(bool expired, string branch, long? head, long? repository, bool trusted)
    {
        JsonObject artifact = new()
        {
            ["expired"] = expired,
            ["workflow_run"] = new JsonObject { ["head_branch"] = branch, ["head_repository_id"] = head, ["repository_id"] = repository },
        };
        Assert.That(ModelArtifact.IsTrusted(artifact), Is.EqualTo(trusted), "a fork can name its branch master");
    }

    [Test]
    public async Task Prune_DeletesAllButTheNewestTrustedArtifacts_AndLeavesForksAlone()
    {
        IGitHubApi api = Substitute.For<IGitHubApi>();
        api.Repository.Returns("o/r");
        api.GetPagesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Fixtures.Pages(new JsonObject
        {
            ["artifacts"] = new JsonArray(Fixtures.Artifact(1, "2026-10-01T00:00:00Z", 1), Fixtures.Artifact(2, "2026-10-02T00:00:00Z", 1),
                Fixtures.Artifact(3, "2026-10-03T00:00:00Z", 1), Fixtures.Artifact(4, "2026-10-04T00:00:00Z", 2)),
        }));

        int deleted = await new ModelArtifact(new ActionsApi(api)).PruneAsync(2, CancellationToken.None);

        Assert.That(deleted, Is.EqualTo(1), "the two newest trusted artifacts stay");
        await api.Received(1).SendAsync(HttpMethod.Delete, "repos/o/r/actions/artifacts/1", null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Download_WithoutATrustedArtifact_ExtractsNothing()
    {
        IGitHubApi api = Substitute.For<IGitHubApi>();
        api.Repository.Returns("o/r");
        api.GetPagesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Fixtures.Pages(new JsonObject
        {
            ["artifacts"] = new JsonArray(Fixtures.Artifact(4, "2026-10-04T00:00:00Z", 2)),
        }));
        using TempDirectory directory = new();

        Assert.That(await new ModelArtifact(new ActionsApi(api)).DownloadAsync(directory.Path, CancellationToken.None), Is.Null, "the selection runs everything");
        await api.DidNotReceive().DownloadAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    private static GitHubClient Client(params HttpResponseMessage[] responses) => Client([], 0, [.. responses]);

    private static GitHubClient Client(List<TimeSpan> waits, params HttpResponseMessage[] responses) => Client(waits, 0, [.. responses]);

    /// <param name="outcomes">Responses, or exceptions the transport throws, in the order the client meets them.</param>
    private static GitHubClient Client(List<TimeSpan> waits, double reserve, params object[] outcomes) =>
        new(new HttpClient(new QueuedHandler(outcomes)) { BaseAddress = new Uri("https://api.github.com/") }, "o/r", reserve,
            (wait, _) => { waits.Add(wait); return Task.CompletedTask; });

    private static HttpResponseMessage Respond(HttpStatusCode status, string body, string? link = null, long? remaining = null, long limit = 5000, long? reset = null)
    {
        HttpResponseMessage response = new(status) { Content = new StringContent(body) };
        if (link is not null)
        {
            response.Headers.Add("Link", link);
        }

        if (remaining is not null)
        {
            response.Headers.Add("x-ratelimit-remaining", remaining.Value.ToString());
            response.Headers.Add("x-ratelimit-limit", limit.ToString());
            response.Headers.Add("x-ratelimit-reset", (reset ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString());
        }

        return response;
    }

    private static async IAsyncEnumerable<JsonNode> Failing(bool fail = true)
    {
        await Task.CompletedTask;
        if (fail)
        {
            throw new GitHubException("502 Bad Gateway", HttpStatusCode.BadGateway);
        }

        yield break;
    }

    private sealed class QueuedHandler(IEnumerable<object> outcomes) : HttpMessageHandler
    {
        private readonly Queue<object> _outcomes = new(outcomes);

        public int Sent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent++;
            return _outcomes.Dequeue() switch
            {
                HttpResponseMessage response => Task.FromResult(response),
                Exception error => Task.FromException<HttpResponseMessage>(error),
                _ => throw new InvalidOperationException("a queued outcome is a response or an exception"),
            };
        }
    }
}

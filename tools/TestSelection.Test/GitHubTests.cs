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

    [Test]
    public async Task Client_BelowTheReserve_WaitsForTheReset()
    {
        List<TimeSpan> waits = [];
        long reset = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        using GitHubClient client = Client(waits,
            Respond(HttpStatusCode.OK, "{}", remaining: 100, limit: 1000, reset: reset),
            Respond(HttpStatusCode.OK, "{}"));

        await client.GetAsync("a", CancellationToken.None);
        await client.GetAsync("b", CancellationToken.None);

        Assert.That(waits.Single(), Is.GreaterThan(TimeSpan.FromMinutes(9)), "the budget left to other jobs is kept");
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

    private static GitHubClient Client(params HttpResponseMessage[] responses) => Client([], responses);

    private static GitHubClient Client(List<TimeSpan> waits, params HttpResponseMessage[] responses) =>
        new(new HttpClient(new QueuedHandler(responses)) { BaseAddress = new Uri("https://api.github.com/") }, "o/r",
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

    private sealed class QueuedHandler(IEnumerable<HttpResponseMessage> responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_responses.Dequeue());
    }
}

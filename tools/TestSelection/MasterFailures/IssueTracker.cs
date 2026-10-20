// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Text.Json.Nodes;
using Nethermind.TestSelection.GitHub;

namespace Nethermind.TestSelection.MasterFailures;

public interface IIssueTracker
{
    Task<int?> OpenIssueForAsync(string workflow, CancellationToken cancellation);
    Task CreateAsync(string title, string body, IReadOnlyCollection<string> assignees, CancellationToken cancellation);
    Task CommentAsync(int issue, string body, CancellationToken cancellation);
    Task CloseAsync(int issue, string comment, CancellationToken cancellation);
}

/// <summary>The repository's issues with the <see cref="IssueText.Label"/> label.</summary>
public sealed class IssueTracker(IGitHubApi api) : IIssueTracker
{
    public async Task<int?> OpenIssueForAsync(string workflow, CancellationToken cancellation)
    {
        await foreach (JsonNode page in api.GetPagesAsync($"repos/{api.Repository}/issues?labels={IssueText.Label}&state=open&per_page=100", cancellation))
        {
            foreach (JsonNode? issue in page.AsArray())
            {
                if (issue!["body"]?.GetValue<string>()?.Contains(IssueText.Marker(workflow), StringComparison.Ordinal) == true)
                {
                    return issue["number"]!.GetValue<int>();
                }
            }
        }

        return null;
    }

    public async Task CreateAsync(string title, string body, IReadOnlyCollection<string> assignees, CancellationToken cancellation)
    {
        await EnsureLabelAsync(cancellation);
        JsonObject issue = new() { ["title"] = title, ["body"] = body, ["labels"] = new JsonArray(IssueText.Label) };
        try
        {
            await api.SendAsync(HttpMethod.Post, $"repos/{api.Repository}/issues", With(issue, assignees), cancellation);
        }
        catch (GitHubException error) when (error.Status == HttpStatusCode.UnprocessableEntity && assignees.Count > 0)
        {
            // An author that cannot be assigned, such as a bot, must not lose the issue.
            await api.SendAsync(HttpMethod.Post, $"repos/{api.Repository}/issues", issue, cancellation);
        }
    }

    public Task CommentAsync(int issue, string body, CancellationToken cancellation) =>
        api.SendAsync(HttpMethod.Post, $"repos/{api.Repository}/issues/{issue}/comments", new JsonObject { ["body"] = body }, cancellation);

    public async Task CloseAsync(int issue, string comment, CancellationToken cancellation)
    {
        await CommentAsync(issue, comment, cancellation);
        await api.SendAsync(HttpMethod.Patch, $"repos/{api.Repository}/issues/{issue}", new JsonObject { ["state"] = "closed" }, cancellation);
    }

    private async Task EnsureLabelAsync(CancellationToken cancellation)
    {
        try
        {
            await api.SendAsync(HttpMethod.Post, $"repos/{api.Repository}/labels",
                new JsonObject { ["name"] = IssueText.Label, ["color"] = "B60205", ["description"] = "A test fails on master" }, cancellation);
        }
        catch (GitHubException error) when (error.Status == HttpStatusCode.UnprocessableEntity)
        {
            // The label exists already.
        }
    }

    private static JsonObject With(JsonObject issue, IReadOnlyCollection<string> assignees)
    {
        JsonObject copy = issue.DeepClone().AsObject();
        copy["assignees"] = new JsonArray([.. assignees.Select(static author => (JsonNode)author)]);
        return copy;
    }
}

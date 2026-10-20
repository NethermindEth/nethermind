// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using Nethermind.TestSelection.Collection;
using Nethermind.TestSelection.GitHub;
using Nethermind.TestSelection.Journal;
using Nethermind.TestSelection.Selection;

namespace Nethermind.TestSelection.MasterFailures;

/// <summary>
/// Turns a red master run of a test workflow into one issue assigned to the author of the pull request that most
/// likely caused it; later failures comment on the open issue, and the next green run closes it.
/// </summary>
public sealed class MasterFailureReporter(IGitHubApi api, RunFailures runFailures, IIssueTracker issues, Projects projects, SelectorLinks links)
{
    public async Task<string> ReportAsync(long runId, IReadOnlyList<JournalEntry> journal, CancellationToken cancellation)
    {
        JsonNode run = await api.GetAsync($"repos/{api.Repository}/actions/runs/{runId}", cancellation);
        string workflow = run["name"]!.GetValue<string>();
        int? open = await issues.OpenIssueForAsync(workflow, cancellation);
        if (run["conclusion"]!.GetValue<string>() == "success")
        {
            return await CloseAsync(open, workflow, run, cancellation);
        }

        if (await LastGreenAsync(run, cancellation) is not { } green)
        {
            return "no green master run to compare with";
        }

        IReadOnlyList<string> tests = Attribution.Regressions(
            await runFailures.ReadAsync(run, cancellation), journal, DateTimeOffset.Parse(green["created_at"]!.GetValue<string>()));
        if (tests.Count == 0)
        {
            return "no failure that a change caused: infra, Hive or tests that were already flaky";
        }

        if (open is { } issue)
        {
            await issues.CommentAsync(issue, $"Still failing in [{runId}]({Url(run)}) at {Sha(run)[..10]}: {tests.Count} tests.", cancellation);
            return $"commented on #{issue}";
        }

        IReadOnlyList<Suspect> suspects = Attribution.Suspects(await MergedBetweenAsync(Sha(green), Sha(run), cancellation), tests, projects, links);
        string[] authors = [.. suspects.Select(static suspect => suspect.Merge.Author).OfType<string>().Distinct().Order(StringComparer.Ordinal)];
        await issues.CreateAsync(IssueText.Title(workflow, tests), IssueText.Body(workflow, Url(run), Sha(run), Sha(green), tests, suspects), authors, cancellation);
        return $"opened an issue for {tests.Count} tests, assigned to {(authors.Length > 0 ? string.Join(", ", authors) : "nobody")}";
    }

    /// <summary>The newest successful master push of the same workflow created before the run.</summary>
    public async Task<JsonNode?> LastGreenAsync(JsonNode run, CancellationToken cancellation)
    {
        DateTimeOffset failedAt = DateTimeOffset.Parse(run["created_at"]!.GetValue<string>());
        JsonNode green = await api.GetAsync(
            $"repos/{api.Repository}/actions/workflows/{run["workflow_id"]}/runs?branch=master&event=push&status=success&per_page=20", cancellation);
        return green["workflow_runs"]!.AsArray()
            .Select(static candidate => candidate!)
            .FirstOrDefault(candidate => DateTimeOffset.Parse(candidate["created_at"]!.GetValue<string>()) < failedAt);
    }

    private async Task<string> CloseAsync(int? open, string workflow, JsonNode run, CancellationToken cancellation)
    {
        if (open is not { } issue)
        {
            return "green, no open issue";
        }

        await issues.CloseAsync(issue, $"[{workflow}]({Url(run)}) is green on master at {Sha(run)[..10]}.", cancellation);
        return $"closed #{issue}";
    }

    private async Task<List<Merge>> MergedBetweenAsync(string from, string to, CancellationToken cancellation)
    {
        JsonNode compare = await api.GetAsync($"repos/{api.Repository}/compare/{from}...{to}", cancellation);
        List<Merge> merges = [];
        foreach (JsonNode? commit in compare["commits"]!.AsArray())
        {
            string sha = commit!["sha"]!.GetValue<string>();
            JsonNode? pull = (await api.GetAsync($"repos/{api.Repository}/commits/{sha}/pulls", cancellation)).AsArray().FirstOrDefault();
            List<string> files = [];
            await foreach (JsonNode page in api.GetPagesAsync($"repos/{api.Repository}/commits/{sha}", cancellation))
            {
                files.AddRange(ChangedFiles.PathsOf(page["files"]!.AsArray()));
            }

            merges.Add(new Merge(sha, pull?["number"]!.GetValue<int>(), pull?["user"]!["login"]!.GetValue<string>(), files));
        }

        return merges;
    }

    private static string Url(JsonNode run) => run["html_url"]!.GetValue<string>();

    private static string Sha(JsonNode run) => run["head_sha"]!.GetValue<string>();
}

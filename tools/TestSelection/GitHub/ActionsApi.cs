// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Nethermind.TestSelection.GitHub;

/// <summary>The GitHub Actions resources this tool reads: workflow runs, their jobs, logs and artifacts.</summary>
public sealed class ActionsApi(IGitHubApi api)
{
    // The list-runs API returns at most this many runs per filtered query, even with pagination.
    public const int ListRunsCap = 1000;
    private static readonly TimeSpan SmallestWindow = TimeSpan.FromMinutes(30);

    public string Repository => api.Repository;

    /// <summary>Completed runs of the workflows created in [since, until], one query per day, split further at the cap.</summary>
    public async Task<List<JsonNode>> RunsAsync(IEnumerable<string> workflows, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellation)
    {
        List<JsonNode> runs = [];
        foreach (string workflow in workflows)
        {
            for (DateTimeOffset start = since; start < until;)
            {
                DateTimeOffset end = Min(start.AddDays(1), until);
                runs.AddRange(await RunsInAsync(workflow, start, end, cancellation));
                start = end.AddSeconds(1);
            }
        }

        return runs;
    }

    public async Task<List<JsonNode>> JobsAsync(long run, CancellationToken cancellation)
    {
        try
        {
            return await ListAsync($"repos/{Repository}/actions/runs/{run}/jobs?filter=latest&per_page=100", "jobs", cancellation);
        }
        catch (GitHubException)
        {
            // Large pages sometimes fail with HTTP 502 on runs with hundreds of jobs; small pages then succeed.
            return await ListAsync($"repos/{Repository}/actions/runs/{run}/jobs?filter=latest&per_page=30", "jobs", cancellation);
        }
    }

    public Task<string> JobLogAsync(long job, CancellationToken cancellation) =>
        api.GetTextAsync($"repos/{Repository}/actions/jobs/{job}/logs", cancellation);

    public async Task<IReadOnlySet<string>> ArtifactNamesAsync(long run, CancellationToken cancellation) =>
        (await RunArtifactsAsync(run, cancellation))
        .Select(static artifact => artifact["name"]!.GetValue<string>())
        .ToHashSet();

    public Task<List<JsonNode>> ArtifactsNamedAsync(string name, CancellationToken cancellation) =>
        ListAsync($"repos/{Repository}/actions/artifacts?name={Uri.EscapeDataString(name)}&per_page=100", "artifacts", cancellation);

    public Task DeleteArtifactAsync(long artifact, CancellationToken cancellation) =>
        api.SendAsync(HttpMethod.Delete, $"repos/{Repository}/actions/artifacts/{artifact}", null, cancellation);

    public Task<List<JsonNode>> RunArtifactsAsync(long run, CancellationToken cancellation) =>
        ListAsync($"repos/{Repository}/actions/runs/{run}/artifacts?per_page=100", "artifacts", cancellation);

    public async Task ExtractArtifactAsync(long artifact, string directory, CancellationToken cancellation)
    {
        using MemoryStream zip = new();
        await api.DownloadAsync($"repos/{Repository}/actions/artifacts/{artifact}/zip", zip, cancellation);
        zip.Position = 0;
        using ZipArchive archive = new(zip, ZipArchiveMode.Read);
        Directory.CreateDirectory(directory);
        archive.ExtractToDirectory(directory, overwriteFiles: true);
    }

    public async Task<List<JsonNode>> ListAsync(string path, string property, CancellationToken cancellation)
    {
        List<JsonNode> items = [];
        await foreach (JsonNode page in api.GetPagesAsync(path, cancellation))
        {
            items.AddRange(page[property]!.AsArray().Select(static item => item!));
        }

        return items;
    }

    private async Task<List<JsonNode>> RunsInAsync(string workflow, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellation)
    {
        string window = $"{since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}..{until.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";
        List<JsonNode> runs = await ListAsync(
            $"repos/{Repository}/actions/workflows/{workflow}/runs?status=completed&created={window}&per_page=100", "workflow_runs", cancellation);
        if (runs.Count < ListRunsCap || until - since <= SmallestWindow)
        {
            return runs;
        }

        DateTimeOffset middle = since + (until - since) / 2;
        return [.. await RunsInAsync(workflow, since, middle, cancellation), .. await RunsInAsync(workflow, middle.AddSeconds(1), until, cancellation)];
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;
}

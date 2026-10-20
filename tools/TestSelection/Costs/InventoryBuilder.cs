// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nethermind.TestSelection.Collection;
using Nethermind.TestSelection.GitHub;
using Nethermind.TestSelection.Journal;

namespace Nethermind.TestSelection.Costs;

/// <summary>
/// Builds the cost inventory from one full run of the main test workflow: EEST costs from its nethtest result
/// artifacts and step times, NUnit costs from job times, or from the log groups of a job that runs a pack.
/// </summary>
public sealed partial class InventoryBuilder(ActionsApi actions)
{
    private const string ResultsPrefix = "nethtest-results-";
    private const string NethtestStep = "Run nethtest and parse results";

    public async Task<CostInventory> BuildAsync(long run, string workDirectory, CancellationToken cancellation)
    {
        List<JsonNode> jobs = await actions.JobsAsync(run, cancellation);
        foreach (JsonNode artifact in await actions.ListAsync($"repos/{actions.Repository}/actions/runs/{run}/artifacts?per_page=100", "artifacts", cancellation))
        {
            string name = artifact["name"]!.GetValue<string>();
            if (name.StartsWith(ResultsPrefix, StringComparison.Ordinal))
            {
                await actions.ExtractArtifactAsync(artifact["id"]!.GetValue<long>(), Path.Combine(workDirectory, name), cancellation);
            }
        }

        return new CostInventory(
            EestCosts(NethtestCases(workDirectory), NethtestSeconds(jobs)),
            await ProjectSecondsAsync(jobs, cancellation));
    }

    /// <summary>Cases per lane, test function and fork, from the nethtest result files under the directory.</summary>
    public static Dictionary<string, Dictionary<string, Dictionary<string, int>>> NethtestCases(string directory)
    {
        Dictionary<string, Dictionary<string, Dictionary<string, int>>> cases = [];
        foreach (string results in Directory.EnumerateFiles(directory, "nethtest-results.json", SearchOption.AllDirectories))
        {
            // Artifact names are nethtest-results-<type>-<setup>-<chunk>.
            string[] parts = Path.GetFileName(Path.GetDirectoryName(results)!)[ResultsPrefix.Length..].Split('-');
            string lane = $"{parts[0]}|{parts[1]}";
            foreach (JsonNode? result in JsonFiles.ReadNode(results).AsArray())
            {
                (string name, string fork) = CaseKey(result!);
                Dictionary<string, int> forks = GetOrAdd(GetOrAdd(cases, lane), name);
                forks[fork] = forks.GetValueOrDefault(fork) + 1;
            }
        }

        return cases;
    }

    public static (string Name, string Fork) CaseKey(JsonNode result)
    {
        string name = result["name"]!.GetValue<string>();
        Match match = NethtestCase().Match(name);
        return match.Success
            ? (match.Groups[1].Value, match.Groups[2].Value)
            : (name.Split('[')[0], result["fork"]?.GetValue<string>() ?? string.Empty);
    }

    /// <summary>Seconds of the nethtest step per lane.</summary>
    public static Dictionary<string, double> NethtestSeconds(IEnumerable<JsonNode> jobs)
    {
        Dictionary<string, double> seconds = [];
        foreach (JsonNode job in jobs)
        {
            string? lane = JobLogs.NethtestLane(job["name"]!.GetValue<string>());
            JsonNode? step = job["steps"]?.AsArray().FirstOrDefault(static s => s!["name"]!.GetValue<string>() == NethtestStep);
            if (lane is not null && step?["completed_at"] is not null)
            {
                seconds[lane] = seconds.GetValueOrDefault(lane) + Duration(step);
            }
        }

        return seconds;
    }

    /// <summary>Spreads each lane's measured step time evenly over its cases.</summary>
    public static Dictionary<string, Dictionary<string, Dictionary<string, double>>> EestCosts(
        Dictionary<string, Dictionary<string, Dictionary<string, int>>> cases, Dictionary<string, double> seconds)
    {
        Dictionary<string, Dictionary<string, Dictionary<string, double>>> costs = [];
        foreach ((string lane, Dictionary<string, Dictionary<string, int>> tests) in cases)
        {
            double perCase = seconds.GetValueOrDefault(lane) / tests.Values.Sum(static forks => forks.Values.Sum());
            costs[lane] = tests.ToDictionary(
                static pair => pair.Key,
                pair => pair.Value.ToDictionary(static fork => fork.Key, fork => fork.Value * perCase));
        }

        return costs;
    }

    private async Task<Dictionary<string, double>> ProjectSecondsAsync(IEnumerable<JsonNode> jobs, CancellationToken cancellation)
    {
        Dictionary<string, double> seconds = [];
        foreach (JsonNode job in jobs)
        {
            string? unit = JobLogs.UnitOf(job["name"]!.GetValue<string>());
            if (unit is null or JobLogs.Eest or JobLogs.Hive || job["completed_at"] is null)
            {
                continue;
            }

            IReadOnlyDictionary<string, double> projects = JobLogs.IsProject(unit)
                ? new Dictionary<string, double> { [unit] = Duration(job) }
                : JobLogs.GroupSeconds(await actions.JobLogAsync(job["id"]!.GetValue<long>(), cancellation));
            foreach ((string project, double projectSeconds) in projects)
            {
                seconds[project] = seconds.GetValueOrDefault(project) + projectSeconds;
            }
        }

        return seconds;
    }

    private static double Duration(JsonNode span) =>
        (DateTimeOffset.Parse(span["completed_at"]!.GetValue<string>()) - DateTimeOffset.Parse(span["started_at"]!.GetValue<string>())).TotalSeconds;

    private static TValue GetOrAdd<TValue>(Dictionary<string, TValue> dictionary, string key) where TValue : new()
    {
        if (!dictionary.TryGetValue(key, out TValue? value))
        {
            dictionary[key] = value = new TValue();
        }

        return value;
    }

    [GeneratedRegex(@"^(test_\w+)\[fork_(\w+?)[-\]]")]
    private static partial Regex NethtestCase();
}

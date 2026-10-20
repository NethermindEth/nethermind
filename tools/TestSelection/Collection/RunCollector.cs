// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Nethermind.TestSelection.GitHub;
using Nethermind.TestSelection.Journal;

namespace Nethermind.TestSelection.Collection;

/// <summary>
/// Adds the finished runs of the test workflows to the journal. Each call reads the runs since
/// <see cref="Overlap"/> before the newest entry, so a run that finished late is not lost, and grows a young journal
/// back by <see cref="BackfillStep"/> until it spans <see cref="Retention"/>. A run that cannot be read is left for a
/// later call.
/// </summary>
public sealed class RunCollector(ActionsApi actions, ChangedFiles changedFiles, RunFailures failures, IReadOnlyList<string> workflows)
{
    public static readonly TimeSpan Overlap = TimeSpan.FromHours(6);
    public static readonly TimeSpan BackfillStep = TimeSpan.FromDays(3);
    public static readonly TimeSpan Retention = TimeSpan.FromDays(60);
    private const int Workers = 4;

    // Artifacts a run uploads when it used the selection.
    private static readonly string[] SelectionArtifacts = ["eest-selection", "test-selection"];

    public async Task<int> CollectAsync(string journalPath, DateTimeOffset now, CancellationToken cancellation)
    {
        List<JournalEntry> journal = [.. RunJournal.Read(journalPath)];
        HashSet<long> known = [.. journal.Select(static entry => entry.Run)];
        List<JsonNode> runs = [.. (await WindowsAsync(journal, now, cancellation)).Where(run => IsCollectable(run) && !known.Contains(RunId(run)))];

        ConcurrentBag<JournalEntry> read = [];
        await Parallel.ForEachAsync(runs, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = cancellation },
            async (run, token) =>
            {
                if (await ReadAsync(run, token) is { } entry)
                {
                    read.Add(entry);
                }
            });

        DateTimeOffset cutoff = now - Retention;
        RunJournal.Write(journalPath, journal.Concat(read).Where(entry => entry.At >= cutoff).OrderBy(static entry => entry.At));
        return runs.Count;
    }

    public async Task<List<JsonNode>> WindowsAsync(IReadOnlyList<JournalEntry> journal, DateTimeOffset now, CancellationToken cancellation)
    {
        if (journal.Count == 0)
        {
            return await actions.RunsAsync(workflows, now - BackfillStep, now, cancellation);
        }

        DateTimeOffset newest = journal[^1].At;
        DateTimeOffset oldest = journal[0].At;
        List<JsonNode> runs = await actions.RunsAsync(workflows, newest - Overlap, now, cancellation);
        if (oldest > now - Retention)
        {
            DateTimeOffset from = oldest - BackfillStep > now - Retention ? oldest - BackfillStep : now - Retention;
            runs.AddRange(await actions.RunsAsync(workflows, from, oldest, cancellation));
        }

        return runs;
    }

    public static bool IsCollectable(JsonNode run) =>
        run["conclusion"]?.GetValue<string>() is "success" or "failure" && run["event"]?.GetValue<string>() is JournalEntry.PullRequest or JournalEntry.Push;

    private async Task<JournalEntry?> ReadAsync(JsonNode run, CancellationToken cancellation)
    {
        long id = RunId(run);
        try
        {
            return new JournalEntry(
                id,
                DateTimeOffset.Parse(run["created_at"]!.GetValue<string>()),
                run["event"]!.GetValue<string>(),
                run["head_branch"]!.GetValue<string>(),
                await changedFiles.OfRunAsync(run, cancellation),
                await failures.ReadAsync(run, cancellation),
                (await actions.ArtifactNamesAsync(id, cancellation)).Overlaps(SelectionArtifacts));
        }
        catch (GitHubException error)
        {
            await Console.Error.WriteLineAsync($"skipping run {id}: {error.Message}");
            return null;
        }
    }

    private static long RunId(JsonNode run) => run["id"]!.GetValue<long>();
}

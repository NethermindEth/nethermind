// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.TestSelection.Files;
using Nethermind.TestSelection.Journal;
using Nethermind.TestSelection.Selection;

namespace Nethermind.TestSelection.MasterFailures;

/// <summary>A pull request merged into master: its squash commit, number, author and changed files.</summary>
public sealed record Merge(string Sha, int? Number, string? Author, IReadOnlyList<string> Files);

/// <summary>A merge that may have broken master, and why.</summary>
public sealed record Suspect(Merge Merge, string Reason);

/// <summary>
/// Finds the merge that broke master: the failures that the environment does not explain, and among the merges since
/// the last green run, the ones with the strongest link to the failing tests.
/// </summary>
public static class Attribution
{
    public static readonly IReadOnlyList<string> Reasons =
    [
        "changes the failing tests or a project they reference",
        "the history links its projects to the failures",
        "changes a project the failing tests depend on",
        "changes the whole build",
    ];

    public const string OnlyMerge = "the only merge since the last green run";

    /// <summary>
    /// The test failures of a master run that the flakiness before the merges does not explain. Failures on other
    /// branches after the merges stay: a broken master breaks the pull requests based on it. Hive is left out.
    /// </summary>
    public static IReadOnlyList<string> Regressions(Failures failed, IReadOnlyList<JournalEntry> journal, DateTimeOffset since)
    {
        DateTimeOffset flakyFrom = since - EnvironmentFilter.Window;
        Dictionary<string, HashSet<string>> branches = [];
        foreach (JournalEntry entry in journal)
        {
            if (entry.IsMasterPush || entry.At < flakyFrom || entry.At > since)
            {
                continue;
            }

            foreach (string test in entry.Failed.Tests)
            {
                branches.GetOrAdd(test).Add(entry.Branch);
            }
        }

        return [.. failed.Tests
            .Where(test => !test.StartsWith("Hive|", StringComparison.Ordinal) && branches.GetValueOrDefault(test)?.Count is null or < EnvironmentFilter.OtherBranches)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>The merges with the strongest link to the failures. A single merge is the suspect on its own.</summary>
    public static IReadOnlyList<Suspect> Suspects(IReadOnlyList<Merge> merges, IReadOnlyList<string> tests, Projects projects, SelectorLinks links)
    {
        Failures failures = new(tests, []);
        FailingTests failing = new(failures.Projects(), failures.EestTests().Select(static test => test.Name).ToHashSet());
        List<(int Rank, Merge Merge)> ranked = [];
        foreach (Merge merge in merges)
        {
            if (Rank(projects.Changed(merge.Files), failing, projects, links) is { } rank)
            {
                ranked.Add((rank, merge));
            }
        }

        if (ranked.Count == 0)
        {
            return merges.Count == 1 ? [new Suspect(merges[0], OnlyMerge)] : [];
        }

        int best = ranked.Min(static pair => pair.Rank);
        return [.. ranked.Where(pair => pair.Rank == best).Select(static pair => new Suspect(pair.Merge, Reasons[pair.Rank]))];
    }

    /// <summary>The index into <see cref="Reasons"/> of the strongest link from a change to the failures, or <c>null</c>.</summary>
    private static int? Rank(Change change, FailingTests failing, Projects projects, SelectorLinks links)
    {
        if (change.TouchesEverything)
        {
            return 3;
        }

        if (failing.Projects.Overlaps(projects.DirectTests(change.Projects)) || failing.Eest.Count > 0 && change.Projects.Overlaps(Selector.EestHarness))
        {
            return 0;
        }

        if (failing.Projects.Overlaps(links.LinkedProjects(change.Projects)) || failing.Eest.Overlaps(links.LinkedTests(change.Projects)))
        {
            return 1;
        }

        // EEST runs the whole client, so any changed project can break it.
        bool dependedOn = failing.Projects.Any(project => change.Projects.Overlaps(projects.Dependencies(Projects.DirectoryOf(project))));
        return dependedOn || failing.Eest.Count > 0 && change.Projects.Count > 0 ? 2 : null;
    }

    /// <summary>The failing NUnit test projects and EEST test functions.</summary>
    private sealed record FailingTests(IReadOnlySet<string> Projects, IReadOnlySet<string> Eest);
}

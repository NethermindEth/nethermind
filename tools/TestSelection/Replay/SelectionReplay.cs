// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.TestSelection.Costs;
using Nethermind.TestSelection.Journal;
using Nethermind.TestSelection.Selection;

namespace Nethermind.TestSelection.Replay;

/// <summary>
/// Decides for every recorded pull request run what the selector would have run with only the history before it, and
/// which of its failures it would have hidden. Runs that used the selection are not measured: they could only fail in
/// the tests they ran.
/// </summary>
public sealed class SelectionReplay(CostInventory inventory, Projects projects)
{
    // The first days of the journal only build history.
    public static readonly TimeSpan Warmup = TimeSpan.FromDays(7);

    public ReplayReport Run(IReadOnlyList<JournalEntry> journal, DateTimeOffset measureFrom)
    {
        ReplayReport report = new();
        Selector selector = new();
        foreach (JournalEntry entry in EnvironmentFilter.WithoutEnvironment(journal))
        {
            Change change = projects.Changed(entry.Files);
            IReadOnlySet<EestTest> tests = entry.EestFailures();
            IReadOnlySet<string> failedProjects = entry.FailedProjects();
            if (entry.IsPullRequest && !entry.Selected && entry.At >= measureFrom)
            {
                Measure(report, selector.Decide(entry.Run.ToString(), entry.At, change, projects.DirectTests(change.Projects)), entry, tests, failedProjects);
            }

            selector.Observe(entry.At, change, tests, failedProjects);
        }

        return report;
    }

    private void Measure(ReplayReport report, RunSelection? selection, JournalEntry entry, IReadOnlySet<EestTest> tests, IReadOnlySet<string> failedProjects)
    {
        report.Measured++;
        report.Eest.Measure(
            entry,
            selection is null ? 1.0 : inventory.ExpectedEestShare(selection),
            [.. tests.Select(static test => $"{test.Name} ({test.Fork})").Order(StringComparer.Ordinal)],
            selection is not null && !tests.Any(test => selection.KeepsTest(test.Name, test.Fork)));
        report.NUnit.Measure(
            entry,
            selection is null ? 1.0 : inventory.ProjectShare(selection),
            [.. failedProjects.Order(StringComparer.Ordinal)],
            selection is not null && !failedProjects.Any(selection.KeepsProject));
    }
}

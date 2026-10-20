// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.TestSelection.Journal;

namespace Nethermind.TestSelection.Selection;

/// <summary>
/// Decides which tests a pull request runs, from the failures that past runs recorded.
/// <list type="bullet">
/// <item>An EEST test runs when it failed before together with a changed project, or when a changed project broke at
/// least <see cref="ForkLinkMinTests"/> tests of its fork in the last <see cref="ForkLinkWindow"/>: a fork under
/// development breaks in new places.</item>
/// <item>An NUnit test project runs when it or a project it references directly changed, or when it failed before
/// together with a changed project.</item>
/// <item>Anything else runs when it falls in the deterministic sample. Harness and build-wide changes, and a
/// <see cref="HoldoutRate"/> share of runs, run everything; the holdout keeps the history unbiased by the selection.</item>
/// </list>
/// Runs that break more than <see cref="MassTests"/> EEST tests or <see cref="MassProjects"/> test projects teach no
/// links: they would tie the changed projects to everything, and the direct references and the sample catch such
/// changes.
/// </summary>
public sealed class Selector(SelectorLinks links)
{
    public const int MassTests = 500;
    public const int MassProjects = 20;
    public const int ForkLinkMinTests = 5;
    public const double HoldoutRate = 0.05;
    public static readonly TimeSpan ForkLinkWindow = TimeSpan.FromDays(14);

    /// <summary>Changes to these projects can alter any EEST result.</summary>
    public static readonly IReadOnlySet<string> EestHarness = new HashSet<string>
    {
        Projects.DirectoryOf("Ethereum.Test.Base"),
        Projects.DirectoryOf("Nethermind.Test.Runner"),
    };

    // Legacy fixture suites that EEST covers: in 90 days of pull requests each failed only on branches where EEST
    // failed too. A pull request runs them when it runs everything or changes them; master runs them always.
    private static readonly IReadOnlySet<string> CoveredByEest = new HashSet<string> { "Ethereum.Legacy.Blockchain.Test" };

    public Selector() : this(new SelectorLinks())
    {
    }

    public SelectorLinks Links => links;

    public static Selector Trained(IReadOnlyList<JournalEntry> journal, Projects projects)
    {
        Selector selector = new();
        foreach (JournalEntry entry in EnvironmentFilter.WithoutEnvironment(journal))
        {
            selector.Observe(entry.At, projects.Changed(entry.Files), entry.EestFailures(), entry.FailedProjects());
        }

        return selector;
    }

    /// <summary>What the run executes, or <c>null</c> when it runs everything.</summary>
    public RunSelection? Decide(string run, DateTimeOffset at, Change change, IReadOnlySet<string> directTests)
    {
        if (change.TouchesEverything || change.Projects.Overlaps(EestHarness) || Sampling.IsSampled($"holdout|{run}", HoldoutRate))
        {
            return null;
        }

        HashSet<string> projects = [.. directTests, .. links.LinkedProjects(change.Projects)];
        foreach (string covered in CoveredByEest)
        {
            if (!change.Projects.Contains(Projects.DirectoryOf(covered)))
            {
                projects.Remove(covered);
            }
        }

        return new RunSelection(run, links.LinkedTests(change.Projects), links.LinkedForks(change.Projects, at - ForkLinkWindow, ForkLinkMinTests), projects);
    }

    /// <summary>Learns from one run. A change that touches everything names no project, so it teaches nothing.</summary>
    public void Observe(DateTimeOffset at, Change change, IReadOnlySet<EestTest> tests, IReadOnlySet<string> projects)
    {
        if (tests.Count > 0 && tests.Select(static test => test.Name).Distinct().Count() <= MassTests)
        {
            links.LinkTests(change.Projects, at, tests);
        }

        if (projects.Count > 0 && projects.Count <= MassProjects)
        {
            links.LinkProjects(change.Projects, projects);
        }
    }
}

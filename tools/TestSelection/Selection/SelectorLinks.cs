// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.TestSelection.Files;
using Nethermind.TestSelection.Journal;

namespace Nethermind.TestSelection.Selection;

/// <summary>
/// What the selector learned: the EEST tests and the test projects that failed together with each project, and when
/// each project broke the tests of a fork.
/// </summary>
public sealed class SelectorLinks
{
    private readonly Dictionary<string, HashSet<string>> _tests = [];
    private readonly Dictionary<string, HashSet<string>> _projects = [];
    private readonly Dictionary<(string Project, string Fork), List<(DateTimeOffset At, string Test)>> _forks = [];

    public IReadOnlyDictionary<string, HashSet<string>> Tests => _tests;
    public IReadOnlyDictionary<string, HashSet<string>> TestProjects => _projects;
    public IReadOnlyDictionary<(string Project, string Fork), List<(DateTimeOffset At, string Test)>> Forks => _forks;

    public void LinkTests(IEnumerable<string> changed, DateTimeOffset at, IReadOnlySet<EestTest> tests)
    {
        foreach (string project in changed)
        {
            foreach (EestTest test in tests)
            {
                Add(_tests, project, test.Name);
                LinkFork(project, test.Fork, at, test.Name);
            }
        }
    }

    public void LinkProjects(IEnumerable<string> changed, IReadOnlySet<string> testProjects)
    {
        foreach (string project in changed)
        {
            foreach (string testProject in testProjects)
            {
                Add(_projects, project, testProject);
            }
        }
    }

    public void LinkProject(string project, string testProject) => Add(_projects, project, testProject);

    public void LinkTest(string project, string test) => Add(_tests, project, test);

    public void LinkFork(string project, string fork, DateTimeOffset at, string test) => _forks.GetOrAdd((project, fork)).Add((at, test));

    public IReadOnlySet<string> LinkedTests(IEnumerable<string> changed) => Union(_tests, changed);

    public IReadOnlySet<string> LinkedProjects(IEnumerable<string> changed) => Union(_projects, changed);

    /// <summary>Forks of which a changed project broke at least <paramref name="minTests"/> tests since <paramref name="since"/>.</summary>
    public IReadOnlySet<string> LinkedForks(IReadOnlySet<string> changed, DateTimeOffset since, int minTests)
    {
        HashSet<string> forks = [];
        foreach (((string project, string fork), List<(DateTimeOffset At, string Test)> events) in _forks)
        {
            if (changed.Contains(project) && events.Where(e => e.At >= since).Select(static e => e.Test).Distinct().Count() >= minTests)
            {
                forks.Add(fork);
            }
        }

        return forks;
    }

    private static void Add(Dictionary<string, HashSet<string>> links, string project, string linked) => links.GetOrAdd(project).Add(linked);

    private static HashSet<string> Union(Dictionary<string, HashSet<string>> links, IEnumerable<string> changed)
    {
        HashSet<string> union = [];
        foreach (string project in changed)
        {
            if (links.TryGetValue(project, out HashSet<string>? linked))
            {
                union.UnionWith(linked);
            }
        }

        return union;
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.TestSelection.Files;
using Nethermind.TestSelection.Journal;
using Nethermind.TestSelection.Selection;

namespace Nethermind.TestSelection.Costs;

/// <summary>
/// Cost in runner seconds of every EEST unit (lane <c>type|setup</c>, test function, fork) and every NUnit test
/// project, from one full run of the main test workflow.
/// </summary>
public sealed class CostInventory(
    IReadOnlyDictionary<string, Dictionary<string, Dictionary<string, double>>> eest,
    IReadOnlyDictionary<string, double> projects)
{
    private readonly Dictionary<EestTest, double> _testCosts = CostsByTest(eest);

    public IReadOnlyDictionary<string, Dictionary<string, Dictionary<string, double>>> Eest => eest;
    public IReadOnlyDictionary<string, double> Projects => projects;

    public static CostInventory Load(string path)
    {
        InventoryFile file = JsonFiles.Read<InventoryFile>(path);
        return new CostInventory(file.Eest, file.Projects);
    }

    public void Save(string path) => JsonFiles.Write(path, new InventoryFile(
        eest.ToDictionary(static pair => pair.Key, static pair => pair.Value),
        projects.ToDictionary(static pair => pair.Key, static pair => pair.Value)));

    /// <summary>
    /// Share of the EEST cost the selection runs. The sample counts by its expected share instead of being drawn for
    /// every test: the replay asks this for thousands of runs.
    /// </summary>
    public double ExpectedEestShare(RunSelection selection)
    {
        double total = 0;
        double linked = 0;
        foreach ((EestTest test, double cost) in _testCosts)
        {
            total += cost;
            if (selection.Tests.Contains(test.Name) || selection.Forks.Contains(test.Fork))
            {
                linked += cost;
            }
        }

        return (linked + Sampling.SampleRate * (total - linked)) / total;
    }

    public double ProjectShare(RunSelection selection)
    {
        double total = 0;
        double kept = 0;
        foreach ((string project, double cost) in projects)
        {
            total += cost;
            if (selection.KeepsProject(project))
            {
                kept += cost;
            }
        }

        return kept / total;
    }

    public IReadOnlyDictionary<string, LaneSelection> Lanes(RunSelection selection)
    {
        Dictionary<string, LaneSelection> lanes = [];
        foreach ((string lane, Dictionary<string, Dictionary<string, double>> tests) in eest)
        {
            LaneSelection laneSelection = new();
            foreach ((string name, Dictionary<string, double> forks) in tests)
            {
                foreach ((string fork, double cost) in forks)
                {
                    laneSelection.Add(name, fork, cost, selection.KeepsTest(name, fork));
                }
            }

            lanes[lane] = laneSelection;
        }

        return lanes;
    }

    private static Dictionary<EestTest, double> CostsByTest(IReadOnlyDictionary<string, Dictionary<string, Dictionary<string, double>>> eest)
    {
        Dictionary<EestTest, double> costs = [];
        foreach (Dictionary<string, Dictionary<string, double>> tests in eest.Values)
        {
            foreach ((string name, Dictionary<string, double> forks) in tests)
            {
                foreach ((string fork, double cost) in forks)
                {
                    EestTest test = new(name, fork);
                    costs[test] = costs.GetValueOrDefault(test) + cost;
                }
            }
        }

        return costs;
    }

    private sealed record InventoryFile(
        Dictionary<string, Dictionary<string, Dictionary<string, double>>> Eest,
        Dictionary<string, double> Projects);
}

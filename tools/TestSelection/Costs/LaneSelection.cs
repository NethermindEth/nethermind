// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.RegularExpressions;

namespace Nethermind.TestSelection.Costs;

/// <summary>What one EEST lane runs: its cost, the cost selected, and the forks selected per test function.</summary>
public sealed class LaneSelection
{
    private readonly SortedDictionary<string, SortedSet<string>> _forks = new(StringComparer.Ordinal);

    public double Cost { get; private set; }
    public double Picked { get; private set; }
    public bool IsEmpty => _forks.Count == 0;

    public void Add(string name, string fork, double cost, bool kept)
    {
        Cost += cost;
        if (!kept)
        {
            return;
        }

        Picked += cost;
        if (!_forks.TryGetValue(name, out SortedSet<string>? forks))
        {
            _forks[name] = forks = new SortedSet<string>(StringComparer.Ordinal);
        }

        forks.Add(fork);
    }

    /// <summary>
    /// nethtest <c>--filterFile</c> lines. Each matches the test name prefix <c>test_x[fork_Y-</c> or
    /// <c>test_x[fork_Y]</c>, so a fork never matches another whose name starts with it.
    /// </summary>
    public IEnumerable<string> FilterLines() =>
        _forks.Select(static pair => $@"{Regex.Escape(pair.Key)}\[fork_(?:{string.Join('|', pair.Value.Select(Regex.Escape))})[-\]]");
}

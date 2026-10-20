// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.TestSelection.Journal;

namespace Nethermind.TestSelection.Replay;

/// <summary>A failing run whose failures the selection would not have run.</summary>
public sealed record HiddenRun(JournalEntry Entry, IReadOnlyList<string> Failures);

/// <summary>Replay results of one kind of test: EEST tests or NUnit test projects.</summary>
public sealed class SuiteReplay
{
    private readonly List<JournalEntry> _failing = [];
    private readonly List<HiddenRun> _hidden = [];

    public double SelectedCost { get; private set; }
    public IReadOnlyList<JournalEntry> Failing => _failing;
    public IReadOnlyList<HiddenRun> Hidden => _hidden;

    public void Measure(JournalEntry entry, double share, IReadOnlyList<string> failures, bool hidden)
    {
        SelectedCost += share;
        if (failures.Count == 0)
        {
            return;
        }

        _failing.Add(entry);
        if (hidden)
        {
            _hidden.Add(new HiddenRun(entry, failures));
        }
    }
}

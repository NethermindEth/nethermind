// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.TestSelection.Selection;

/// <summary>
/// What one pull request run executes: EEST tests by function and fork, NUnit test projects by name, and a
/// <see cref="Sampling.SampleRate"/> sample of everything else drawn from the run id.
/// </summary>
public sealed record RunSelection(string Run, IReadOnlySet<string> Tests, IReadOnlySet<string> Forks, IReadOnlySet<string> Projects)
{
    public bool KeepsTest(string name, string fork) =>
        Tests.Contains(name) || Forks.Contains(fork) || Sampling.IsSampled($"{Run}|{name}|{fork}");

    public bool KeepsProject(string project) => Projects.Contains(project) || Sampling.IsSampled($"{Run}|{project}");
}

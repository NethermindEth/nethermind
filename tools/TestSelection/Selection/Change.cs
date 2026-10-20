// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.TestSelection.Selection;

/// <summary>
/// The projects a change touches, as repository-relative project directories, or a change that can affect every
/// test: a build file, a test workflow or a fixture submodule.
/// </summary>
public sealed record Change(IReadOnlySet<string> Projects, bool TouchesEverything)
{
    public static Change Everything { get; } = new(new HashSet<string>(), true);

    public static Change Of(IReadOnlySet<string> projects) => new(projects, false);
}

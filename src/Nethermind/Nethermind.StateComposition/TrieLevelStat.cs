// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.StateComposition;

/// <summary>
/// Per-depth node statistics.
/// Short=Extension, Full=Branch, Value=Leaf.
/// </summary>
public readonly record struct TrieLevelStat
{
    public int Depth { get; init; }
    public long ShortNodeCount { get; init; }
    public long FullNodeCount { get; init; }
    public long ValueNodeCount { get; init; }
    public long TotalSize { get; init; }
}

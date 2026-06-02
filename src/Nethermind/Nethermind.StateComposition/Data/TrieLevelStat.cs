// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.StateComposition.Data;

/// <summary>
/// Per-depth node statistics using Geth's trie node vocabulary for cross-client parity.
/// Field names mirror <c>go-ethereum/trie/inspect.go:jsonLevel</c>:
/// <list type="bullet">
/// <item><description><see cref="FullNodeCount"/> = branch nodes (Geth <c>fullNode</c>)</description></item>
/// <item><description><see cref="ShortNodeCount"/> = extension + leaf nodes combined (Geth <c>shortNode</c>)</description></item>
/// <item><description><see cref="ValueNodeCount"/> = leaf nodes only (Geth <c>valueNode</c>, a subset of ShortNode)</description></item>
/// </list>
/// Extensions alone = <c>ShortNodeCount - ValueNodeCount</c>.
/// </summary>
public readonly record struct TrieLevelStat
{
    public int Depth { get; init; }
    public long ShortNodeCount { get; init; }
    public long FullNodeCount { get; init; }
    public long ValueNodeCount { get; init; }
    public long TotalSize { get; init; }
}

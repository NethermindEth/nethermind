// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Metric;

namespace Nethermind.State.Flat.Persistence.TrieNodeLog;

/// <summary>Single-valued label key for the trie node log metrics.</summary>
public readonly record struct TrieNodeLogLabel(string Value) : IMetricLabels
{
    public static readonly TrieNodeLogLabel Hit = new("hit");
    public static readonly TrieNodeLogLabel Chain = new("chain");
    public static readonly TrieNodeLogLabel Miss = new("miss");
    public static readonly TrieNodeLogLabel Active = new("active");
    public static readonly TrieNodeLogLabel Sealed = new("sealed");
    public static readonly TrieNodeLogLabel MergedPinned = new("merged_pinned");

    public static readonly TrieNodeLogLabel State = new("state");
    public static readonly TrieNodeLogLabel Storage = new("storage");

    public static int ColumnCount { get; } = Enum.GetValues<FlatDbColumns>().Length;

    public string[] Labels => [Value];

    /// <summary>The column label of a logged column: state for both state trie columns, storage for storage.</summary>
    public static TrieNodeLogLabel Column(FlatDbColumns column) => column == FlatDbColumns.StorageNodes ? Storage : State;
}

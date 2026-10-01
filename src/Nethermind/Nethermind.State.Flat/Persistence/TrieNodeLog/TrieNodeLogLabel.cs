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

    private static readonly TrieNodeLogLabel[] Columns = CreateColumnLabels();

    public string[] Labels => [Value];

    public static TrieNodeLogLabel Column(byte column) => Columns[column];

    public static int ColumnCount => Columns.Length;

    private static TrieNodeLogLabel[] CreateColumnLabels()
    {
        TrieNodeLogLabel[] labels = new TrieNodeLogLabel[Enum.GetValues<FlatDbColumns>().Length];
        foreach (FlatDbColumns column in Enum.GetValues<FlatDbColumns>())
        {
            labels[(int)column] = new TrieNodeLogLabel(column switch
            {
                FlatDbColumns.StateTopNodes => "state_top",
                FlatDbColumns.StateNodes => "state",
                FlatDbColumns.StorageNodes => "storage",
                FlatDbColumns.FallbackNodes => "fallback",
                _ => column.ToString().ToLowerInvariant(),
            });
        }
        return labels;
    }
}

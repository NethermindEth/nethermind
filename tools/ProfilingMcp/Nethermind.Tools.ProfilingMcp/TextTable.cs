// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Tools.ProfilingMcp;

/// <summary>Renders rows as a fixed-width text table; the last column is left-aligned and unpadded, the rest right-aligned.</summary>
internal sealed class TextTable(params string[] headers)
{
    private readonly List<string[]> _rows = [];

    public void Add(params string[] cells) => _rows.Add(cells);

    public int Count => _rows.Count;

    public override string ToString()
    {
        int[] widths = new int[headers.Length];
        for (int i = 0; i < headers.Length; i++)
            widths[i] = headers[i].Length;
        foreach (string[] row in _rows)
        {
            for (int i = 0; i < row.Length; i++)
                widths[i] = Math.Max(widths[i], row[i].Length);
        }

        StringBuilder sb = new();
        AppendRow(sb, headers, widths);
        foreach (string[] row in _rows)
            AppendRow(sb, row, widths);
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, string[] cells, int[] widths)
    {
        int last = cells.Length - 1;
        for (int i = 0; i < last; i++)
            sb.Append(cells[i].PadLeft(widths[i])).Append("  ");
        sb.Append(cells[last]).Append('\n');
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;

namespace Nethermind.State.Flat.History.Walk;

internal sealed class HistoryRowCursor
{
    private const int ProbeRows = 4096;
    private const ulong MinWindow = 1024;

    private readonly ISortedKeyValueStore _rows;
    private readonly HistoryRowFormat _rowFormat;
    private readonly byte[] _flatKey;
    private readonly ulong _from;
    private readonly ulong _to;

    public HistoryRowCursor(ISortedKeyValueStore rows, HistoryRowFormat rowFormat, ReadOnlySpan<byte> flatKey, ulong from, ulong to)
    {
        _rows = rows;
        _rowFormat = rowFormat;
        _flatKey = flatKey.ToArray();
        _from = from;
        _to = to;
    }

    public bool TryReadStart(out ulong block, out byte[] value)
    {
        block = 0;
        value = [];
        Span<byte> lower = stackalloc byte[_flatKey.Length + sizeof(ulong)];
        WriteRowKey(lower, _from);
        Span<byte> upper = stackalloc byte[_flatKey.Length + sizeof(ulong) + 1];
        _flatKey.CopyTo(upper);
        upper[_flatKey.Length..].Fill(0xFF);
        upper[^1] = 0x00;

        using ISortedView view = _rows.GetViewBetween(lower, upper);
        while (view.MoveNext())
        {
            if (!Matches(view.CurrentKey)) continue;

            block = _rowFormat.DecodeSuffixBlock(view.CurrentKey[_flatKey.Length..]);
            value = view.CurrentValue.ToArray();
            return true;
        }

        return false;
    }

    public IEnumerable<(ulong Block, byte[] Value)> Ascending()
    {
        List<(ulong Block, byte[] Value)> probe = ReadDescending(_from + 1, _to, ProbeRows, out bool complete);
        if (complete)
        {
            for (int i = probe.Count - 1; i >= 0; i--) yield return probe[i];
            yield break;
        }

        ulong probedSpan = _to - probe[^1].Block + 1;
        ulong window = Math.Max(MinWindow, probedSpan * ProbeRows / (ulong)probe.Count);
        for (ulong lo = _from + 1; lo <= _to; lo += window)
        {
            ulong hi = _to - lo < window - 1 ? _to : lo + window - 1;
            List<(ulong Block, byte[] Value)> rows = ReadDescending(lo, hi, int.MaxValue, out _);
            for (int i = rows.Count - 1; i >= 0; i--) yield return rows[i];
            if (hi == _to) break;
        }
    }

    private List<(ulong Block, byte[] Value)> ReadDescending(ulong lo, ulong hi, int limit, out bool complete)
    {
        List<(ulong Block, byte[] Value)> rows = [];
        complete = true;
        if (lo > hi) return rows;

        Span<byte> lower = stackalloc byte[_flatKey.Length + sizeof(ulong)];
        WriteRowKey(lower, hi);
        Span<byte> upper = stackalloc byte[_flatKey.Length + sizeof(ulong)];
        WriteRowKey(upper, lo - 1);

        using ISortedView view = _rows.GetViewBetween(lower, upper);
        while (view.MoveNext())
        {
            if (!Matches(view.CurrentKey)) continue;

            ulong block = _rowFormat.DecodeSuffixBlock(view.CurrentKey[_flatKey.Length..]);
            if (block < lo || block > hi) continue;

            if (rows.Count >= limit)
            {
                complete = false;
                break;
            }

            rows.Add((block, view.CurrentValue.ToArray()));
        }

        return rows;
    }

    private bool Matches(ReadOnlySpan<byte> key) => key.Length == _flatKey.Length + sizeof(ulong) && key[.._flatKey.Length].SequenceEqual(_flatKey);

    private void WriteRowKey(Span<byte> destination, ulong block)
    {
        _flatKey.CopyTo(destination);
        BinaryPrimitives.WriteUInt64BigEndian(destination[_flatKey.Length..], ~block);
    }
}

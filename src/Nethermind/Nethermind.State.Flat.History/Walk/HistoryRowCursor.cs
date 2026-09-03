// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.History.Walk;

internal sealed class HistoryRowCursor : IDisposable
{
    public const int ProbeRows = 4096;
    public const int MaxWindowRows = 4 * ProbeRows;
    private const ulong MinWindow = 1024;
    private const int MaxRowKeyLength = BaseFlatPersistence.StorageKeyLength + sizeof(ulong);

    private readonly ISortedKeyValueStore _rows;
    private readonly HistoryRowFormat _rowFormat;
    private readonly byte[] _flatKey;
    private readonly ulong _from;
    private readonly ulong _to;
    private readonly CancellationToken _token;
    private readonly RowArena _arena = new();
    private readonly ArrayPoolList<(ulong Block, int Offset, int Length)> _window = new(ProbeRows);
    private int _position = -1;
    private ulong _nextLow;
    private ulong _windowSize;
    private bool _probed;
    private bool _exhausted;

    public HistoryRowCursor(ISortedKeyValueStore rows, HistoryRowFormat rowFormat, ReadOnlySpan<byte> flatKey, ulong from, ulong to, CancellationToken token)
    {
        if (flatKey.Length + sizeof(ulong) > MaxRowKeyLength) throw new ArgumentOutOfRangeException(nameof(flatKey));

        _rows = rows;
        _rowFormat = rowFormat;
        _flatKey = flatKey.ToArray();
        _from = from;
        _to = to;
        _token = token;
        _nextLow = from + 1;
    }

    public ulong Block => _window[_position].Block;

    public ReadOnlySpan<byte> Value
    {
        get
        {
            (ulong _, int offset, int length) = _window[_position];
            return _arena.Slice(offset, length);
        }
    }

    public bool TryReadStart(out ulong block, out byte[] value)
    {
        block = 0;
        value = [];
        Span<byte> lower = stackalloc byte[MaxRowKeyLength];
        int keyLength = WriteRowKey(lower, _from);
        Span<byte> upper = stackalloc byte[MaxRowKeyLength + 1];
        _flatKey.CopyTo(upper);
        upper[_flatKey.Length..].Fill(0xFF);
        upper[keyLength] = 0x00;

        using ISortedView view = _rows.GetViewBetween(lower[..keyLength], upper[..(keyLength + 1)], ReadFlags.HintCacheMiss);
        while (view.MoveNext())
        {
            if (!Matches(view.CurrentKey)) continue;

            block = _rowFormat.DecodeSuffixBlock(view.CurrentKey[_flatKey.Length..]);
            value = view.CurrentValue.ToArray();
            return true;
        }

        return false;
    }

    public bool MoveNext()
    {
        if (_position + 1 < _window.Count)
        {
            _position++;
            return true;
        }

        while (!_exhausted)
        {
            _token.ThrowIfCancellationRequested();
            if (FillNextWindow() && _window.Count > 0)
            {
                _position = 0;
                return true;
            }
        }

        return false;
    }

    private bool FillNextWindow()
    {
        if (_nextLow > _to)
        {
            _exhausted = true;
            return false;
        }

        if (!_probed)
        {
            _probed = true;
            ReadDescending(_nextLow, _to, ProbeRows, out bool complete);
            if (complete)
            {
                _exhausted = true;
                ReverseWindow();
                return true;
            }

            ulong probedSpan = _window[0].Block - _window[^1].Block + 1;
            _windowSize = Math.Max(MinWindow, probedSpan * ProbeRows / (ulong)_window.Count);
        }

        while (true)
        {
            ulong hi = _to - _nextLow < _windowSize - 1 ? _to : _nextLow + _windowSize - 1;
            ReadDescending(_nextLow, hi, MaxWindowRows, out bool complete);
            if (!complete && _windowSize > MinWindow)
            {
                _windowSize = Math.Max(MinWindow, _windowSize / 2);
                continue;
            }

            ReverseWindow();
            if (hi == _to) _exhausted = true;
            else _nextLow = hi + 1;
            return true;
        }
    }

    private void ReverseWindow()
    {
        for (int left = 0, right = _window.Count - 1; left < right; left++, right--)
        {
            (ulong Block, int Offset, int Length) swap = _window[left];
            _window[left] = _window[right];
            _window[right] = swap;
        }
    }

    private void ReadDescending(ulong lo, ulong hi, int limit, out bool complete)
    {
        _window.Clear();
        _arena.Clear();
        complete = true;
        if (lo > hi) return;

        Span<byte> lower = stackalloc byte[MaxRowKeyLength];
        int keyLength = WriteRowKey(lower, hi);
        Span<byte> upper = stackalloc byte[MaxRowKeyLength];
        WriteRowKey(upper, lo - 1);

        using ISortedView view = _rows.GetViewBetween(lower[..keyLength], upper[..keyLength], ReadFlags.HintCacheMiss);
        while (view.MoveNext())
        {
            if (!Matches(view.CurrentKey)) continue;

            ulong block = _rowFormat.DecodeSuffixBlock(view.CurrentKey[_flatKey.Length..]);
            if (block < lo || block > hi) continue;

            if (_window.Count >= limit)
            {
                complete = false;
                return;
            }

            ReadOnlySpan<byte> value = view.CurrentValue;
            _window.Add((block, _arena.Append(value), value.Length));
        }
    }

    public void Dispose()
    {
        _window.Dispose();
        _arena.Dispose();
    }

    private bool Matches(ReadOnlySpan<byte> key) => key.Length == _flatKey.Length + sizeof(ulong) && key[.._flatKey.Length].SequenceEqual(_flatKey);

    private int WriteRowKey(Span<byte> destination, ulong block)
    {
        _flatKey.CopyTo(destination);
        BinaryPrimitives.WriteUInt64BigEndian(destination[_flatKey.Length..], ~block);
        return _flatKey.Length + sizeof(ulong);
    }
}

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.State.Flat.Storage;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Span-backed <see cref="IHsstByteReader{TPin}"/> that, on every read or pin, computes which OS
/// page(s) the access spans (in arena-absolute terms) and reports them to a
/// <see cref="PageClockCache"/>. Page math: <c>pageIdx = (baseOffset + localOffset) / Environment.SystemPageSize</c>.
/// Otherwise identical to <see cref="SpanByteReader"/> — zero-copy slice, <see cref="NoOpPin"/>.
/// </summary>
public ref struct ArenaByteReader : IHsstByteReader<NoOpPin>
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly PageClockCache? _cache;
    private readonly int _arenaId;
    private readonly long _baseOffset;
    // OS page size is a power of two — use shift for division and mask for modulo.
    private readonly int _pageShift;
    private readonly long _pageMask;
    // Page-aligned absolute address of the last touched range. -1 sentinel = uninitialised.
    // Used to skip the per-page Touch loop when a single-page access stays within the same OS
    // page as the previous access — the common case for HSST seeks that re-read sequential
    // bytes within one node.
    private long _lastPageBase;

    public ArenaByteReader(ReadOnlySpan<byte> data, PageClockCache? cache, int arenaId, long baseOffset)
    {
        _data = data;
        _cache = cache;
        _arenaId = arenaId;
        _baseOffset = baseOffset;
        int pageSize = Environment.SystemPageSize;
        _pageShift = BitOperations.Log2((uint)pageSize);
        _pageMask = pageSize - 1;
        _lastPageBase = -1;
    }

    public long Length => _data.Length;

    public bool TryRead(long offset, scoped Span<byte> output)
    {
        if ((ulong)offset > (ulong)(_data.Length - output.Length)) return false;
        TouchRange(offset, output.Length);
        _data.Slice((int)offset, output.Length).CopyTo(output);
        return true;
    }

    public bool TryReadWithReadahead(long offset, scoped Span<byte> output) => TryRead(offset, output);

    public NoOpPin PinBuffer(long offset, long size)
    {
        if ((ulong)offset + (ulong)size > (ulong)_data.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        TouchRange(offset, size);
        return new NoOpPin(_data.Slice((int)offset, (int)size));
    }

    private void TouchRange(long localOffset, long length)
    {
        if (_cache is null || length <= 0) return;
        long absStart = _baseOffset + localOffset;
        long absEnd = absStart + length - 1;
        long startPageBase = absStart & ~_pageMask;
        long endPageBase = absEnd & ~_pageMask;
        // Fast path: access stays within a single OS page, and that page is the same as the
        // last touch — nothing new to report to the cache.
        if (startPageBase == endPageBase && startPageBase == _lastPageBase) return;
        _lastPageBase = endPageBase;

        int firstPage = (int)(absStart >> _pageShift);
        int lastPage = (int)(absEnd >> _pageShift);
        for (int p = firstPage; p <= lastPage; p++)
            _cache.Touch(_arenaId, p);
    }
}

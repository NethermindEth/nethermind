// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Io;

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// Pointer-backed <see cref="IByteReader{TPin}"/> over an arena-mmap region.
/// Holds a raw <c>byte*</c> + <see cref="long"/> length so the addressed region can exceed
/// 2 GiB (each individual pin still materialises an int-sized <see cref="ReadOnlySpan{T}"/>).
/// Each read or pin reports touched OS pages to <see cref="ArenaReservation.TouchRange"/> for residency
/// tracking only — the generic read path does not pre-fault; deliberate prefaults use
/// <see cref="ArenaReservation.TouchRangePopulate"/>.
/// </summary>
public unsafe ref struct ArenaByteReader : IByteReader<NoOpPin>
{
    private readonly byte* _basePtr;
    private readonly long _length;
    private readonly ArenaReservation _reservation;
    private readonly long _baseOffset;
    // OS page size is a power of two — mask for the in-page offset / page-base computation.
    private readonly long _pageMask;
    // Page-aligned absolute address of the last touched range. -1 sentinel = uninitialised.
    // Used to skip the per-page Touch loop when a single-page access stays within the same OS
    // page as the previous access — the common case for table seeks that re-read sequential
    // bytes within one node.
    private long _lastPageBase;

    public ArenaByteReader(byte* basePtr, long length, ArenaReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        _basePtr = basePtr;
        _length = length;
        _reservation = reservation;
        _baseOffset = reservation.Offset;
        _pageMask = PageLayout.OsPageSize - 1;
        _lastPageBase = -1;
    }

    public long Length => _length;

    public bool TryRead(long offset, scoped Span<byte> output)
    {
        if ((ulong)offset + (ulong)output.Length > (ulong)_length) return false;
        TouchRange(offset, output.Length);
        new ReadOnlySpan<byte>(_basePtr + offset, output.Length).CopyTo(output);
        return true;
    }

    public NoOpPin PinBuffer(Bound bound)
    {
        if ((ulong)bound.Offset + (ulong)bound.Length > (ulong)_length)
            throw new ArgumentOutOfRangeException(nameof(bound));
        TouchRange(bound.Offset, bound.Length);
        return new NoOpPin(new ReadOnlySpan<byte>(_basePtr + bound.Offset, checked((int)bound.Length)));
    }

    private void TouchRange(long localOffset, long length)
    {
        if (length <= 0) return;
        long absStart = _baseOffset + localOffset;
        long absEnd = absStart + length - 1;
        long startPageBase = absStart & ~_pageMask;
        long endPageBase = absEnd & ~_pageMask;
        if (startPageBase == endPageBase && startPageBase == _lastPageBase) return;
        _lastPageBase = endPageBase;

        _reservation.TouchRange(localOffset, length);
    }
}

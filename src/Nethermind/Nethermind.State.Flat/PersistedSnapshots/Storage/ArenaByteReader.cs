// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.Intrinsics.X86;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// Pointer-backed <see cref="IHsstByteReader{TPin}"/> over an arena-mmap region. On every
/// read or pin computes which OS page(s) the access spans (in arena-absolute terms) and
/// reports them to the owning <see cref="ArenaReservation"/> via <see cref="ArenaReservation.TouchRangePopulate"/>,
/// which folds residency tracking, local pre-fault, and same/cross-arena eviction dispatch
/// behind a single call. Page math uses <see cref="PageLayout.OsPageSize"/>.
/// Holds a raw <c>byte*</c> + <see cref="long"/> length so the addressed region can exceed
/// 2 GiB (each individual pin still materialises an int-sized <see cref="ReadOnlySpan{T}"/>).
/// </summary>
public unsafe ref struct ArenaByteReader : IHsstByteReader<NoOpPin>
{
    private readonly byte* _basePtr;
    private readonly long _length;
    private readonly ArenaReservation _reservation;
    private readonly long _baseOffset;
    // OS page size is a power of two — mask for the in-page offset / page-base computation.
    private readonly long _pageMask;
    // Page-aligned absolute address of the last touched range. -1 sentinel = uninitialised.
    // Used to skip the per-page Touch loop when a single-page access stays within the same OS
    // page as the previous access — the common case for HSST seeks that re-read sequential
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

    /// <summary>
    /// Prefetches the body of a BTree node whose first byte was just read (page + TLB now resident):
    /// pulls the two cache lines after the header line so the floor-search's key scan finds them warm.
    /// <paramref name="offset"/> is the node start; line 0 is already cached from the flag-byte read.
    /// </summary>
    public readonly void Prefetch(long offset)
    {
        if (!Sse.IsSupported || (ulong)offset >= (ulong)_length) return;
        byte* p = _basePtr + offset;
        Sse.Prefetch0(p + 64);
        Sse.Prefetch0(p + 128);
    }

    private void TouchRange(long localOffset, long length)
    {
        if (length <= 0) return;
        long absStart = _baseOffset + localOffset;
        long absEnd = absStart + length - 1;
        long startPageBase = absStart & ~_pageMask;
        long endPageBase = absEnd & ~_pageMask;
        // Fast path: access stays within a single OS page, and that page is the same as the
        // last touch — nothing new to report to the tracker.
        if (startPageBase == endPageBase && startPageBase == _lastPageBase) return;
        _lastPageBase = endPageBase;

        // Let the reservation probe every overlapping page and coalesce the pre-fault syscall.
        _reservation.TouchRangePopulate(localOffset, length);
    }
}

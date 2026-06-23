// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Io;

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// Scoped whole-buffer view over an <see cref="ArenaReservation"/>. Opens a fresh
/// per-reservation mmap view with <c>MADV_NORMAL</c> hint (distinct from the global
/// random-access view used by point queries) and acquires a lease on the reservation.
/// Disposing releases the lease; when <c>adviseDontNeedOnDispose</c> is <c>true</c> it
/// also issues <c>madvise(MADV_DONTNEED)</c> on the range so the kernel can reclaim those
/// pages from the page cache.
/// </summary>
/// <remarks>
/// Also serves as the <see cref="IByteReaderSource{TReader,TPin}"/> for the reservation:
/// the mmap base pointer is captured once at construction (one call on the underlying
/// <see cref="ArenaFile.MmapWholeView"/>) so <see cref="CreateReader"/> mints fresh
/// pointer-backed readers on the merge/scan hot path with no per-call indirection or
/// dispose check. Callers must keep the session alive while any reader derived from it
/// is in use.
/// </remarks>
public sealed unsafe class WholeReadSession : IDisposable, IByteReaderSource<WholeReadSessionReader, NoOpPin>
{
    private readonly ArenaReservation _reservation;
    private readonly ArenaFile.MmapWholeView _view;
    private readonly byte* _basePtr;
    private readonly long _size;
    private bool _disposed;

    internal WholeReadSession(ArenaReservation reservation, bool adviseDontNeedOnDispose)
    {
        _reservation = reservation;
        _reservation.AcquireLease();
        _view = _reservation.OpenWholeView(adviseDontNeedOnDispose);
        _basePtr = _view.DataPtr;
        _size = _view.Size;
    }

    /// <summary>
    /// Materialise a fresh <see cref="IByteReader{TPin}"/> over the session's view, addressed
    /// in the reservation's own offset space (offset 0 = first byte). Pointer-backed so &gt;2 GiB
    /// reservations are addressable. No dispose check — the caller guarantees the session is alive
    /// (see the type remarks); this is the merge/scan hot path.
    /// </summary>
    public WholeReadSessionReader CreateReader() => new(_basePtr, _size);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view.Dispose();
        _reservation.Dispose();
    }
}

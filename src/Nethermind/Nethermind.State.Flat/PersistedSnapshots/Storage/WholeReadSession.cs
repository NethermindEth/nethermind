// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Io;

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// Scoped whole-buffer view over an <see cref="ArenaReservation"/>. Opens a fresh
/// per-reservation mmap view with <c>MADV_NORMAL</c> hint (distinct from the global
/// random-access view used by point queries) and acquires a lease on the reservation.
/// Disposing releases the lease; when <c>adviseDontNeedOnDispose</c> is <c>true</c> it
/// also issues <c>madvise(MADV_DONTNEED)</c> on the range and clears the matching
/// entries from the per-arena <see cref="PageResidencyTracker"/> — kernel-side and
/// tracker-side drops travel together so the tracker never holds ghost entries for
/// pages the kernel has already released.
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
    private readonly bool _adviseDontNeedOnDispose;
    private int _disposed;

    internal WholeReadSession(ArenaReservation reservation, bool adviseDontNeedOnDispose)
    {
        _reservation = reservation;
        _adviseDontNeedOnDispose = adviseDontNeedOnDispose;
        _reservation.AcquireLease();
        try
        {
            _view = _reservation.OpenWholeView(adviseDontNeedOnDispose);
        }
        catch
        {
            _reservation.Dispose(); // release the lease acquired above if the view could not be opened
            throw;
        }
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _view.Dispose();
        if (_adviseDontNeedOnDispose)
            _reservation.ForgetTracker();
        _reservation.Dispose();
    }
}

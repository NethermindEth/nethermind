// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// Scoped whole-buffer view over an <see cref="ArenaReservation"/>. Opens a fresh
/// per-reservation mmap view with <c>MADV_NORMAL</c> hint (distinct from the global
/// random-access view used by point queries) and acquires a lease on the reservation.
/// Disposing applies <c>MADV_DONTNEED</c> to the range and releases the lease.
/// </summary>
public sealed class WholeReadSession : IDisposable
{
    private readonly ArenaReservation _reservation;
    private readonly IArenaWholeView _view;
    private bool _disposed;

    internal WholeReadSession(ArenaReservation reservation)
    {
        _reservation = reservation;
        _reservation.AcquireLease();
        _view = _reservation.OpenWholeView();
    }

    public ReadOnlySpan<byte> GetSpan()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _view.GetSpan();
    }

    /// <summary>
    /// <see cref="IHsstByteReader{TPin}"/> over the session's view, addressed in the
    /// reservation's own offset space (offset 0 = first byte of the reservation).
    /// Pointer-backed so &gt;2 GiB reservations are addressable.
    /// </summary>
    public unsafe WholeReadSessionReader GetReader()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new WholeReadSessionReader(_view.DataPtr, _view.Size);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view.Dispose();
        _reservation.Dispose();
    }
}

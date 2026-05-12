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

    /// <summary>Total reservation size in bytes (long-typed, may exceed 2 GiB).</summary>
    public long Size => _view.Size;

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

    /// <summary>
    /// Raw view fields suitable for caching across an entire merge loop, then constructing
    /// <see cref="WholeReadSessionReader"/> instances on demand without re-paying the
    /// per-call dispose check. The returned pointer is owned by this session — the caller
    /// must ensure the session is not disposed while the cached fields are in use.
    /// </summary>
    public unsafe (IntPtr DataPtr, long Length) GetRawView()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ((IntPtr)_view.DataPtr, _view.Size);
    }

    /// <summary>
    /// Materialise the entire reservation as a single <see cref="ReadOnlySpan{Byte}"/>.
    /// <para>
    /// Span&lt;T&gt; is intrinsically int-bounded; this overload throws via a checked
    /// cast when the reservation exceeds <see cref="int.MaxValue"/>. Callers that
    /// must support &gt;2 GiB reservations should use <see cref="GetReader"/>
    /// (pointer-backed, long-bounded) instead and walk the data in int-sized chunks.
    /// </para>
    /// </summary>
    public unsafe ReadOnlySpan<byte> AsSpanIntBounded()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new ReadOnlySpan<byte>(_view.DataPtr, checked((int)_view.Size));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view.Dispose();
        _reservation.Dispose();
    }
}

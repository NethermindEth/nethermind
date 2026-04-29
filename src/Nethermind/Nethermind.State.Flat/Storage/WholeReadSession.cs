// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// Scoped whole-buffer view over an <see cref="ArenaReservation"/>. Acquires a lease in the
/// constructor; <see cref="Dispose"/> releases it. Use via
/// <c>using var session = reservation.BeginWholeReadSession();</c>; the span returned by
/// <see cref="GetSpan"/> stays valid for the session's lifetime.
/// </summary>
public sealed class WholeReadSession : IDisposable
{
    private readonly ArenaReservation _reservation;
    private bool _disposed;

    internal WholeReadSession(ArenaReservation reservation)
    {
        _reservation = reservation;
        _reservation.AcquireLease();
    }

    public ReadOnlySpan<byte> GetSpan()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _reservation.GetSpanInternal();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reservation.Dispose();
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.Network.Rlpx;

/// <summary>Tracks inbound packet completion for correlated cell responses.</summary>
public sealed class PacketReceiptTracker
{
    internal static PacketReceiptTracker Closed { get; } = new(true);
    private readonly bool _sentinel;
    private bool _active;
    private readonly Lock _lock = new();

    internal PacketReceiptTracker() { }

    private PacketReceiptTracker(bool sentinel)
    {
        _sentinel = sentinel;
        _closed = sentinel;
    }

    internal bool IsActive => Volatile.Read(ref _active);

    internal void Activate()
    {
        if (!_sentinel) Volatile.Write(ref _active, true);
    }
    private ITimestamper _clock = Timestamper.Default;
    private bool _clockBound;
    private bool _closed;
    private long _issued;
    private long _completed;
    private Task? _executorLifetime;
    private bool IsClosedLocked => _closed || _executorLifetime?.IsCompleted == true;

    internal bool IsClosed
    {
        get { lock (_lock) return IsClosedLocked; }
    }

    internal void BindExecutorLifetime(Task lifetime)
    {
        if (_sentinel) return;
        lock (_lock) _executorLifetime = lifetime;
    }

    internal void BindClock(ITimestamper clock)
    {
        if (_sentinel) return;
        lock (_lock)
        {
            if (_clockBound && !ReferenceEquals(_clock, clock))
                ThrowClockAlreadyBound();
            _clock = clock;
            _clockBound = true;
        }
    }

    internal void Stamp(ZeroPacket packet)
    {
        if (!IsActive) return;
        lock (_lock)
        {
            if (IsClosedLocked || _issued == long.MaxValue)
            {
                _closed = true;
                return;
            }
            packet.ReceiptSequence = ++_issued;
            packet.ReceivedAt = _clock.UtcNowOffset;
        }
    }

    internal long CaptureWatermark()
    {
        lock (_lock) return _issued;
    }

    internal ExpiryGuard CreateExpiryGuard() => new(this);

    internal sealed class ExpiryGuard(PacketReceiptTracker receipts)
    {
        private long? _watermark;

        internal bool CanExpire()
        {
            lock (receipts._lock)
            {
                _watermark ??= receipts._issued;
                return receipts.IsClosedLocked || receipts._completed >= _watermark.Value;
            }
        }
    }

    internal bool HasCompleted(long watermark)
    {
        lock (_lock) return IsClosedLocked || _completed >= watermark;
    }

    internal void Complete(long sequence)
    {
        if (sequence == 0) return;
        lock (_lock)
        {
            if (IsClosedLocked) return;
            if (_completed == long.MaxValue || sequence != _completed + 1)
                _closed = true;
            else
                _completed = sequence;
        }
    }

    internal void Close()
    {
        lock (_lock) _closed = true;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowClockAlreadyBound() =>
        throw new InvalidOperationException("The inbound receipt clock is already bound.");
}

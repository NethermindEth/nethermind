// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;

namespace Nethermind.TxPool;

/// <summary>
/// One peer's budget for the validation gas of frame transactions that fail admission.
/// </summary>
/// <remarks>
/// A token bucket refilled at <c>gasPerSecond</c> and holding at most <c>burstSeconds</c> of it. A caller reserves a
/// transaction's declared validation gas before validating it and refunds it when the transaction is admitted, so only
/// failing validation is charged. Once a peer has spent its budget, its next frame transactions are deferred
/// unvalidated. The unpaid work one peer can buy is then bounded in gas per second rather than in transactions per
/// second, so a higher MAX_VERIFY_GAS no longer multiplies it.
/// </remarks>
public sealed class PeerValidationGasBudget
{
    private readonly double _gasPerSecond;
    private readonly double _capacity;
    private readonly Func<long> _timestamp;
    private readonly Lock _lock = new();
    private double _tokens;
    private long _lastRefill;

    /// <param name="timestamp">Clock in <see cref="Stopwatch"/> ticks; the wall clock when omitted.</param>
    public PeerValidationGasBudget(ulong gasPerSecond, double burstSeconds, Func<long>? timestamp = null)
    {
        ArgumentOutOfRangeException.ThrowIfZero(gasPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(burstSeconds);
        _gasPerSecond = gasPerSecond;
        _capacity = gasPerSecond * burstSeconds;
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _tokens = _capacity;
        _lastRefill = _timestamp();
    }

    /// <summary>Reserves <paramref name="gas"/> if the peer still has it; otherwise leaves the budget untouched.</summary>
    /// <returns><see langword="false"/> when the transaction must be deferred without validation.</returns>
    public bool TryReserve(ulong gas)
    {
        lock (_lock)
        {
            Refill();
            if (_tokens < gas) return false;
            _tokens -= gas;
            return true;
        }
    }

    /// <summary>Returns a reservation whose transaction was admitted, so admitted validation is never charged.</summary>
    public void Refund(ulong gas)
    {
        lock (_lock)
        {
            _tokens = Math.Min(_capacity, _tokens + gas);
        }
    }

    private void Refill()
    {
        long now = _timestamp();
        double elapsedSeconds = (now - _lastRefill) / (double)Stopwatch.Frequency;
        _lastRefill = now;
        if (elapsedSeconds > 0) _tokens = Math.Min(_capacity, _tokens + elapsedSeconds * _gasPerSecond);
    }
}

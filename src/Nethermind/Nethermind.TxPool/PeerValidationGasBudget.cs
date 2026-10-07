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
/// A token bucket refilled at <c>gasPerSecond</c> and holding at most one second of it. A caller reserves a
/// transaction's declared validation gas before validating it and refunds it unless validation ran and failed, so only
/// failing validation is charged. Once a peer has spent its budget, its next frame transactions are dropped
/// unvalidated. The unpaid work one peer can buy is then bounded in gas per second rather than in transactions per
/// second, so a higher MAX_VERIFY_GAS no longer multiplies it.
/// </remarks>
public sealed class PeerValidationGasBudget
{
    private readonly double _gasPerSecond;
    private readonly ulong _capacity;
    private readonly ulong _maxVerifyGas;
    private readonly Func<long> _timestamp;
    private readonly Lock _lock = new();
    private double _tokens;
    private long _lastRefill;

    /// <param name="maxVerifyGas">The pool's <c>MAX_VERIFY_GAS</c>; a transaction above it is rejected before any
    /// validation runs, so it is never charged. <c>0</c> when the pool lifts the limit.</param>
    /// <param name="timestamp">Clock in <see cref="Stopwatch"/> ticks; the wall clock when omitted.</param>
    public PeerValidationGasBudget(ulong gasPerSecond, ulong maxVerifyGas, Func<long>? timestamp = null)
    {
        ArgumentOutOfRangeException.ThrowIfZero(gasPerSecond);
        _gasPerSecond = gasPerSecond;
        _capacity = gasPerSecond;
        _maxVerifyGas = maxVerifyGas;
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _tokens = _capacity;
        _lastRefill = _timestamp();
    }

    /// <summary>Reserves the charge for <paramref name="validationGas"/> if the peer still has it; otherwise leaves
    /// the budget untouched.</summary>
    /// <returns><see langword="false"/> when the transaction must be dropped without validation.</returns>
    public bool TryReserve(ulong validationGas)
    {
        ulong charge = Charge(validationGas);
        if (charge == 0) return true;

        lock (_lock)
        {
            Refill();
            if (_tokens < charge) return false;
            _tokens -= charge;
            return true;
        }
    }

    /// <summary>Returns a reservation whose transaction did not fail validation.</summary>
    public void Refund(ulong validationGas)
    {
        ulong charge = Charge(validationGas);
        if (charge == 0) return;

        lock (_lock)
        {
            _tokens = Math.Min(_capacity, _tokens + charge);
        }
    }

    // At most a full bucket, so a budget below one transaction's gas still admits one per refill instead of none.
    private ulong Charge(ulong validationGas) =>
        _maxVerifyGas != 0 && validationGas > _maxVerifyGas ? 0 : Math.Min(validationGas, _capacity);

    private void Refill()
    {
        long now = _timestamp();
        double elapsedSeconds = (now - _lastRefill) / (double)Stopwatch.Frequency;
        _lastRefill = now;
        if (elapsedSeconds > 0) _tokens = Math.Min(_capacity, _tokens + elapsedSeconds * _gasPerSecond);
    }
}

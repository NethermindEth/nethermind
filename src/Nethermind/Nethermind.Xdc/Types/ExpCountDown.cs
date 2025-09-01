// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Consensus.HotStuff.Types;

public class ExpCountDown : IDisposable, IExpCountDown
{
    private int _initialDuration;
    private int _base;
    private int _maxExponent;
    private int _currentExponent;
    private Timer? _timer;
    private Action? _callback;

    public ExpCountDown(int initialDuration, int @base, int maxExponent)
        => SetParams(initialDuration, @base, maxExponent, false);

    public void SetParams(int initialDuration, int @base, int maxExponent, bool shouldScheduleNext = true)
    {
        if (@base <= 1) throw new ArgumentException("Base must be > 1", nameof(@base));
        if (maxExponent < 0) throw new ArgumentException("MaxExponent must be >= 0", nameof(maxExponent));

        _initialDuration = initialDuration;
        _base = @base;
        _maxExponent = maxExponent;
        _currentExponent = 0;

        if (_callback != null && shouldScheduleNext)
        {
            ScheduleNext();
        }
    }

    /// <summary>
    /// Computes next timeout duration with exponential backoff.
    /// </summary>
    private int NextTimeout()
    {
        int exp = Math.Min(_currentExponent, _maxExponent);
        var timeout = _initialDuration * (int)Math.Pow(_base, exp);
        _currentExponent++;
        return timeout;
    }

    /// <summary>
    /// Start countdown and fire callback after timeout.
    /// </summary>
    public void Start(Action callback)
    {
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        ScheduleNext();
    }

    private void ScheduleNext()
    {
        var timeout = NextTimeout();
        _timer?.Dispose();
        _timer = new Timer(_ => _callback?.Invoke(), null, timeout, uint.MaxValue);
    }

    /// <summary>
    /// Restart countdown from initial duration (e.g. on progress).
    /// </summary>
    public void Reset()
    {
        _currentExponent = 0;
        if (_callback != null)
        {
            ScheduleNext();
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery;

internal sealed class FixedWindowLimiter(int maxCount, TimeSpan window, TimeProvider? timeProvider = null)
{
    private readonly Lock _lock = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private long _windowStart = (timeProvider ?? TimeProvider.System).GetTimestamp();
    private int _count;

    internal bool TryAcquire()
    {
        lock (_lock)
        {
            long now = _timeProvider.GetTimestamp();
            if (_timeProvider.GetElapsedTime(_windowStart, now) >= window)
            {
                _windowStart = now;
                _count = 0;
            }

            if (_count >= maxCount) return false;
            _count++;
            return true;
        }
    }
}

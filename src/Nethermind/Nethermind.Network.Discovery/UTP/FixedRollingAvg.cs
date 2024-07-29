// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery;

public class FixedRollingAvg
{
    private int _size = 0;
    private int _tailIdx = 0;

    private int[] _rollingWindow;
    private uint[] _rollingWindowSetTime;
    private int _sum;
    private readonly int _expiry;
    private readonly int _defaultValue;
    private readonly int _capacity;

    private int HeadIdx => (_tailIdx + _size) % _capacity;

    public FixedRollingAvg(int capacity, int defaultValue, int expiry)
    {
        _rollingWindow = new int[capacity + 1];
        _rollingWindowSetTime = new uint[capacity + 1];
        _defaultValue = defaultValue;
        _expiry = expiry;
        _capacity = capacity;
    }

    public int GetAvgFixed16Precision(uint now)
    {
        while (_size > 0)
        {
            // Not expired. We assume that later observation is always for later time.
            // TODO: make overflow safe
            if (_rollingWindowSetTime[(_tailIdx + 1) % _capacity] + _expiry > now) break;

            _tailIdx++;
            _tailIdx%=_capacity;
            _size--;
            _sum -= _rollingWindow[_tailIdx];
        }

        if (_size == 0) return _defaultValue << 16;
        return (_sum << 16) / _size;
    }

    public int GetAvg(uint now)
    {
        return GetAvgFixed16Precision(now) >> 16;
    }

    public void Observe(int delay, uint now)
    {
        if (_size == _capacity) // Full
        {
            _tailIdx++;
            _tailIdx%=_capacity;
            _sum -= _rollingWindow[_tailIdx];
            _size--;
        }

        _size++;
        var headIdx = HeadIdx;
        _rollingWindow[headIdx] = delay;
        _rollingWindowSetTime[headIdx] = now;
        _sum += _rollingWindow[headIdx];
    }

    public void AdjustMin(int delayMicros, uint now)
    {
        var headIdx = HeadIdx;
        int updateTo = Math.Min(delayMicros, _rollingWindow[headIdx]);
        AdjustCurrent(updateTo, now);
    }

    private void AdjustCurrent(int updateTo, uint now)
    {
        var headIdx = HeadIdx;
        _sum -= _rollingWindow[headIdx];
        _rollingWindow[headIdx] = updateTo;
        _rollingWindowSetTime[headIdx] = now;
        _sum += updateTo;
    }
}

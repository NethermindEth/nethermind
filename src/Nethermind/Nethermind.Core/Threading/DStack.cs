// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Threading;

/// <summary>
/// Like a concurrent stack, but also support dequeue.
/// It will not reduce buffer size on dequeue/pop.
/// The dequeue is expected to not be called often and may return false and not return item if blocked.
/// </summary>
/// <param name="initialCapacity"></param>
/// <typeparam name="T"></typeparam>
public class DStack<T>(int initialCapacity)
{
    private readonly McsLock _locker = new McsLock(); // TODO: Benchmark if plain spinlock is faster

    private int _startIdx = -1;
    private int _endIdx = -1;
    private T[] _buffer = new T[Math.Max(initialCapacity, 1)];

    public int Count
    {
        get
        {
            using var _ = _locker.Acquire();
            return _endIdx - _startIdx;
        }
    }

    public bool TryPop(out T? item)
    {
        using var _ = _locker.Acquire();

        if (_endIdx == _startIdx)
        {
            item = default;
            return false;
        }

        item = _buffer[_endIdx];
        _buffer[_endIdx] = default!;
        _endIdx--;

        if (_endIdx == _startIdx)
        {
            // Reset the startidx to start of the buffer.
            _startIdx = -1;
            _endIdx = -1;
        }
        return true;
    }

    public void Push(T item)
    {
        using var _ = _locker.Acquire();

        int newEndIdx = _endIdx + 1;
        if (newEndIdx == _buffer.Length)
        {
            T[] newItems = new T[_buffer.Length * 2];
            int count = _endIdx - _startIdx;
            Array.Copy(_buffer, (_startIdx + 1), newItems, 0, count);
            _buffer = newItems;
            _startIdx = -1;
            newEndIdx = count;
        }

        _buffer[newEndIdx] = item;
        _endIdx = newEndIdx;
    }

    public bool TryDequeue(out T? item)
    {
        // Is there a `TryAcquire`?
        using var _ = _locker.Acquire();

        if (_endIdx == _startIdx)
        {
            item = default;
            return false;
        }

        item = _buffer[_startIdx + 1];
        _buffer[_startIdx + 1] = default!;
        _startIdx++;
        return true;
    }
}

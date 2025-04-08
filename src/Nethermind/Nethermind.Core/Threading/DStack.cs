// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;

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
    private SpinLock _locker = new SpinLock(); // TODO: Benchmark if other lock is faster

    private int _startIdx = -1;
    private int _endIdx = -1;
    private T[] _buffer = new T[Math.Max(initialCapacity, 1)];

    public int Count
    {
        get
        {
            bool lockTaken = false;
            _locker.Enter(ref lockTaken);
            Debug.Assert(lockTaken);

            var res = _endIdx - _startIdx;
            _locker.Exit();
            return res;
        }
    }

    public bool TryPop(out T? item)
    {
        bool lockTaken = false;
        _locker.Enter(ref lockTaken);
        Debug.Assert(lockTaken);

        try
        {
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
        finally
        {
            _locker.Exit();
        }
    }

    public void Push(T item)
    {
        bool lockTaken = false;
        _locker.Enter(ref lockTaken);
        Debug.Assert(lockTaken);

        try
        {
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
        finally
        {
            _locker.Exit();
        }
    }

    public bool TryDequeue(out T? item)
    {
        bool lockTaken = false;
        _locker.TryEnter(ref lockTaken);
        if (!lockTaken)
        {
            item = default;
            return false;
        }

        try
        {
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
        finally
        {
            _locker.Exit();
        }
    }
}

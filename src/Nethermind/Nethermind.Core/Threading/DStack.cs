// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Collections;

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
    private McsLock _locker = new McsLock();

    private long _atomicEndAndStart;
    private T[] _buffer = new T[Math.Max(initialCapacity, 1)];

    public int Count
    {
        get
        {
            (int startIdx, int endIdx) = GetStartAndEndIdx();
            return endIdx - startIdx;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool TryPop(out T? item)
    {
        using var _ = _locker.Acquire();

        (int startIdx, int endIdx) = GetStartAndEndIdx();
        if (endIdx == startIdx)
        {
            item = default;
            return false;
        }

        item = _buffer[endIdx];
        _buffer[endIdx] = default!;
        endIdx--;

        if (endIdx == startIdx)
        {
            // Reset the startidx to start of the buffer.
            startIdx = -1;
            endIdx = -1;
        }

        SetStartAndEndIdxUnlocked(startIdx, endIdx);
        return true;
    }

    private (int startIdx, int endIdx) GetStartAndEndIdx()
    {
        long atomicValue = Interlocked.Read(ref _atomicEndAndStart);
        int startIdx = (int)(atomicValue >> 32);
        int endIdx = (int)(atomicValue & 0xFFFFFFFFL);
        return (startIdx, endIdx);
    }

    private void SetStartAndEndIdxUnlocked(int startIdx, int endIdx)
    {
        long startAsLong = startIdx;
        long endAsLong = endIdx;
        long atomicValue = (startAsLong << 32) | endAsLong;
        Interlocked.Exchange(ref _atomicEndAndStart, atomicValue);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Push(T item)
    {
        using var _ = _locker.Acquire();

        PushUnlock(item);
    }

    private void PushUnlock(T item)
    {
        (int startIdx, int endIdx) = GetStartAndEndIdx();

        int newEndIdx = endIdx + 1;
        if (newEndIdx == _buffer.Length)
        {
            T[] newItems = new T[_buffer.Length * 2];
            int count = endIdx - startIdx;
            Array.Copy(_buffer, (startIdx + 1), newItems, 0, count);
            _buffer = newItems;
            startIdx = -1;
            newEndIdx = count;
        }

        _buffer[newEndIdx] = item;
        SetStartAndEndIdxUnlocked(startIdx, newEndIdx);
    }

    public void PushMany(Span<T> items)
    {
        using var _ = _locker.Acquire();

        foreach (var item in items)
        {
            PushUnlock(item);
        }
    }

    public bool TryDequeue(out T? item, out bool shouldRetry)
    {
        item = default;
        shouldRetry = false;

        (int startIdx, int endIdx) = GetStartAndEndIdx();
        if (endIdx == startIdx)
        {
            return false;
        }

        if (!_locker.TryAcquire(out McsLock.Disposable disposable))
        {
            shouldRetry = true;
            return false;
        }

        try
        {
            (startIdx, endIdx) = GetStartAndEndIdx();
            if (endIdx == startIdx)
            {
                return false;
            }

            item = _buffer[startIdx + 1];
            _buffer[startIdx + 1] = default!;
            SetStartAndEndIdxUnlocked(startIdx+1, endIdx);
            return true;
        }
        finally
        {
            disposable.Dispose();
        }
    }
}

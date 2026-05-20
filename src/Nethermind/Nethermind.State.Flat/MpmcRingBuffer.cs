// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.State.Flat;

/// <summary>
/// Multiple consumer variant of <see cref="SpmcRingBuffer{T}"/>.
/// Slightly slower in the enqueue due to the need of interlocked operation on the tail.
/// </summary>
/// <typeparam name="T"></typeparam>
public sealed class MpmcRingBuffer<T>
{
    private readonly T[] _entries;
    private readonly long[] _sequences;
    private readonly int _mask;
    private readonly int _capacity;

    public long EstimatedJobCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            long tail = Volatile.Read(ref _tail);
            long head = Volatile.Read(ref _head);

            long count = tail - head;
            return count < 0 ? 0 : count; // clamp just in case of a race
        }
    }

#pragma warning disable CS0169 // Field is never used
    // --- head (consumers) + padding ---
    private long _head;
    private long _p1, _p2, _p3, _p4, _p5, _p6, _p7;

    // --- tail (producers) + padding ---
    private long _tail;
    private long _p8, _p9, _p10, _p11, _p12, _p13, _p14;
#pragma warning restore CS0169 // Field is never used

    public MpmcRingBuffer(int capacityPowerOfTwo)
    {
        if (capacityPowerOfTwo <= 0 || (capacityPowerOfTwo & (capacityPowerOfTwo - 1)) != 0)
            throw new ArgumentException("Capacity must be power of two.");

        _capacity = capacityPowerOfTwo;
        _mask = capacityPowerOfTwo - 1;
        _entries = new T[capacityPowerOfTwo];
        _sequences = new long[capacityPowerOfTwo];

        for (int i = 0; i < capacityPowerOfTwo; i++)
            _sequences[i] = i;
    }

    /// <summary>
    /// Multiple producer variant of <see cref="SpmcRingBuffer{T}.TryEnqueue"/>.
    /// </summary>
    /// <param name="item"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(in T item)
    {
        while (true)
        {
            long tail = Volatile.Read(ref _tail);
            int index = (int)(tail & _mask);
            long seq = Volatile.Read(ref _sequences[index]);

            if (seq == tail)
            {
                // Interlocked exchange for multiple producer.
                if (Interlocked.CompareExchange(ref _tail, tail + 1, tail) == tail)
                {
                    // Success
                    _entries[index] = item;

                    // Mark as ready for consumer (tail + 1)
                    Volatile.Write(ref _sequences[index], tail + 1);
                    return true;
                }
            }
            else if (seq < tail)
            {
                // Slot hasn't been consumed yet from the previous lap
                return false;
            }

            // If seq > tail, another producer won the race; loop and retry
            Thread.SpinWait(1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out T item)
    {
        while (true)
        {
            long head = Volatile.Read(ref _head);
            int index = (int)(head & _mask);
            long seq = Volatile.Read(ref _sequences[index]);
            long expectedSeq = head + 1;

            // If seq == expectedSeq, the producer has finished writing
            if (seq == expectedSeq)
            {
                if (Interlocked.CompareExchange(ref _head, head + 1, head) == head)
                {
                    item = _entries[index];
                    // Mark as ready for the producer's next lap (head + capacity)
                    Volatile.Write(ref _sequences[index], head + _capacity);
                    return true;
                }
            }
            else if (seq < expectedSeq)
            {
                // Producer hasn't filled this slot yet
                item = default!;
                return false;
            }

            // If seq > expectedSeq, another consumer won the race; loop and retry
            Thread.SpinWait(1);
        }
    }
}

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.State.Flat;

/// <summary>
/// AI generated single producer multiple consumer ring buffer. If called by multiple producer, it will hang.
/// See <see cref="MpmcRingBuffer{T}"/> for multiple producer variant.
/// The selection of <see cref="T"/> is important. Is should be ideally a struct of size no more than 64 byte
/// or 32 byte if possible.
/// </summary>
/// <typeparam name="T"></typeparam>
public sealed class SpmcRingBuffer<T>
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

    // --- head (consumers) + padding to avoid false sharing with _tail ---
    private long _head;
#pragma warning disable CS0169 // Field is never used
    private long _headPad1, _headPad2, _headPad3, _headPad4, _headPad5, _headPad6, _headPad7;

    // --- tail (producer) + padding ---
    private long _tail;
    private long _tailPad1, _tailPad2, _tailPad3, _tailPad4, _tailPad5, _tailPad6, _tailPad7;
#pragma warning restore CS0169 // Field is never used

    public SpmcRingBuffer(int capacityPowerOfTwo)
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
    /// Single producer: enqueue one item if there is space.
    /// Returns false if the ring is full.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(in T item)
    {
        // Single producer: no CAS needed on tail.
        long tail = _tail;
        int index = (int)(tail & _mask);

        // Slot is free only if its sequence equals the current tail.
        long seq = Volatile.Read(ref _sequences[index]);
        if (seq != tail)
            return false; // not yet consumed -> buffer full

        _entries[index] = item;

        // Publish:
        // seq = tail + 1 means "item for head == tail is now visible".
        // Volatile.Write gives us the release fence so consumer
        // sees the payload after seeing seq.
        Volatile.Write(ref _sequences[index], tail + 1);

        _tail = tail + 1;

        return true;
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

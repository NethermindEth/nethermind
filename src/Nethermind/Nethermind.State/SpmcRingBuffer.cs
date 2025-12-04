// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.State;

/// <summary>
/// ChatGPT generated single producer multiple consumer ring buffer
/// TODO: Check if using full fledge LMAX Distruptor is worth it.
/// </summary>
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
            // Single producer only ever increments _tail
            long tail = Volatile.Read(ref _tail);

            // Multiple consumers only ever increment _head
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
        if (capacityPowerOfTwo <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacityPowerOfTwo));

        // must be a power of two
        if ((capacityPowerOfTwo & (capacityPowerOfTwo - 1)) != 0)
            throw new ArgumentException("Capacity must be power of two.", nameof(capacityPowerOfTwo));

        _capacity = capacityPowerOfTwo;
        _mask = capacityPowerOfTwo - 1;
        _entries = new T[capacityPowerOfTwo];
        _sequences = new long[capacityPowerOfTwo];

        // LMAX / Vyukov-style:
        // initial state: seq[i] = i, head = 0, tail = 0
        // - producer at tail = 0 expects seq[0] == 0 to claim
        // - after publishing item 0: seq[0] = 1 (head+1)
        for (int i = 0; i < capacityPowerOfTwo; i++)
            _sequences[i] = i;
    }

    /// <summary>
    /// Approximate number of items in the buffer.
    /// </summary>
    public long EstimatedCount =>
        Volatile.Read(ref _tail) - Volatile.Read(ref _head);

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

        // Write payload first.
        _entries[index] = item;

        // Publish:
        // seq = tail + 1 means "item for head == tail is now visible".
        // Volatile.Write gives us the release fence so consumer
        // sees the payload after seeing seq.
        Volatile.Write(ref _sequences[index], tail + 1);

        // Advance tail (only producer touches this).
        _tail = tail + 1;

        return true;
    }

    /// <summary>
    /// Multiple consumers: try to dequeue one item.
    /// Returns false if buffer appears empty at time of check.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out T item)
    {
        while (true)
        {
            long head = Volatile.Read(ref _head);
            int index = (int)(head & _mask);

            long seq = Volatile.Read(ref _sequences[index]);
            long expectedSeq = head + 1;

            // Not yet published?
            if (seq < expectedSeq)
            {
                item = default!;
                return false;
            }

            // Slot is ready for this consumer, try to claim head.
            if (seq == expectedSeq &&
                Interlocked.CompareExchange(ref _head, head + 1, head) == head)
            {
                // We own this slot now.
                item = _entries[index];

                // Mark slot as free for the next wrap:
                // when tail reaches head + _capacity, this slot
                // will again have seq == tail.
                Volatile.Write(
                    ref _sequences[index],
                    head + _capacity
                );

                return true;
            }

            // Lost race to another consumer, retry with updated head.
        }
    }
}

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.State;

/// <summary>
/// ChatGPT generated single producer multiple consumer ring buffer
/// TODO: Check if using full fledge LMAX Distruptor is worth it.
/// </summary>
/// <typeparam name="T"></typeparam>
public sealed class SpmcRingBuffer<T>
{
    private readonly T[] _entries;
    private readonly long[] _sequences;
    private readonly int _mask;
    private readonly int _capacity;

    private long _head; // consumers increment this
    private long _tail; // producer increments this

    public SpmcRingBuffer(int capacityPowerOfTwo)
    {
        if ((capacityPowerOfTwo & (capacityPowerOfTwo - 1)) != 0)
            throw new ArgumentException("Capacity must be power of two.");

        _capacity = capacityPowerOfTwo;
        _mask = capacityPowerOfTwo - 1;
        _entries = new T[capacityPowerOfTwo];
        _sequences = new long[capacityPowerOfTwo];

        // initialize sequence numbers so each slot appears available
        for (int i = 0; i < capacityPowerOfTwo; i++)
            _sequences[i] = i;
    }

    public long EstimatedJobCount => _tail - _head;

    public bool TryEnqueue(T item)
    {
        if (TryClaim(out var slot))
        {
            this[slot] = item;
            Publish(slot);
            return true;
        }

        return false;
    }

    public bool TryClaim(out int slot)
    {
        long tail = _tail;
        int index = (int)(tail & _mask);

        // check slot availability
        if (Volatile.Read(ref _sequences[index]) != tail)
        {
            slot = -1;
            return false; // not ready (buffer full)
        }

        _tail = tail + 1; // claim
        slot = index;
        return true;
    }

    public void Publish(int slot)
    {
        long publishSeq = Volatile.Read(ref _tail);
        Volatile.Write(ref _sequences[slot], publishSeq);
    }

    public bool TryDequeue(out T item)
    {
        while (true)
        {
            long head = Volatile.Read(ref _head);
            int index = (int)(head & _mask);
            long seq = Volatile.Read(ref _sequences[index]);

            if (seq <= head)
            {
                item = default!;
                return false; // not yet published
            }

            if (Interlocked.CompareExchange(ref _head, head + 1, head) == head)
            {
                item = _entries[index];
                // mark slot as available for producer again
                Volatile.Write(ref _sequences[index], head + _capacity);
                return true;
            }
        }
    }

    public ref T this[int slot] => ref _entries[slot];
}

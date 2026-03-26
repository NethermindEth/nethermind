// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;

namespace Nethermind.State.Flat;

/// <summary>
/// A preallocated pool of fixed-size (532 byte) byte arrays for trie node RLP encoding during commit.
/// Uses atomic index increment for lock-free rent. Returns are no-ops — arrays are reused after <see cref="Reset"/>.
/// Lives at the scope provider level and is reused across blocks.
/// </summary>
public sealed class PreallocatedCappedArrayPool(int initialCapacity = 1024) : ICappedArrayPool
{
    private const int BufferSize = 532;

    private byte[][] _buffers = CreateBuffers(initialCapacity);
    private int _index;

    public CappedArray<byte> Rent(int size)
    {
        if (size == 0) return CappedArray<byte>.Empty;
        if (size > BufferSize) throw new ArgumentException($"Requested size {size} exceeds preallocated buffer size {BufferSize}");

        int idx = Interlocked.Increment(ref _index) - 1;
        byte[][] buffers = _buffers;
        byte[] buffer;
        if ((uint)idx < (uint)buffers.Length)
        {
            buffer = buffers[idx];
            buffer.AsSpan(0, size).Clear();
        }
        else
        {
            // Pool exhausted — allocate a fresh array. Reset() will expand for next block.
            buffer = new byte[size];
        }

        return new CappedArray<byte>(buffer, size);
    }

    public void Return(in CappedArray<byte> buffer) { }

    /// <summary>
    /// Resets the pool for reuse. Expands if the previous block used more buffers than allocated.
    /// </summary>
    public void Reset()
    {
        int used = Volatile.Read(ref _index);
        if (used > _buffers.Length)
        {
            int newCapacity = used + (used >> 1);
            byte[][] newBuffers = new byte[newCapacity][];
            Array.Copy(_buffers, newBuffers, _buffers.Length);
            for (int i = _buffers.Length; i < newCapacity; i++)
                newBuffers[i] = new byte[BufferSize];
            _buffers = newBuffers;
        }

        _index = 0;
    }

    private static byte[][] CreateBuffers(int capacity)
    {
        byte[][] buffers = new byte[capacity][];
        for (int i = 0; i < capacity; i++)
            buffers[i] = new byte[BufferSize];
        return buffers;
    }
}

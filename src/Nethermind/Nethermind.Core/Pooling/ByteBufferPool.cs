// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Nethermind.Core.Pooling;

public class ByteBufferPool
{
    public const int BlobSize = 131072;
    public const int ProofSize = 48;
    private const int _poolSize = 256;

    private readonly static ByteBufferPool _blobPool = new(BlobSize, _poolSize); // 32 MiB
    private readonly static ByteBufferPool _proofPool = new(ProofSize, _poolSize * 2); // 24 KiB

    public static byte[] RentBlob() => _blobPool.Rent();
    public static void PoolBlobs(byte[][] bytesArrays)
    {
        foreach (var array in bytesArrays)
        {
            if (!_blobPool.Return(array)) break;
        }
    }
    public static byte[] RentProof() => _proofPool.Rent();
    public static void PoolProofs(byte[][] bytesArrays)
    {
        foreach (var array in bytesArrays)
        {
            if (!_proofPool.Return(array)) break;
        }
    }

    private readonly int _bufferSize;
    private readonly int _maxPoolSize;
    private readonly ConcurrentQueue<byte[]> _pool;
    private int _count;

    public ByteBufferPool(int bufferSize, int maxPoolSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPoolSize);

        _bufferSize = bufferSize;
        _maxPoolSize = maxPoolSize;
        _pool = new ConcurrentQueue<byte[]>();
        for (var i = 0; i < maxPoolSize; i++)
        {
            _pool.Enqueue(new byte[bufferSize]);
        }
        _count = maxPoolSize;
    }

    /// <summary>
    /// Rent a buffer: either from the pool or a new one if the pool is empty.
    /// </summary>
    public byte[] Rent()
    {
        if (_pool.TryDequeue(out var buffer))
        {
            Interlocked.Decrement(ref _count);
            return buffer;
        }

        // Pool empty -> allocate new
        return new byte[_bufferSize];
    }

    /// <summary>
    /// Return a buffer to the pool if it matches the size and there's room.
    /// Otherwise it's simply discarded (GC will collect it eventually).
    /// </summary>
    public bool Return(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        // Drop mismatched sizes
        if (buffer.Length != _bufferSize)
            return true;

        // Only enqueue if we haven't reached the cap
        // increment count first, then check against max to avoid races
        var newCount = Interlocked.Increment(ref _count);
        if (newCount <= _maxPoolSize)
        {
            _pool.Enqueue(buffer);
            return true;
        }
        else
        {
            // We went over the cap: decrement back and drop
            Interlocked.Decrement(ref _count);
            return false;
        }
    }
}

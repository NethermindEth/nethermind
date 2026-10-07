// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Threading;
using Microsoft.AspNetCore.Connections;

namespace Nethermind.Runner.JsonRpc;

/// <summary>Gives Kestrel memory pools of <see cref="TransportMemoryPool.BlockSize"/> blocks instead of its 4 KiB ones.</summary>
/// <remarks>
/// The socket transport receives into one pool block at a time, so the block size caps a single receive. With 4 KiB
/// blocks an engine_newPayload body of a few hundred KiB arrives as some eighty receives, each a pipe flush that wakes
/// the request again, and reading it lags the wire by a couple of hundred microseconds; with 64 KiB blocks it takes a
/// handful. Kestrel asks this factory for every pool it creates (one per socket I/O queue), so all its connections,
/// requests and responses alike, buffer in these blocks.
/// </remarks>
internal sealed class TransportMemoryPoolFactory : IMemoryPoolFactory<byte>
{
    private int _poolsCreated;

    /// <summary>Pools handed out so far.</summary>
    internal int PoolsCreated => Volatile.Read(ref _poolsCreated);

    public MemoryPool<byte> Create(MemoryPoolOptions? options = null)
    {
        Interlocked.Increment(ref _poolsCreated);
        return new TransportMemoryPool();
    }
}

/// <summary>A pool of pinned, uninitialised <see cref="BlockSize"/> blocks, as Kestrel's own pool hands out 4 KiB ones.</summary>
/// <remarks>
/// Keeps at most <see cref="MaxRetainedBlocks"/> returned blocks for reuse and drops the rest to the GC, so a burst of
/// connections does not pin its peak for the life of the process. A request larger than a block gets an unpooled
/// array of its own; the pipes Kestrel builds on a pool never ask for more than <see cref="MaxBufferSize"/>.
/// </remarks>
internal sealed class TransportMemoryPool : MemoryPool<byte>
{
    public const int BlockSize = 64 * 1024;
    public const int MaxRetainedBlocks = 32;

    private readonly ConcurrentQueue<byte[]> _free = new();
    private int _freeCount;
    private volatile bool _disposed;

    public override int MaxBufferSize => BlockSize;

    /// <summary>Returned blocks kept for reuse.</summary>
    internal int RetainedBlocks => Volatile.Read(ref _freeCount);

    public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (minBufferSize > BlockSize)
        {
            return new Block(null, GC.AllocateUninitializedArray<byte>(minBufferSize, pinned: true));
        }

        if (_free.TryDequeue(out byte[]? array))
        {
            Interlocked.Decrement(ref _freeCount);
        }
        else
        {
            array = GC.AllocateUninitializedArray<byte>(BlockSize, pinned: true);
        }

        return new Block(this, array);
    }

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        while (_free.TryDequeue(out _)) Interlocked.Decrement(ref _freeCount);
    }

    private void Return(byte[] array)
    {
        if (_disposed) return;
        if (Interlocked.Increment(ref _freeCount) > MaxRetainedBlocks)
        {
            Interlocked.Decrement(ref _freeCount);
            return;
        }

        _free.Enqueue(array);
    }

    private sealed class Block(TransportMemoryPool? pool, byte[] array) : IMemoryOwner<byte>
    {
        private int _returned;

        public Memory<byte> Memory => array;

        public void Dispose()
        {
            // Once only: a second return would hand the same array to two owners.
            if (Interlocked.Exchange(ref _returned, 1) == 0) pool?.Return(array);
        }
    }
}

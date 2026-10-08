// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Microsoft.AspNetCore.Connections;

namespace Nethermind.Runner.JsonRpc;

/// <summary>Gives Kestrel memory pools that hand out <see cref="TransportMemoryPool.BlockSize"/> blocks instead of its 4 KiB ones.</summary>
/// <remarks>
/// <para>
/// The socket transport receives into one pool block at a time, so the block size caps a single receive. With 4 KiB
/// blocks an engine_newPayload body of a few hundred KiB arrives as some eighty receives, each a pipe flush that wakes
/// the request again, and reading it lags the wire by a couple of hundred microseconds; with 64 KiB blocks it takes a
/// handful.
/// </para>
/// <para>
/// Kestrel asks this factory for every pool it creates, one per socket I/O queue of every endpoint, and names none of
/// them apart (<see cref="MemoryPoolOptions.Owner"/> is "kestrel" for all), so the engine and the public JSON-RPC
/// endpoints alike buffer their requests and responses in these blocks. The pools of one endpoint serve only that
/// endpoint's connections, so the large blocks each pool may hand out are never taken by traffic on another port.
/// </para>
/// <para>
/// Kestrel trims its own pools from its heartbeat, which it does not offer to a replacement factory, so this one runs
/// a single timer of its own every <see cref="TransportMemoryPool.TrimInterval"/> while it has live pools, and stops it
/// when the last pool is disposed.
/// </para>
/// </remarks>
internal sealed class TransportMemoryPoolFactory : IMemoryPoolFactory<byte>, IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<TransportMemoryPool, byte> _pools = new();
    private readonly Lock _lock = new();
    private ITimer? _timer;
    private bool _disposed;
    private int _poolsCreated;

    public TransportMemoryPoolFactory() : this(TimeProvider.System) { }

    internal TransportMemoryPoolFactory(TimeProvider timeProvider) => _timeProvider = timeProvider;

    /// <summary>Pools handed out so far.</summary>
    internal int PoolsCreated => Volatile.Read(ref _poolsCreated);

    /// <summary>Pools handed out and not yet disposed.</summary>
    internal int LivePools => _pools.Count;

    /// <summary>The pools handed out and not yet disposed.</summary>
    internal List<TransportMemoryPool> Pools
    {
        get
        {
            List<TransportMemoryPool> pools = new(_pools.Count);
            foreach (KeyValuePair<TransportMemoryPool, byte> pool in _pools) pools.Add(pool.Key);
            return pools;
        }
    }

    /// <summary>Whether the trim timer is running.</summary>
    internal bool IsTrimming
    {
        get
        {
            lock (_lock) return _timer is not null;
        }
    }

    public MemoryPool<byte> Create(MemoryPoolOptions? options = null)
    {
        Interlocked.Increment(ref _poolsCreated);
        TransportMemoryPool pool = new(this);
        lock (_lock)
        {
            if (!_disposed)
            {
                _pools.TryAdd(pool, 0);
                _timer ??= _timeProvider.CreateTimer(
                    static state => ((TransportMemoryPoolFactory)state!).TrimPools(),
                    this,
                    TransportMemoryPool.TrimInterval,
                    TransportMemoryPool.TrimInterval);
            }
        }

        return pool;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _pools.Clear();
            StopTimer();
        }
    }

    internal void OnPoolDisposed(TransportMemoryPool pool)
    {
        lock (_lock)
        {
            if (_pools.TryRemove(pool, out _) && _pools.IsEmpty) StopTimer();
        }
    }

    private void TrimPools()
    {
        foreach (KeyValuePair<TransportMemoryPool, byte> pool in _pools) pool.Key.Trim();
    }

    private void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }
}

/// <summary>
/// A pool of pinned, uninitialised blocks: <see cref="BlockSize"/> ones while few are out, Kestrel's own
/// <see cref="SmallBlockSize"/> ones beyond that.
/// </summary>
/// <remarks>
/// <para>
/// Every rent costs a whole block, however little of it the caller fills, and a burst of small messages, such as a
/// notification fanned out to every WebSocket subscriber, can have thousands out at once. So a pool hands out at most
/// <see cref="MaxLargeBlocks"/> large blocks, a bound on its large-block memory that does not grow with the number of
/// clients, and serves rents beyond them from small blocks, which cost what they cost on Kestrel's own pool. Kestrel's
/// pipes ask for at least a small block and take any larger one, so either size serves them. The engine endpoint has
/// pools of its own and only the consensus client's few connections, which stay well within the large blocks even
/// while a payload body fills Kestrel's request buffer.
/// </para>
/// <para>
/// Each size keeps its returned blocks and releases them only when recent demand no longer needs them, the way
/// Kestrel's own pool does, so under steady load a pool stops allocating; pinned blocks dropped while still wanted
/// would only come back as fresh pinned allocations that just gen2 reclaims. Demand is the most blocks of that size
/// out at once over the last <see cref="DemandWindows"/> calls of <see cref="Trim"/>, a minute at the factory's
/// <see cref="TrimInterval"/>. Each call keeps enough blocks to meet that peak again and releases at most a twentieth
/// of the rest, but at least <see cref="MinReleasePerTrim"/>, which is the rate Kestrel's pool sheds blocks when idle;
/// a pool that falls quiet ends with <see cref="MinRetainedBlocks"/> of each size. The minute matters for the engine
/// endpoint: it needs its blocks for well under a millisecond once a slot, and Kestrel's ten-second idle rule would
/// release them between slots and allocate them again for the next payload.
/// </para>
/// <para>
/// A rent no block can serve, larger than a large block or larger than a small one once the large blocks are all out,
/// gets an ordinary array of its own. Kestrel's pipes ask for more than a small block only when a writer wants a
/// bigger span, which is rare, and a short-lived array is cheapest left to gen0; the socket pins it only while an
/// operation runs.
/// </para>
/// </remarks>
internal sealed class TransportMemoryPool : MemoryPool<byte>
{
    public const int BlockSize = 64 * 1024;

    /// <summary>The size of a small block, Kestrel's own block size.</summary>
    public const int SmallBlockSize = 4 * 1024;

    /// <summary>The most large blocks a pool has at once, out or retained.</summary>
    public const int MaxLargeBlocks = 32;

    /// <summary>Blocks of each size an idle pool keeps, enough for one connection to receive and respond without allocating.</summary>
    public const int MinRetainedBlocks = 2;

    /// <summary>Calls of <see cref="Trim"/> whose peaks make up the demand a pool keeps blocks for.</summary>
    public const int DemandWindows = 6;

    /// <summary>The fewest surplus blocks of a size one <see cref="Trim"/> releases, unless fewer are surplus.</summary>
    public const int MinReleasePerTrim = 10;

    /// <summary>How often the factory trims its pools; Kestrel's pool uses the same delay.</summary>
    public static readonly TimeSpan TrimInterval = TimeSpan.FromSeconds(10);

    private readonly TransportMemoryPoolFactory? _factory;
    private readonly Lock _trimLock = new();
    private volatile bool _disposed;

    public TransportMemoryPool() : this(null) { }

    internal TransportMemoryPool(TransportMemoryPoolFactory? factory) => _factory = factory;

    public override int MaxBufferSize => BlockSize;

    /// <summary>The <see cref="BlockSize"/> blocks.</summary>
    internal BlockList Large { get; } = new(BlockSize, MaxLargeBlocks);

    /// <summary>The <see cref="SmallBlockSize"/> blocks.</summary>
    internal BlockList Small { get; } = new(SmallBlockSize, int.MaxValue);

    public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (minBufferSize <= BlockSize && Large.TryRent() is { } large) return new Block(Large, large);
        if (minBufferSize <= SmallBlockSize && Small.TryRent() is { } small) return new Block(Small, small);
        return new Block(null, GC.AllocateUninitializedArray<byte>(minBufferSize));
    }

    /// <summary>Closes a demand window and releases part of the blocks its demand no longer needs.</summary>
    internal void Trim()
    {
        lock (_trimLock)
        {
            if (_disposed) return;
            Large.Trim();
            Small.Trim();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        Large.Close();
        Small.Close();
        _factory?.OnPoolDisposed(this);
    }

    /// <summary>The blocks of one size: those free for reuse, and the counts that trimming and the size cap rely on.</summary>
    internal sealed class BlockList(int blockSize, int maxBlocks)
    {
        private readonly ConcurrentQueue<byte[]> _free = new();
        private readonly int[] _windowPeaks = new int[DemandWindows];
        private int _window;
        private int _blocks;
        private int _inUse;
        private int _peakInUse;
        private long _allocated;
        private long _released;
        private volatile bool _closed;

        public int BlockSize => blockSize;

        /// <summary>Returned blocks kept for reuse.</summary>
        public int RetainedBlocks => _free.Count;

        /// <summary>Blocks rented and not yet returned.</summary>
        public int BlocksInUse => Volatile.Read(ref _inUse);

        /// <summary>Blocks that exist: out, retained, or on their way back.</summary>
        public int Blocks => Volatile.Read(ref _blocks);

        /// <summary>Blocks allocated because none was free.</summary>
        public long BlocksAllocated => Interlocked.Read(ref _allocated);

        /// <summary>Blocks <see cref="Trim"/> has released to the GC.</summary>
        public long BlocksReleased => Interlocked.Read(ref _released);

        /// <summary>A free block, a new one if the size has room for it, or null.</summary>
        public byte[]? TryRent()
        {
            if (!_free.TryDequeue(out byte[]? array))
            {
                if (Interlocked.Increment(ref _blocks) > maxBlocks)
                {
                    Interlocked.Decrement(ref _blocks);
                    return null;
                }

                Interlocked.Increment(ref _allocated);
                array = GC.AllocateUninitializedArray<byte>(blockSize, pinned: true);
            }

            int inUse = Interlocked.Increment(ref _inUse);
            if (inUse > Volatile.Read(ref _peakInUse)) RaisePeak(inUse);
            return array;
        }

        public void Return(byte[] array)
        {
            Interlocked.Decrement(ref _inUse);
            if (!_closed) _free.Enqueue(array);
        }

        public void Trim()
        {
            // The next window starts from the blocks still out, so a long-lived connection keeps counting.
            int inUse = Volatile.Read(ref _inUse);
            _windowPeaks[_window] = Math.Max(Interlocked.Exchange(ref _peakInUse, inUse), inUse);
            _window = (_window + 1) % DemandWindows;

            int demand = 0;
            foreach (int peak in _windowPeaks) demand = Math.Max(demand, peak);

            int retained = _free.Count;
            int surplus = retained - Math.Max(MinRetainedBlocks, demand - inUse);
            int release = Math.Min(surplus, Math.Max(MinReleasePerTrim, retained / 20));
            for (; release > 0 && _free.TryDequeue(out _); release--)
            {
                Interlocked.Decrement(ref _blocks);
                Interlocked.Increment(ref _released);
            }
        }

        public void Close()
        {
            _closed = true;
            _free.Clear();
        }

        private void RaisePeak(int inUse)
        {
            int peak = Volatile.Read(ref _peakInUse);
            while (inUse > peak)
            {
                int seen = Interlocked.CompareExchange(ref _peakInUse, inUse, peak);
                if (seen == peak) return;
                peak = seen;
            }
        }
    }

    private sealed class Block(BlockList? list, byte[] array) : IMemoryOwner<byte>
    {
        private int _returned;

        public Memory<byte> Memory => array;

        public void Dispose()
        {
            // Once only: a second return would hand the same array to two owners.
            if (Interlocked.Exchange(ref _returned, 1) == 0) list?.Return(array);
        }
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Microsoft.AspNetCore.Connections;

namespace Nethermind.Runner.JsonRpc;

/// <summary>Gives Kestrel memory pools of <see cref="TransportMemoryPool.BlockSize"/> blocks instead of its 4 KiB ones.</summary>
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
/// endpoints alike buffer their requests and responses in these blocks. Kestrel trims its own pools from its
/// heartbeat, which it does not offer to a replacement factory, so this one runs a single timer of its own every
/// <see cref="TransportMemoryPool.TrimInterval"/> while it has live pools, and stops it when the last pool is disposed.
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

/// <summary>A pool of pinned, uninitialised <see cref="BlockSize"/> blocks, as Kestrel's own pool hands out 4 KiB ones.</summary>
/// <remarks>
/// <para>
/// Keeps every returned block and releases blocks only when recent demand no longer needs them, the way Kestrel's own
/// pool does: a pool that served a burst holds on to it while the load lasts, so under steady load it stops
/// allocating, and pinned blocks dropped while still wanted would only come back as fresh pinned allocations that
/// just gen2 reclaims.
/// </para>
/// <para>
/// Demand is the most blocks out at once over the last <see cref="DemandWindows"/> calls of <see cref="Trim"/>, a
/// minute at the factory's <see cref="TrimInterval"/>. Each call keeps enough blocks to meet that peak again and
/// releases at most a twentieth of the rest, but at least <see cref="MinReleasePerTrim"/>, which is the rate Kestrel's
/// pool sheds blocks when idle; a pool that falls quiet ends with <see cref="MinRetainedBlocks"/>. The minute matters
/// for the engine endpoint: it needs its blocks for well under a millisecond once a slot, and Kestrel's ten-second
/// idle rule would release them between slots and allocate them again for the next payload.
/// </para>
/// <para>
/// A request larger than a block gets an unpooled array of its own; the pipes Kestrel builds on a pool never ask for
/// more than <see cref="MaxBufferSize"/>.
/// </para>
/// </remarks>
internal sealed class TransportMemoryPool : MemoryPool<byte>
{
    public const int BlockSize = 64 * 1024;

    /// <summary>Blocks an idle pool keeps, enough for one connection to receive and respond without allocating.</summary>
    public const int MinRetainedBlocks = 2;

    /// <summary>Calls of <see cref="Trim"/> whose peaks make up the demand a pool keeps blocks for.</summary>
    public const int DemandWindows = 6;

    /// <summary>The fewest surplus blocks one <see cref="Trim"/> releases, unless fewer are surplus.</summary>
    public const int MinReleasePerTrim = 10;

    /// <summary>How often the factory trims its pools; Kestrel's pool uses the same delay.</summary>
    public static readonly TimeSpan TrimInterval = TimeSpan.FromSeconds(10);

    private readonly ConcurrentQueue<byte[]> _free = new();
    private readonly TransportMemoryPoolFactory? _factory;
    private readonly Lock _trimLock = new();
    private readonly int[] _windowPeaks = new int[DemandWindows];
    private int _window;
    private int _inUse;
    private int _peakInUse;
    private long _allocated;
    private long _released;
    private volatile bool _disposed;

    public TransportMemoryPool() : this(null) { }

    internal TransportMemoryPool(TransportMemoryPoolFactory? factory) => _factory = factory;

    public override int MaxBufferSize => BlockSize;

    /// <summary>Returned blocks kept for reuse.</summary>
    internal int RetainedBlocks => _free.Count;

    /// <summary>Pooled blocks rented and not yet returned.</summary>
    internal int BlocksInUse => Volatile.Read(ref _inUse);

    /// <summary>Blocks allocated because none was free.</summary>
    internal long BlocksAllocated => Interlocked.Read(ref _allocated);

    /// <summary>Blocks <see cref="Trim"/> has released to the GC.</summary>
    internal long BlocksReleased => Interlocked.Read(ref _released);

    public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (minBufferSize > BlockSize)
        {
            return new Block(null, GC.AllocateUninitializedArray<byte>(minBufferSize, pinned: true));
        }

        int inUse = Interlocked.Increment(ref _inUse);
        if (inUse > Volatile.Read(ref _peakInUse)) RaisePeak(inUse);

        if (!_free.TryDequeue(out byte[]? array))
        {
            Interlocked.Increment(ref _allocated);
            array = GC.AllocateUninitializedArray<byte>(BlockSize, pinned: true);
        }

        return new Block(this, array);
    }

    /// <summary>Closes a demand window and releases part of the blocks its demand no longer needs.</summary>
    internal void Trim()
    {
        lock (_trimLock)
        {
            if (_disposed) return;

            // The next window starts from the blocks still out, so a long-lived connection keeps counting.
            int inUse = Volatile.Read(ref _inUse);
            _windowPeaks[_window] = Math.Max(Interlocked.Exchange(ref _peakInUse, inUse), inUse);
            _window = (_window + 1) % DemandWindows;

            int demand = 0;
            foreach (int peak in _windowPeaks) demand = Math.Max(demand, peak);

            int retained = _free.Count;
            int surplus = retained - Math.Max(MinRetainedBlocks, demand - inUse);
            int release = Math.Min(surplus, Math.Max(MinReleasePerTrim, retained / 20));
            for (; release > 0 && _free.TryDequeue(out _); release--) Interlocked.Increment(ref _released);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        _free.Clear();
        _factory?.OnPoolDisposed(this);
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

    private void Return(byte[] array)
    {
        Interlocked.Decrement(ref _inUse);
        if (!_disposed) _free.Enqueue(array);
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

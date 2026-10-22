// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Config;
using Nethermind.Core.Exceptions;
using Nethermind.Db.Rocks.Config;
using Nethermind.RocksDbBindings;

namespace Nethermind.Db.Rocks;

public sealed class HyperClockCacheWrapper : IDisposable
{
    private readonly Cache _cache;
    private readonly long _capacity;

    private int _disposed;

    /// <param name="capacity">The cache capacity in bytes. Must be greater than zero.</param>
    /// <exception cref="InvalidConfigurationException">
    /// <paramref name="capacity"/> is zero, which rocksdb cannot allocate a handle table for.
    /// </exception>
    public HyperClockCacheWrapper(ulong capacity = 32_000_000)
    {
        // A zero capacity makes rocksdb request a zero-length anonymous mapping for the handle
        // table and abort the process ("Anonymous mmap for RocksDB HyperClockCache failed"),
        // so reject it here while it can still be reported as the configuration error it is.
        if (capacity == 0)
        {
            throw new InvalidConfigurationException(
                $"Block cache capacity must be greater than zero. Check Db.{nameof(IDbConfig.SharedBlockCacheSize)} and FlatDb.{nameof(IFlatDbConfig.BlockCacheSizeBudget)}.",
                ExitCodes.ForbiddenOptionValue);
        }

        _cache = Cache.CreateHyperClock(capacity);
        _capacity = (long)capacity;
        GC.AddMemoryPressure(_capacity);
    }

    public nint Handle => _cache.Handle;

    public long GetUsage() => (long)_cache.GetUsage();

    /// <summary>Keeps the reported memory pressure balanced when an owner abandons the wrapper undisposed.</summary>
    /// <remarks>The native handle has its own critical finalizer, so only the pressure is released here.</remarks>
    ~HyperClockCacheWrapper() => Release(disposing: false);

    public void Dispose()
    {
        Release(disposing: true);
        GC.SuppressFinalize(this);
    }

    private void Release(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Zero when the constructor rejected the capacity before any pressure was added.
        if (_capacity > 0) GC.RemoveMemoryPressure(_capacity);
        if (disposing) _cache.Dispose();
    }
}

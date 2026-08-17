// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.RocksDbBindings;

namespace Nethermind.Db.Rocks;

public sealed class HyperClockCacheWrapper : IDisposable
{
    private readonly Cache _cache;
    private readonly long _capacity;

    private bool _disposed;

    public HyperClockCacheWrapper(ulong capacity = 32_000_000)
    {
        _cache = Cache.CreateHyperClock(capacity);
        _capacity = (long)capacity;
        GC.AddMemoryPressure(_capacity);
    }

    public nint Handle => _cache.Handle;

    public long GetUsage() => (long)_cache.GetUsage();

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _cache.Dispose();
        GC.RemoveMemoryPressure(_capacity);
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Extensions;
using RocksDbSharp;

namespace Nethermind.Db.Rocks;

public class HyperClockCacheWrapper : IDisposable
{
    private readonly IntPtr _cacheHandle;

    public HyperClockCacheWrapper(ulong capacity = 32_000_000)
    {
        _cacheHandle = RocksDbSharp.Native.Instance.rocksdb_cache_create_hyper_clock(new UIntPtr(capacity), 0);
    }

    private bool _isDisposed;
    public IntPtr? Handle => _cacheHandle;
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;
        RocksDbSharp.Native.Instance.rocksdb_cache_destroy(_cacheHandle);
    }

    public long GetUsage()
    {
        return (long)Native.Instance.rocksdb_cache_get_usage(_cacheHandle);
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using RocksDbSharp;

namespace Nethermind.Db.Rocks;

public class HyperClockCacheWrapper : SafeHandleZeroOrMinusOneIsInvalid
{
    private static readonly Lock _nativeCacheLock = new();

    public HyperClockCacheWrapper(ulong capacity = 32_000_000) : base(ownsHandle: true)
    {
        lock (_nativeCacheLock)
        {
            SetHandle(Native.Instance.rocksdb_cache_create_hyper_clock(new UIntPtr(capacity), 0));
        }
    }

    public IntPtr Handle => DangerousGetHandle();

    protected override bool ReleaseHandle()
    {
        lock (_nativeCacheLock)
        {
            Native.Instance.rocksdb_cache_destroy(handle);
        }
        return true;
    }

    public long GetUsage()
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        return (long)Native.Instance.rocksdb_cache_get_usage(DangerousGetHandle());
    }
}

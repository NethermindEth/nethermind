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

    private readonly long _capacity;

    public HyperClockCacheWrapper(ulong capacity = 32_000_000) : base(ownsHandle: true)
    {
        lock (_nativeCacheLock)
        {
            SetHandle(Native.Instance.rocksdb_cache_create_hyper_clock(new UIntPtr(capacity), 0));
        }
        // If the native call returned a zero/null handle, SafeHandle won't call ReleaseHandle,
        // so don't add pressure either — keep add/remove balanced.
        _capacity = IsInvalid ? 0 : (long)capacity;
        if (_capacity > 0) GC.AddMemoryPressure(_capacity);
    }

    public IntPtr Handle => DangerousGetHandle();

    protected override bool ReleaseHandle()
    {
        lock (_nativeCacheLock)
        {
            Native.Instance.rocksdb_cache_destroy(handle);
        }
        if (_capacity > 0) GC.RemoveMemoryPressure(_capacity);
        return true;
    }

    public long GetUsage()
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        return (long)Native.Instance.rocksdb_cache_get_usage(DangerousGetHandle());
    }
}

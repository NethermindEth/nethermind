// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Nethermind.RocksDbBindings.Native;

using static Nethermind.RocksDbBindings.Native.RocksDbNative;

namespace Nethermind.Db.Rocks;

public class HyperClockCacheWrapper : SafeHandleZeroOrMinusOneIsInvalid
{
    private static readonly Lock _nativeCacheLock = new();

    private readonly long _capacity;

    public unsafe HyperClockCacheWrapper(ulong capacity = 32_000_000) : base(ownsHandle: true)
    {
        lock (_nativeCacheLock)
        {
            SetHandle((nint)rocksdb_cache_create_hyper_clock((nuint)capacity, 0));
        }
        // If the native call returned a zero/null handle, SafeHandle won't call ReleaseHandle,
        // so don't add pressure either — keep add/remove balanced.
        _capacity = IsInvalid ? 0 : (long)capacity;
        if (_capacity > 0) GC.AddMemoryPressure(_capacity);
    }

    public nint Handle => DangerousGetHandle();

    protected override unsafe bool ReleaseHandle()
    {
        lock (_nativeCacheLock)
        {
            rocksdb_cache_destroy((rocksdb_cache_t*)handle);
        }
        if (_capacity > 0) GC.RemoveMemoryPressure(_capacity);
        return true;
    }

    public unsafe long GetUsage()
    {
        bool addedRef = false;
        try
        {
            // Keep the cache alive if disposal races this call.
            DangerousAddRef(ref addedRef);
            return (long)rocksdb_cache_get_usage((rocksdb_cache_t*)DangerousGetHandle());
        }
        finally
        {
            if (addedRef) DangerousRelease();
        }
    }
}

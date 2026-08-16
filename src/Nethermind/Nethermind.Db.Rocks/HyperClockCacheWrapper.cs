// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Microsoft.Win32.SafeHandles;
using Nethermind.RocksDbBindings;

namespace Nethermind.Db.Rocks;

public class HyperClockCacheWrapper : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly Cache _cache;
    private readonly long _capacity;

    public HyperClockCacheWrapper(ulong capacity = 32_000_000) : base(ownsHandle: true)
    {
        _cache = Cache.CreateHyperClock(capacity);
        SetHandle(_cache.Handle);
        // If the native call returned a zero/null handle, SafeHandle won't call ReleaseHandle,
        // so don't add pressure either — keep add/remove balanced.
        _capacity = IsInvalid ? 0 : (long)capacity;
        if (_capacity > 0) GC.AddMemoryPressure(_capacity);
    }

    public nint Handle => DangerousGetHandle();

    protected override bool ReleaseHandle()
    {
        _cache.Dispose();
        if (_capacity > 0) GC.RemoveMemoryPressure(_capacity);
        return true;
    }

    public long GetUsage()
    {
        bool addedRef = false;
        try
        {
            // Keep the cache alive if disposal races this call.
            DangerousAddRef(ref addedRef);
            return (long)_cache.GetUsage();
        }
        finally
        {
            if (addedRef) DangerousRelease();
        }
    }
}

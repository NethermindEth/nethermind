// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Nethermind.Core.Caching;
using RocksDbSharp;

namespace Nethermind.Db.Rocks;

public class HyperClockCacheWrapper : SafeHandleZeroOrMinusOneIsInvalid, IAdaptiveCache
{
    private static readonly Lock _nativeCacheLock = new();
    private const long MinimumAdaptiveCapacity = 16 * 1024 * 1024;

    private long _capacity;

    public HyperClockCacheWrapper(
        ulong capacity = 32_000_000,
        string name = "RocksDB block cache",
        long maximumCapacity = long.MaxValue) : base(ownsHandle: true)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, (ulong)long.MaxValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCapacity, (long)capacity);

        lock (_nativeCacheLock)
        {
            SetHandle(Native.Instance.rocksdb_cache_create_hyper_clock(new UIntPtr(capacity), 0));
        }
        // If the native call returned a zero/null handle, SafeHandle won't call ReleaseHandle,
        // so don't add pressure either — keep add/remove balanced.
        _capacity = IsInvalid ? 0 : (long)capacity;
        if (_capacity > 0) GC.AddMemoryPressure(_capacity);
        Name = name;
        MinimumCapacity = Math.Min(_capacity, MinimumAdaptiveCapacity);
        MaximumCapacity = maximumCapacity;
    }

    public IntPtr Handle => DangerousGetHandle();
    public string Name { get; }
    public long Capacity => Volatile.Read(ref _capacity);
    public long Usage => GetUsage();
    public long MinimumCapacity { get; }
    public long MaximumCapacity { get; }

    protected override bool ReleaseHandle()
    {
        lock (_nativeCacheLock)
        {
            Native.Instance.rocksdb_cache_destroy(handle);
        }
        long capacity = Interlocked.Exchange(ref _capacity, 0);
        if (capacity > 0) GC.RemoveMemoryPressure(capacity);
        return true;
    }

    public long GetUsage()
    {
        bool addedReference = false;
        try
        {
            DangerousAddRef(ref addedReference);
            return (long)Native.Instance.rocksdb_cache_get_usage(DangerousGetHandle());
        }
        finally
        {
            if (addedReference) DangerousRelease();
        }
    }

    public void SetCapacity(long capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, MinimumCapacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, MaximumCapacity);

        bool addedReference = false;
        try
        {
            DangerousAddRef(ref addedReference);
            lock (_nativeCacheLock)
            {
                long previous = Volatile.Read(ref _capacity);
                if (capacity == previous) return;

                Native.Instance.rocksdb_cache_set_capacity(DangerousGetHandle(), new UIntPtr((ulong)capacity));
                Volatile.Write(ref _capacity, capacity);
                if (capacity > previous)
                    GC.AddMemoryPressure(capacity - previous);
                else
                    GC.RemoveMemoryPressure(previous - capacity);
            }
        }
        finally
        {
            if (addedReference) DangerousRelease();
        }
    }
}

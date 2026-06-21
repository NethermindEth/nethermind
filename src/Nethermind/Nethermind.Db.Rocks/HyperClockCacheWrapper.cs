// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Microsoft.Win32.SafeHandles;

namespace Nethermind.Db.Rocks;

/// <summary>
/// Compatibility wrapper for the former RocksDB shared block cache.
/// </summary>
public sealed class HyperClockCacheWrapper : SafeHandleZeroOrMinusOneIsInvalid
{
    public HyperClockCacheWrapper(ulong capacity = 32_000_000) : base(ownsHandle: true) => SetHandle(IntPtr.Zero);

    public IntPtr Handle => IntPtr.Zero;

    public long GetUsage()
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        return 0;
    }

    protected override bool ReleaseHandle() => true;
}

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

namespace Nethermind.Core.Threading;

/// <summary>
/// A <see cref="long"/> isolated on its own cache line so that concurrent atomic updates to it do not
/// suffer false sharing with neighbouring fields.
/// </summary>
/// <remarks>
/// The field sits at offset 64 within a <see cref="CacheLineSize"/>-byte block. This separates it from
/// fields laid out before the struct on 64-byte cache-line machines; callers should avoid placing another
/// hot field immediately after an instance unless the surrounding layout and alignment are known.
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = CacheLineSize)]
public struct CacheLinePaddedLong(long value)
{
    // Cache line is 64 bytes on Intel and 128 bytes on Apple Silicon; pad to the larger to be safe.
    private const int CacheLineSize = 128;
    private const int CacheLinePadding = CacheLineSize / 2;

    [FieldOffset(CacheLinePadding)]
    public long Value = value;
}

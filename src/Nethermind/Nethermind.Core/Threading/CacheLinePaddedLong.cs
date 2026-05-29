// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

namespace Nethermind.Core.Threading;

/// <summary>
/// A <see cref="long"/> isolated on its own cache line so that concurrent atomic updates to it do not
/// suffer false sharing with neighbouring fields.
/// </summary>
/// <remarks>
/// The field sits at offset 64 within a <see cref="CacheLineSize"/>-byte block, so the padding follows the
/// value. This isolates it from anything laid out after it; callers must not place another hot field
/// immediately before an instance.
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

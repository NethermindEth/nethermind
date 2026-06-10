// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;

namespace Nethermind.Serialization.Ssz;

/// <summary>
/// An allocation-free 16-byte value type used for fixed-length SSZ byte vectors.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
public struct SszBytes16
{
    private ulong _low;
    private ulong _high;

    /// <summary>
    /// Creates an <see cref="SszBytes16"/> from exactly 16 bytes without heap allocation.
    /// </summary>
    public static SszBytes16 FromSpan(ReadOnlySpan<byte> span)
    {
        if (span.Length != 16)
        {
            throw new ArgumentException("SszBytes16 requires exactly 16 bytes", nameof(span));
        }

        SszBytes16 result = default;
        span.CopyTo(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref result, 1)));
        return result;
    }

    public ReadOnlySpan<byte> AsSpan() =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref this, 1));
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;

namespace Nethermind.Serialization.Ssz;

/// <summary>
/// An allocation-free 8-byte value type used wherever an SSZ wire format
/// requires a fixed-length 8-byte field (e.g. Engine API <c>PayloadId</c>).
/// Lives next to <see cref="SszBytes32"/> so SSZ-generator consumers can
/// reference it without pulling in unrelated assemblies.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 8)]
public struct SszBytes8
{
    private ulong _value;

    /// <summary>
    /// Creates an <see cref="SszBytes8"/> from exactly 8 bytes without any heap allocation.
    /// </summary>
    public static SszBytes8 FromSpan(ReadOnlySpan<byte> span)
    {
        if (span.Length != 8)
            throw new ArgumentException("SszBytes8 requires exactly 8 bytes", nameof(span));

        SszBytes8 result = default;
        span.CopyTo(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref result, 1)));
        return result;
    }

    public ReadOnlySpan<byte> AsSpan() =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref this, 1));
}

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core.Extensions;

namespace Nethermind.State.Flat;

/// <summary>
/// Make storing slot value smaller than a byte[].
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 32, Size = 32)]
public struct SlotValue
{
    public readonly Vector256<byte> _bytes; // Use Vector256 as the internal storage field
    public readonly Span<byte> AsSpan => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref Unsafe.AsRef(in _bytes), 1));
    public readonly ReadOnlySpan<byte> AsReadOnlySpan => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in _bytes), 1));
    public const int ByteCount = 32;

    public SlotValue(ReadOnlySpan<byte> data)
    {
        if (data.Length > 32)
        {
            throw new ArgumentException("Slot value cannot exceed 32 bytes", nameof(data));
        }

        Span<byte> buffer = stackalloc byte[32];
        buffer.Clear();
        data.CopyTo(buffer);
        _bytes = Unsafe.ReadUnaligned<Vector256<byte>>(ref MemoryMarshal.GetReference(buffer));
    }

    public static SlotValue? FromBytes(byte[]? data) =>
        data == null ? null : new SlotValue(data);

    public static SlotValue? FromSpanWithoutLeadingZero(ReadOnlySpan<byte> value)
    {
        Span<byte> buffer = stackalloc byte[32];
        buffer.Clear();
        value.CopyTo(buffer[(32 - value.Length)..]);
        return new SlotValue(buffer);
    }

    /// <summary>
    /// Currently the worldstate that the evm use expect the bytes to be without leading zeros
    /// </summary>
    public readonly byte[] ToEvmBytes() =>
        AsReadOnlySpan.WithoutLeadingZeros().ToArray();
}

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
public readonly struct SlotValue
{
    public readonly Vector256<byte> _bytes; // Use Vector256 as the internal storage field
    public Span<byte> AsSpan => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref Unsafe.AsRef(in _bytes), 1));
    public ReadOnlySpan<byte> AsReadOnlySpan => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in _bytes), 1));
    public const int ByteCount = 32;

    public SlotValue(ReadOnlySpan<byte> data)
    {
        if (data.Length > 32)
        {
            ThrowInvalidLength();
        }

        if (data.Length == 32)
        {
            _bytes = Unsafe.ReadUnaligned<Vector256<byte>>(ref MemoryMarshal.GetReference(data));
        }
        else
        {
            _bytes = Vector256<byte>.Zero;
            data.CopyTo(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref _bytes, 1)));
        }
    }

    private static void ThrowInvalidLength() => throw new ArgumentException("Slot value cannot exceed 32 bytes", "data");

    public static SlotValue? FromBytes(byte[]? data) => data == null ? null : new SlotValue(data);

    public static SlotValue FromSpanWithoutLeadingZero(ReadOnlySpan<byte> data)
    {
        switch (data.Length)
        {
            case > 32:
                ThrowInvalidLength();
                return default;
            case 32:
                return Unsafe.ReadUnaligned<SlotValue>(ref MemoryMarshal.GetReference(data));
            default:
                Span<byte> buffer = stackalloc byte[32];
                buffer[..(32 - data.Length)].Clear();
                data.CopyTo(buffer[(32 - data.Length)..]);
                return Unsafe.ReadUnaligned<SlotValue>(ref MemoryMarshal.GetReference(buffer));
        }
    }

    /// <summary>
    /// Currently, the worldstate that the evm use expect the bytes to be without leading zeros
    /// </summary>
    public byte[] ToEvmBytes() => AsReadOnlySpan.WithoutLeadingZeros().ToArray();
}

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core.Extensions;

namespace Nethermind.State.Flat;

[StructLayout(LayoutKind.Sequential, Pack = 32, Size = 32)]
public struct SlotValue
{
    public readonly Vector256<byte> _bytes; // Use Vector256 as the internal storage field
    public readonly Span<byte> AsSpan => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref Unsafe.AsRef(in _bytes), 1));
    public readonly ReadOnlySpan<byte> AsReadOnlySpan => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in _bytes), 1));

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

    public static SlotValue? FromBytes(byte[]? data)
    {
        if (data == null) return null;
        return new SlotValue(data);
    }

    public static SlotValue FromSpan(ReadOnlySpan<byte> data)
    {
        return new SlotValue(data);
    }

    public static SlotValue? FromSpanWithoutLeadingZero(ReadOnlySpan<byte> value)
    {
        Span<byte> buffer = stackalloc byte[32];
        value.CopyTo(buffer[(32 - value.Length)..]);
        return new SlotValue(buffer);
    }

    public static Span<byte> GetSpan(ref SlotValue value)
    {
        // 1. Unsafe.As turns 'ref SlotValue' into 'ref byte'
        // 2. MemoryMarshal.CreateSpan creates a 32-byte "window" over that address
        return MemoryMarshal.CreateSpan(ref Unsafe.As<SlotValue, byte>(ref value), 32);
    }
    public static ReadOnlySpan<byte> GetReadOnlySpan(in SlotValue value)
    {
        return MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.As<SlotValue, byte>(ref Unsafe.AsRef(in value)), 32);
    }

    /// <summary>
    /// Currently the worldstate that the evm use expect the bytes to be without leading zeros
    /// </summary>
    /// <returns></returns>
    public readonly byte[] ToEvmBytes()
    {
        return AsReadOnlySpan.WithoutLeadingZeros().ToArray();
    }

    public readonly void CopyTo(Span<byte> destination)
    {
        if (destination.Length < 32)
        {
            throw new ArgumentException("Destination must be at least 32 bytes", nameof(destination));
        }
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(destination), _bytes);
    }
}

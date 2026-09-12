// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.Int256;

namespace Nethermind.State.Flat;

/// <summary>A numeric storage slot held without a separate byte array.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 32)]
public readonly struct SlotValue
{
    public readonly UInt256 Value;
    public const int ByteCount = 32;

    public SlotValue(in UInt256 value) => Value = value;

    public void ToUInt256(out UInt256 value) => value = Value;

    /// <summary>Reads big-endian bytes, padding a short input on the right.</summary>
    public SlotValue(ReadOnlySpan<byte> data)
    {
        if (data.Length > ByteCount) ThrowInvalidLength();
        Value = new UInt256(data, isBigEndian: true);
        if (data.Length is > 0 and < ByteCount) Value <<= (ByteCount - data.Length) * 8;
    }

    private static void ThrowInvalidLength() => throw new ArgumentException("Slot value cannot exceed 32 bytes", "data");

    public static SlotValue? FromBytes(byte[]? data) => data is null ? null : new SlotValue(data);

    /// <summary>Reads a big-endian integer with omitted leading zero bytes.</summary>
    public static SlotValue FromSpanWithoutLeadingZero(ReadOnlySpan<byte> data)
    {
        if (data.Length > ByteCount) ThrowInvalidLength();
        UInt256 value = new(data, isBigEndian: true);
        return new(in value);
    }
}

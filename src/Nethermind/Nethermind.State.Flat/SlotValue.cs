// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.Int256;

namespace Nethermind.State.Flat;

/// <summary>A numeric storage slot held without a separate byte array.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 32)]
public readonly struct SlotValue(in UInt256 value)
{
    public readonly UInt256 Value = value;
    public const int ByteCount = 32;

    private static void ThrowInvalidLength() => throw new ArgumentException("Slot value cannot exceed 32 bytes", "data");

    /// <summary>Reads a big-endian integer with omitted leading zero bytes.</summary>
    public static SlotValue FromSpanWithoutLeadingZero(ReadOnlySpan<byte> data)
    {
        if (data.Length > ByteCount) ThrowInvalidLength();
        UInt256 value = new(data, isBigEndian: true);
        return new(in value);
    }
}

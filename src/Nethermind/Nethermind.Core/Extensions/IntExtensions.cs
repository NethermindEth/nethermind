// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Int256;

namespace Nethermind.Core.Extensions;

public static class IntExtensions
{
    public static string ToHexString(this int @this)
    {
        return $"0x{@this:x}";
    }

    public static UInt256 Ether(this int @this)
    {
        return (uint)@this * Unit.Ether;
    }

    public static UInt256 Wei(this int @this)
    {
        return (uint)@this * Unit.Wei;
    }

    public static UInt256 GWei(this int @this)
    {
        return (uint)@this * Unit.GWei;
    }

    public static byte[] ToByteArray(this int value)
    {
        byte[] bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    public static byte[] ToBigEndianByteArray(this uint value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return bytes;
    }

    public static byte[] ToBigEndianByteArray(this int value)
        => ToBigEndianByteArray((uint)value);
}

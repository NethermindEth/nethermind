// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;

namespace Nethermind.Core.Extensions;

public static class Short16Extensions
{
    public static byte[] ToByteArray(this short value)
    {
        byte[] bytes = new byte[sizeof(short)];
        BinaryPrimitives.WriteInt16BigEndian(bytes, value);
        return bytes;
    }

    public static byte[] ToBigEndianByteArray(this ushort value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return bytes;
    }

    public static byte[] ToBigEndianByteArray(this short value)
        => ToBigEndianByteArray((ushort)value);

}

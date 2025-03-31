// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Extensions;

public static class UInt64Extensions
{
    public static ulong ToULongFromBigEndianByteArrayWithoutLeadingZeros(this byte[]? bytes) =>
        ToULongFromBigEndianByteArrayWithoutLeadingZeros(bytes.AsSpan());

    public static ulong ToULongFromBigEndianByteArrayWithoutLeadingZeros(this ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return 0L;
        }

        ulong value = 0;
        int length = bytes.Length;

        for (int i = 0; i < length; i++)
        {
            value = (value << 8) | bytes[i];
        }

        return value;
    }
}

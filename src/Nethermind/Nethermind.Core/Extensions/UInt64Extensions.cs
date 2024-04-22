// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Extensions;

public static class UInt64Extensions
{
    // ToDo add tests
    public static ulong ToULongFromBigEndianByteArrayWithoutLeadingZeros(this byte[]? bytes)
    {
        if (bytes is null)
        {
            return 0L;
        }

        ulong value = 0;
        int length = bytes.Length;

        for (int i = 0; i < length; i++)
        {
            value += (ulong)bytes[length - 1 - i] << 8 * i;
        }

        return value;
    }
}

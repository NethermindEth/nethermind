// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;

namespace Nethermind.Serialization.Ssz;

public static partial class Ssz
{
    public static void Encode(Span<byte> span, BitArray? vector)
    {
        if (vector is null)
        {
            return;
        }
        int byteLength = (vector.Length + 7) / 8;
        byte[] bytes = new byte[byteLength];
        vector.CopyTo(bytes, 0);
        Encode(span, bytes);
    }

    public static void Encode(Span<byte> span, BitArray? list, int limit)
    {
        if (list is null)
        {
            return;
        }
        int byteLength = (list.Length + 8) / 8;
        byte[] bytes = new byte[byteLength];
        list.CopyTo(bytes, 0);
        bytes[byteLength - 1] |= (byte)(1 << (list.Length % 8));
        Encode(span, bytes);
    }
}

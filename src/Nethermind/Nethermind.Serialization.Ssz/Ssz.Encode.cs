// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Runtime.CompilerServices;

namespace Nethermind.Serialization.Ssz;

/// <summary>
/// https://github.com/ethereum/consensus-specs/blob/dev/ssz/simple-serialize.md
/// </summary>
public static partial class Ssz
{
    public static void Encode(Span<byte> span, BitArray? vector)
    {
        if (vector is null)
        {
            return;
        }

        EncodeVector(span, vector);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EncodeVector(Span<byte> span, BitArray value)
    {
        int byteLength = (value.Length + 7) / 8;
        byte[] bytes = new byte[byteLength];
        value.CopyTo(bytes, 0);
        bytes.CopyTo(span);
    }

    public static void Encode(Span<byte> span, BitArray? list, int limit)
    {
        if (list is null)
        {
            return;
        }

        EncodeList(span, list);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EncodeList(Span<byte> span, BitArray value)
    {
        int byteLength = (value.Length + 8) / 8;
        byte[] bytes = new byte[byteLength];
        value.CopyTo(bytes, 0);
        bytes[byteLength - 1] |= (byte)(1 << (value.Length % 8));
        bytes.CopyTo(span);
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
        ReadOnlySpan<byte> bytes = CollectionsMarshal.AsBytes(value);
        bytes.CopyTo(span);
        int remainder = value.Length & 7;
        if (remainder != 0)
        {
            // AsBytes exposes unused backing bits; SSZ requires zero padding.
            span[bytes.Length - 1] &= (byte)((1 << remainder) - 1);
        }
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
        int sentinelIndex = value.Length / 8;
        Span<byte> destination = span[..(sentinelIndex + 1)];
        EncodeVector(destination, value);
        int remainder = value.Length & 7;
        if (remainder == 0) destination[sentinelIndex] = 1;
        else destination[sentinelIndex] |= (byte)(1 << remainder);
    }
}

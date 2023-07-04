// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Ssz;

public static partial class Ssz
{
    public const int RootLength = Root.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Encode(Span<byte> span, Root value, ref int offset)
    {
        Encode(span.Slice(offset, Ssz.RootLength), value);
        offset += Ssz.RootLength;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(Span<byte> span, Root value)
    {
        Encode(span, value.AsSpan());
    }

    //        public static void Encode(Span<byte> span, ReadOnlySpan<Hash32> value)
    //        {
    //            for (int i = 0; i < value.Length; i++)
    //            {
    //                Encode(span.Slice(i * Ssz.Hash32Length, Ssz.Hash32Length), value[i]);    
    //            }
    //        }

    public static void Encode(Span<byte> span, IReadOnlyList<Root> value)
    {
        for (int i = 0; i < value.Count; i++)
        {
            Encode(span.Slice(i * Ssz.RootLength, Ssz.RootLength), value[i]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Root DecodeRoot(ReadOnlySpan<byte> span, ref int offset)
    {
        Root hash32 = DecodeRoot(span.Slice(offset, Ssz.RootLength));
        offset += Ssz.RootLength;
        return hash32;
    }

    public static Root DecodeRoot(ReadOnlySpan<byte> span)
    {
        return new Root(span);
    }

    public static Root[] DecodeRoots(ReadOnlySpan<byte> span)
    {
        if (span.Length == 0)
        {
            return Array.Empty<Root>();
        }

        int count = span.Length / Ssz.RootLength;
        Root[] result = new Root[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> current = span.Slice(i * Ssz.RootLength, Ssz.RootLength);
            result[i] = DecodeRoot(current);
        }

        return result;
    }
}

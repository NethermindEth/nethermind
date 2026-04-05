// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Ssz;

public static partial class Ssz
{
    public static void Encode(Span<byte> span, SszBytes32 value) =>
        value.Hash.Bytes.CopyTo(span);

    public static void Encode(Span<byte> span, SszBytes32[] value)
    {
        const int typeSize = 32;
        if (span.Length != value.Length * typeSize)
            ThrowTargetLength<SszBytes32[]>(span.Length, value.Length * typeSize);
        for (int i = 0; i < value.Length; i++)
            Encode(span.Slice(i * typeSize, typeSize), value[i]);
    }

    public static void Decode(ReadOnlySpan<byte> span, out SszBytes32 value)
    {
        ValidateLength(span, 32);
        value = new SszBytes32(span);
    }

    public static void Decode(ReadOnlySpan<byte> span, out ReadOnlySpan<SszBytes32> result)
    {
        const int typeSize = 32;
        ValidateArrayLength(span, typeSize);
        SszBytes32[] array = new SszBytes32[span.Length / typeSize];
        for (int i = 0; i < array.Length; i++)
            Decode(span.Slice(i * typeSize, typeSize), out array[i]);
        result = array;
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Ssz;

public sealed class Int32SszVectorConverter : SszVectorConverter<int>
{
    public const int Length = sizeof(int);

    private Int32SszVectorConverter() { }

    public static int FromSpan(ReadOnlySpan<byte> span)
    {
        Ssz.Decode(span, out int result);
        return result;
    }

    public static void ToSpan(Span<byte> span, int value) => Ssz.Encode(span, value);
}

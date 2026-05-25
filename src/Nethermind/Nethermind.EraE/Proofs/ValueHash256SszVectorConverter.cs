// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;

namespace Nethermind.EraE.Proofs;

public sealed class ValueHash256SszVectorConverter : SszVectorConverter<ValueHash256>
{
    public const int Length = ValueHash256.MemorySize;

    private ValueHash256SszVectorConverter() { }

    public static ValueHash256 FromSpan(ReadOnlySpan<byte> span)
    {
        if (span.Length != Length)
        {
            throw new InvalidDataException($"{nameof(ValueHash256SszVectorConverter)} expects input of length {Length} and received {span.Length}");
        }

        return new ValueHash256(span);
    }

    public static void ToSpan(Span<byte> span, ValueHash256 value) => value.Bytes.CopyTo(span);
}

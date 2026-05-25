// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Stateless.Execution.IO;

public sealed class ValueHash256SszVectorConverter : SszVectorConverter<ValueHash256>
{
    public const int Length = ValueHash256.MemorySize;

    private ValueHash256SszVectorConverter() { }

    public static ValueHash256 FromSpan(ReadOnlySpan<byte> span)
    {
        SszVectorConverterHelpers.ValidateLength(span, Length, nameof(ValueHash256SszVectorConverter));
        return new ValueHash256(span);
    }

    public static void ToSpan(Span<byte> span, ValueHash256 value) => value.Bytes.CopyTo(span);
}

file static class SszVectorConverterHelpers
{
    public static void ValidateLength(ReadOnlySpan<byte> span, int expectedLength, string converterName)
    {
        if (span.Length != expectedLength)
        {
            throw new InvalidDataException($"{converterName} expects input of length {expectedLength} and received {span.Length}");
        }
    }
}

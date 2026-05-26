// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Core.Crypto;

public sealed class ValueHash256SszVectorConverter : SszVectorConverter<ValueHash256>
{
    public const int Length = ValueHash256.MemorySize;

    private ValueHash256SszVectorConverter() { }

    public static ValueHash256 FromSpan(ReadOnlySpan<byte> span) => new(span);

    public static void ToSpan(Span<byte> span, ValueHash256 value) => value.Bytes.CopyTo(span);
}

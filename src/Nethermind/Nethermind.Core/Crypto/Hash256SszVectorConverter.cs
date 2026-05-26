// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Core.Crypto;

public sealed class Hash256SszVectorConverter : SszVectorConverter<Hash256>
{
    public const int Length = Hash256.Size;

    private Hash256SszVectorConverter() { }

    public static Hash256 FromSpan(ReadOnlySpan<byte> span) => new(span);

    public static void ToSpan(Span<byte> span, Hash256 value) => value.Bytes.CopyTo(span);
}

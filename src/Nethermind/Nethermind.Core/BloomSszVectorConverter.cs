// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Merkleization;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Core;

public sealed class BloomSszVectorConverter : ISszVectorConverter<Bloom>
{
    public const int Length = Bloom.ByteLength;

    private BloomSszVectorConverter() { }

    public static Bloom FromSpan(ReadOnlySpan<byte> span) => new(span);

    public static void ToSpan(Span<byte> span, Bloom value) => value.Bytes.CopyTo(span);

    public static void Feed(ref Merkleizer merkleizer, Bloom value) => merkleizer.Feed(value.Bytes);
}

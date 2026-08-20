// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;
using Nethermind.Serialization.Ssz;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.Stateless.Execution.IO;

[SszVectorTypeConverter<SszPublicKey>]
public static class SszPublicKeyVectorTypeConverter
{
    public const int Length = SszPublicKey.PublicKeyLength;

    public static SszPublicKey FromSpan(ReadOnlySpan<byte> span) => SszPublicKey.FromSpan(span);

    public static void FromSpan(ReadOnlySpan<byte> span, Span<SszPublicKey> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = FromSpan(span.Slice(i * Length, Length));
        }
    }

    public static void ToSpan(Span<byte> span, SszPublicKey value) => value.AsSpan().CopyTo(span);

    public static void ToSpan(Span<byte> span, ReadOnlySpan<SszPublicKey> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            ToSpan(span.Slice(i * Length, Length), values[i]);
        }
    }

    public static void Feed(ref Merkleizer merkleizer, SszPublicKey value)
    {
        Merkle.Merkleize(out UInt256 root, value.AsSpan());
        merkleizer.Feed(root);
    }
}

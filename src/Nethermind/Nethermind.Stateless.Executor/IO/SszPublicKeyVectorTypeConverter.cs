// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.Stateless.Execution.IO;

[SszVectorTypeConverter<SszPublicKey>]
public static class SszPublicKeyVectorTypeConverter
{
    public const int Length = SszPublicKey.PublicKeyLength;

    public static SszPublicKey FromSpan(ReadOnlySpan<byte> span) => SszPublicKey.FromSpan(span);

    // An inline array of bytes has the flat wire layout already, so the whole list moves in one memcpy.
    public static void FromSpan(ReadOnlySpan<byte> span, Span<SszPublicKey> values) =>
        span[..(values.Length * Length)].CopyTo(MemoryMarshal.AsBytes(values));

    public static void ToSpan(Span<byte> span, SszPublicKey value) => value.AsSpan().CopyTo(span);

    public static void ToSpan(Span<byte> span, ReadOnlySpan<SszPublicKey> values) =>
        MemoryMarshal.AsBytes(values).CopyTo(span);

    public static void Feed(ref Merkleizer merkleizer, SszPublicKey value)
    {
        Merkle.Merkleize(out UInt256 root, value.AsSpan());
        merkleizer.Feed(root);
    }
}

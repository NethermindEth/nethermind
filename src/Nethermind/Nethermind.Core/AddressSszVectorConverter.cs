// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;
using Nethermind.Merkleization;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Core;

public sealed class AddressSszVectorConverter : ISszVectorConverter<Address>
{
    public const int Length = Address.Size;

    private AddressSszVectorConverter() { }

    public static Address FromSpan(ReadOnlySpan<byte> span) => new(span);

    public static void ToSpan(Span<byte> span, Address value) => value.Bytes.CopyTo(span);

    public static void Feed(ref Merkleizer merkleizer, Address value)
    {
        Merkle.Merkleize(out UInt256 root, value.Bytes);
        merkleizer.Feed(root);
    }
}

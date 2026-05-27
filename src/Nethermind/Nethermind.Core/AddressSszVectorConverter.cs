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

    public static void Merkleize(Address value, out UInt256 root) => Merkle.Merkleize(out root, value.Bytes);
}

// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Int256;
using Nethermind.Merkleization;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Shutter;

[SszContainer]
public partial struct SlotDecryptionIdentities
{
    public ulong InstanceID { get; set; }
    public ulong Eon { get; set; }
    public ulong Slot { get; set; }
    public ulong TxPointer { get; set; }

    [SszList(1024)]
    public ArrayPoolList<IdentityPreimage> IdentityPreimages { get; set; }
}

public partial struct IdentityPreimage(ReadOnlyMemory<byte> data)
{
    public const int Length = 52;

    public ReadOnlyMemory<byte> Data { get; set; } = data;
}

public sealed class IdentityPreimageSszVectorConverter : ISszVectorConverter<IdentityPreimage>
{
    public const int Length = IdentityPreimage.Length;

    private IdentityPreimageSszVectorConverter() { }

    public static IdentityPreimage FromSpan(ReadOnlySpan<byte> span)
    {
        Validate(span);
        return new(span.ToArray());
    }

    public static void ToSpan(Span<byte> span, IdentityPreimage value)
    {
        Validate(value.Data.Span);
        value.Data.Span.CopyTo(span);
    }

    public static void Feed(ref Merkleizer merkleizer, IdentityPreimage value)
    {
        ReadOnlySpan<byte> data = value.Data.Span;
        if (data.IsEmpty)
        {
            merkleizer.Feed(default);
            return;
        }

        Validate(data);
        Merkle.Merkleize(out UInt256 root, data);
        merkleizer.Feed(root);
    }

    private static void Validate(ReadOnlySpan<byte> span)
    {
        if (span.Length != Length)
        {
            throw new System.IO.InvalidDataException($"{nameof(IdentityPreimage)}.{nameof(IdentityPreimage.Data)} must contain exactly {Length} bytes, got {span.Length}.");
        }
    }
}

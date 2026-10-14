// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using Nethermind.Int256;

namespace Nethermind.Serialization.Ssz.Merkleization;

public static partial class Merkle
{
    public static void Merkleize(out UInt256 root, BitArray value)
    {
        byte[] bytes = new byte[(value.Length + 7) / 8];
        value.CopyTo(bytes, 0);
        Merkleize(out root, bytes);
    }

    public static void Merkleize(out UInt256 root, BitArray value, ulong limit)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((ulong)value.Length, limit, nameof(value));

        ulong chunkCount = limit == 0 ? 0 : (limit - 1) / 256 + 1;
        byte[] bytes = new byte[(value.Length + 7) / 8];
        value.CopyTo(bytes, 0);
        Merkleize(out root, bytes, chunkCount);
        MixIn(ref root, value.Length);
    }
}

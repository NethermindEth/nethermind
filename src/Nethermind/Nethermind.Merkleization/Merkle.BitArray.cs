// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Int256;

namespace Nethermind.Merkleization;

public static partial class Merkle
{
    public static void IzeBitvector(out UInt256 root, BitArray value)
    {
        Merkleizer merkleizer = new Merkleizer(0);
        merkleizer.FeedBitvector(value);
        merkleizer.CalculateRoot(out root);
    }

    public static void IzeBitlist(out UInt256 root, BitArray value, ulong maximumBitlistLength)
    {
        Merkleizer merkleizer = new Merkleizer(0);
        merkleizer.FeedBitlist(value, maximumBitlistLength);
        merkleizer.CalculateRoot(out root);
    }

}

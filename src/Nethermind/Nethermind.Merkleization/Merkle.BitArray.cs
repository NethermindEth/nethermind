// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Int256;

namespace Nethermind.Merkleization;

public static partial class Merkle
{
    public static void Merkleize(out UInt256 root, BitArray value)
    {
        Merkleizer merkleizer = new Merkleizer(0);
        merkleizer.Feed(value);
        merkleizer.CalculateRoot(out root);
    }

    public static void Merkleize(out UInt256 root, BitArray value, ulong limit)
    {
        Merkleizer merkleizer = new Merkleizer(0);
        merkleizer.Feed(value, limit);
        merkleizer.CalculateRoot(out root);
    }

}

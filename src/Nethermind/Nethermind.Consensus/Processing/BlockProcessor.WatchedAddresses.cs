// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor
{
    // Addresses to dump after each processed block (nonce, balance, code, storage).
    private static readonly Address[] WatchedAddresses = new[]
    {
        new Address("0xbabe2bed00000000000000000000000000000003"),
        new Address("0x00000961ef480eb55e80d19ad83579a64c007002"),
        new Address("0x0000bbddc7ce488642fb579f8b00f3a590007251"),
        new Address("0x0000f90827f1c53a10cb7a02335b175320002935"),
        new Address("0x000f3df6d732807ef1319fb7b8bb8522d0beac02"),
        new Address("0x2000000000000000000000000000000000000001"),
        new Address("0xfffffffffffffffffffffffffffffffffffffffe"),
        new Address("0xa94f5374fce5edbc8e2a8697c15331677e6ebf0b"),
        new Address("0x1000000000000000000000000000000000001000"),
        new Address("0x1559000000000000000000000000000000000000")
    };
}

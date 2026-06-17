// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

public class ForkInfoTests
{
    private static readonly Hash256 OpMainnetGenesis = new("0x7ca38a1916c42007829c55e69d3e9a73265554b586a499015373241b8a3fa48b");

    [TestCase(38_950_927, 1_764_691_201ul, "0xd53e568f", 1_783_526_401ul, "OP Mainnet - Jovian")]
    [TestCase(48_000_000, 1_783_526_401ul, "0xc29239af", 0ul, "OP Mainnet - Karst")]
    public void Fork_id_and_hash_as_expected(long head, ulong headTimestamp, string forkHashHex, ulong next, string description) =>
        Network.Test.ForkInfoTests.Test(head, headTimestamp, OpMainnetGenesis, forkHashHex, next, description, "op-mainnet.json.zst");

}

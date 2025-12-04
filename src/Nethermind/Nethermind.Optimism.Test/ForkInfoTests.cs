// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

public class ForkInfoTests
{
    [TestCase(15_051_000, 1_764_691_201ul , "0x1cfeafc9", 0ul, "Jovian timestamp")]
    public void Fork_id_and_hash_as_expected(long head, ulong headTimestamp, string forkHashHex, ulong next, string description)
    {
        Network.Test.ForkInfoTests.Test(head, headTimestamp, KnownHashes.MainnetGenesis, forkHashHex, next, description,  "op-mainnet.json.zst");
    }

}

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

public class ForkInfoTests
{
    private static readonly Hash256 BaseMainnetGenesis = new("0xf712aa9241cc24369b143cf6dce85f0902a9731e70d66818a3a5845b296c73dd");

    [TestCase(38_950_927, 1_764_691_201ul, "0x1cfeafc9", 0ul, "Base Mainnet - Jovian")]
    public void Fork_id_and_hash_as_expected(long head, ulong headTimestamp, string forkHashHex, ulong next, string description)
    {
        Network.Test.ForkInfoTests.Test(head, headTimestamp, BaseMainnetGenesis, forkHashHex, next, description, "base-mainnet.json.zst");
    }

}

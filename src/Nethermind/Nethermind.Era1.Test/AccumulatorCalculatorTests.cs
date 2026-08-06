// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Era1.Test;

public class AccumulatorCalculatorTests
{
    // An independent Python SSZ merkleization (hashlib only) derived these roots:
    // hash_tree_root(EpochRecord) with EpochRecord = List[HeaderRecord, 8192] per EIP-7643.
    public static IEnumerable<TestCaseData> ComputeRootCases()
    {
        yield return new TestCaseData(
            new (Hash256, UInt256)[] { (Keccak.Zero, 1), (Keccak.MaxValue, 2) },
            "0x3ed62652dfb7e1072d0f040feb6d002a9f7ce37cf8ddb16549a7ac5cf8e3b791")
            .SetName($"{nameof(ComputeRoot_MatchesSpecRoot)}(two entries)");
        yield return new TestCaseData(
            new (Hash256, UInt256)[] { (Keccak.Zero, 0) },
            "0x81fd641249670887a731386e756a7a1538dc781b1b0bf016889045d350812817")
            .SetName($"{nameof(ComputeRoot_MatchesSpecRoot)}(single entry)");
    }

    [TestCaseSource(nameof(ComputeRootCases))]
    public void ComputeRoot_MatchesSpecRoot((Hash256 Hash, UInt256 Td)[] entries, string expectedRoot)
    {
        using AccumulatorCalculator sut = new();
        foreach ((Hash256 hash, UInt256 td) in entries)
        {
            sut.Add(hash, td);
        }

        Assert.That(sut.ComputeRoot(), Is.EqualTo(new ValueHash256(expectedRoot)));
    }
}

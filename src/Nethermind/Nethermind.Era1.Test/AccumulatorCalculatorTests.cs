// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Era1.Test;

public class AccumulatorCalculatorTests
{
    // An independent Python SSZ merkleization (hashlib only) derived every root in this file:
    // hash_tree_root(EpochRecord) with EpochRecord = List[HeaderRecord, 8192] per EIP-7643.

    // The root of [(Keccak.Zero, td 1)].
    private const string SingleEntryZeroTd1Root = "0xadd755f5bbbf0768705dad22180e521ef7fad7ee697a9d43f63cd37713b489c6";

    // The single-entry cases differ pairwise in exactly one input, so a root that ignores
    // the hash or the total difficulty cannot match all of them.
    public static IEnumerable<TestCaseData> ComputeRootCases()
    {
        yield return new TestCaseData(
            new (Hash256, UInt256)[] { (Keccak.Zero, 1), (Keccak.MaxValue, 2) },
            "0x3ed62652dfb7e1072d0f040feb6d002a9f7ce37cf8ddb16549a7ac5cf8e3b791")
            .SetName($"{nameof(ComputeRoot_MatchesSpecRoot)}(two entries)");
        yield return new TestCaseData(
            new (Hash256, UInt256)[] { (Keccak.Zero, 1) }, SingleEntryZeroTd1Root)
            .SetName($"{nameof(ComputeRoot_MatchesSpecRoot)}(single zero hash, td 1)");
        yield return new TestCaseData(
            new (Hash256, UInt256)[] { (Keccak.MaxValue, 1) },
            "0x033c473aad051c4d45926b9e621509a5981c49d0b7873697cbe03a0c504df7fa")
            .SetName($"{nameof(ComputeRoot_MatchesSpecRoot)}(single max hash, td 1)");
        yield return new TestCaseData(
            new (Hash256, UInt256)[] { (Keccak.Zero, 100) },
            "0xddabd41f523fab42a8d682b45fd3b6b42f682ed06e67b95971bbb609a061459b")
            .SetName($"{nameof(ComputeRoot_MatchesSpecRoot)}(single zero hash, td 100)");
        yield return new TestCaseData(
            new (Hash256, UInt256)[] { (Keccak.Zero, 0) },
            "0x81fd641249670887a731386e756a7a1538dc781b1b0bf016889045d350812817")
            .SetName($"{nameof(ComputeRoot_MatchesSpecRoot)}(single zero hash, td 0)");
        yield return new TestCaseData(
            Array.Empty<(Hash256, UInt256)>(),
            "0x4a8c3a07c8d23adc5bac61157555c3c784d53d9bc110c1370809bd23cd93777d")
            .SetName($"{nameof(ComputeRoot_MatchesSpecRoot)}(empty)");
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

    [Test]
    public void Clear_ResetsToAFreshAccumulator()
    {
        using AccumulatorCalculator sut = new();
        sut.Add(Keccak.MaxValue, 100);
        sut.Clear();
        sut.Add(Keccak.Zero, 1);

        // EraWriter.Finalize clears the calculator between era files. A lost reset
        // corrupts every accumulator after the first.
        Assert.That(sut.ComputeRoot(), Is.EqualTo(new ValueHash256(SingleEntryZeroTd1Root)));
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using AccumulatorCalculator = Nethermind.Era1.AccumulatorCalculator;
using NUnit.Framework;

namespace Nethermind.EraE.Test.Archive;

public class AccumulatorCalculatorTests
{
    private const int TreeDepth = 13; // log2(8192)

    // Root of [(Keccak.Zero, td 1), (Keccak.MaxValue, td 2)]. Derived with an independent
    // Python SSZ merkleization (hashlib only): hash_tree_root(List[HeaderRecord, 8192])
    // per the portal-network history spec.
    private const string TwoEntryRoot = "0x3ed62652dfb7e1072d0f040feb6d002a9f7ce37cf8ddb16549a7ac5cf8e3b791";

    // Same derivation as TwoEntryRoot. The single-entry cases differ pairwise in exactly
    // one input, so a root that ignores the hash or the total difficulty cannot match all of them.
    public static IEnumerable<TestCaseData> ComputeRootCases()
    {
        yield return new TestCaseData(
            new (Hash256, UInt256)[] { (Keccak.Zero, 1), (Keccak.MaxValue, 2) }, TwoEntryRoot)
            .SetName($"{nameof(ComputeRoot_MatchesSpecRoot)}(two entries)");
        yield return new TestCaseData(
            new (Hash256, UInt256)[] { (Keccak.Zero, 1) },
            "0xadd755f5bbbf0768705dad22180e521ef7fad7ee697a9d43f63cd37713b489c6")
            .SetName($"{nameof(ComputeRoot_MatchesSpecRoot)}(single zero hash, td 1)");
        yield return new TestCaseData(
            new (Hash256, UInt256)[] { (Keccak.MaxValue, 1) },
            "0x033c473aad051c4d45926b9e621509a5981c49d0b7873697cbe03a0c504df7fa")
            .SetName($"{nameof(ComputeRoot_MatchesSpecRoot)}(single max hash, td 1)");
        yield return new TestCaseData(
            new (Hash256, UInt256)[] { (Keccak.Zero, 100) },
            "0xddabd41f523fab42a8d682b45fd3b6b42f682ed06e67b95971bbb609a061459b")
            .SetName($"{nameof(ComputeRoot_MatchesSpecRoot)}(single zero hash, td 100)");
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

    [TestCase(-1, TestName = "negative")]
    [TestCase(1, TestName = "at_count")]
    public void GetProof_WithOutOfRangeIndex_ThrowsArgumentOutOfRangeException(int index)
    {
        using AccumulatorCalculator sut = new();
        sut.Add(Keccak.Zero, 1);

        Assert.That(() => sut.GetProof(index), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void GetProof_WithSingleEntry_ValidatesAgainstSpecRoot()
    {
        using AccumulatorCalculator sut = new();
        sut.Add(Keccak.Zero, 42);

        ValueHash256[] proof = sut.GetProof(0);

        Assert.That(proof, Has.Length.EqualTo(15));
        // Root of [(Keccak.Zero, td 42)], derived like TwoEntryRoot.
        Assert.That(ComputeRootFromProof(Keccak.Zero, proof, 0),
            Is.EqualTo(new ValueHash256("0xa5693bb5c7bbdcb3a65f20ba6e4643535e56c974d6e46262a1c314678bd271c3")));
    }

    [TestCase(0U)]
    [TestCase(1U)]
    [TestCase(7U)]
    public void GetProof_WhenCalled_ProofZeroIsTotalDifficultyLE(uint blockIndex)
    {
        using AccumulatorCalculator sut = new();
        for (uint i = 0; i <= blockIndex; i++)
            sut.Add(Keccak.Zero, i + 1);

        byte[] expected = new byte[32];
        expected[0] = (byte)(blockIndex + 1);
        Assert.That(sut.GetProof((int)blockIndex)[0].ToByteArray(), Is.EqualTo(expected));
    }

    [TestCase(0)]
    [TestCase(1)]
    public void GetProof_WithTwoEntries_EachIndexValidatesAgainstSpecRoot(int blockIndex)
    {
        using AccumulatorCalculator sut = new();
        sut.Add(Keccak.Zero, 1);
        sut.Add(Keccak.MaxValue, 2);

        ValueHash256[] proof = sut.GetProof(blockIndex);

        Hash256 headerHash = blockIndex == 0 ? Keccak.Zero : Keccak.MaxValue;
        Assert.That(ComputeRootFromProof(headerHash, proof, blockIndex),
            Is.EqualTo(new ValueHash256(TwoEntryRoot)));
    }

    /// <summary>
    /// Recomputes the accumulator root from one block's proof.
    /// </summary>
    /// <remarks>
    /// Transcribes the merkle proof verification from the portal-network history spec.
    /// The leaf is sha256(block_hash ++ total_difficulty_le). Each index bit selects the
    /// side of the next tree level. The SSZ list length mixes in last. The transcription
    /// uses SHA256 directly, so the expectation stays independent of the production merkleization.
    /// </remarks>
    private static ValueHash256 ComputeRootFromProof(Hash256 headerHash, ValueHash256[] proof, int blockIndex)
    {
        Span<byte> node = stackalloc byte[32];
        Span<byte> combined = stackalloc byte[64];
        headerHash.Bytes.CopyTo(combined);
        proof[0].Bytes.CopyTo(combined[32..]);
        SHA256.TryHashData(combined, node, out _);

        int index = blockIndex;
        for (int level = 1; level <= TreeDepth; level++)
        {
            if ((index & 1) == 0)
            {
                node.CopyTo(combined);
                proof[level].Bytes.CopyTo(combined[32..]);
            }
            else
            {
                proof[level].Bytes.CopyTo(combined);
                node.CopyTo(combined[32..]);
            }
            SHA256.TryHashData(combined, node, out _);
            index >>= 1;
        }

        node.CopyTo(combined);
        proof[TreeDepth + 1].Bytes.CopyTo(combined[32..]);
        SHA256.TryHashData(combined, node, out _);
        return new ValueHash256(node);
    }
}

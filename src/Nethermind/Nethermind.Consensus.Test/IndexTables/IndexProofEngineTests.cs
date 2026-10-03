// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Nethermind.Consensus.IndexTables;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.IndexTables;

public class IndexProofEngineTests
{
    [Test]
    public void GenerateProof_single_entry_produces_valid_proof()
    {
        List<IndexEntry> entries = [IndexEntry.CreateBlock(TestItem.KeccakA, 0)];

        IndexEntryProof proof = IndexProofEngine.GenerateProof(entries, 0);

        Assert.Multiple(() =>
        {
            Assert.That(proof.Entry.Length, Is.EqualTo(42));
            Assert.That(proof.LeafIndex, Is.EqualTo(0));
            Assert.That(proof.ListLength, Is.EqualTo(1));
            // A single-entry table merkleizes at depth 0, so the length chunk is the only sibling.
            Assert.That(proof.Proof.Length, Is.EqualTo(1));
        });
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(8)]
    [TestCase(17)]
    public void GenerateProof_verifies_against_table_root(int entryCount)
    {
        List<IndexEntry> entries = BuildEntries(entryCount);
        UInt256 expectedRoot = IndexTableRootCalculator.ComputeRoot(entries);

        for (int leafIndex = 0; leafIndex < entryCount; leafIndex++)
        {
            IndexEntryProof proof = IndexProofEngine.GenerateProof(entries, leafIndex);
            Assert.That(ReconstructRoot(proof), Is.EqualTo(expectedRoot), $"leaf {leafIndex} of {entryCount}");
        }
    }

    [Test]
    public void GenerateProofs_matches_proofs_generated_one_at_a_time()
    {
        List<IndexEntry> entries = BuildEntries(9);
        int[] leafIndices = [0, 3, 8];

        IndexEntryProof[] batch = IndexProofEngine.GenerateProofs(entries, leafIndices);

        Assert.That(batch, Has.Length.EqualTo(leafIndices.Length));
        for (int i = 0; i < leafIndices.Length; i++)
        {
            IndexEntryProof single = IndexProofEngine.GenerateProof(entries, leafIndices[i]);
            Assert.Multiple(() =>
            {
                Assert.That(batch[i].Entry, Is.EqualTo(single.Entry));
                Assert.That(batch[i].LeafIndex, Is.EqualTo(single.LeafIndex));
                Assert.That(batch[i].Proof, Is.EqualTo(single.Proof));
                Assert.That(batch[i].ListLength, Is.EqualTo(single.ListLength));
            });
        }
    }

    [Test]
    public void GenerateProofs_returns_empty_for_no_indices() =>
        Assert.That(IndexProofEngine.GenerateProofs(BuildEntries(4), []), Is.Empty);

    [TestCase(1, 0, ExpectedResult = 1024UL)]
    [TestCase(1, 1, ExpectedResult = 1025UL)]
    [TestCase(4, 0, ExpectedResult = 4096UL)]
    [TestCase(4, 4, ExpectedResult = 4097UL)]
    [TestCase(16, 0, ExpectedResult = 16384UL)]
    [TestCase(256, 0, ExpectedResult = 262144UL)]
    public ulong ComputeStorageSlot_matches_spec(int tableSize, long firstBlock)
    {
        UInt256 slot = IndexProofEngine.ComputeStorageSlot(tableSize, firstBlock);
        return (ulong)slot;
    }

    [Test]
    public void ComputeStorageSlot_wraps_at_1024()
    {
        UInt256 slot = IndexProofEngine.ComputeStorageSlot(1, 1024);
        Assert.That((ulong)slot, Is.EqualTo(1024UL));

        UInt256 slot2 = IndexProofEngine.ComputeStorageSlot(1, 1025);
        Assert.That((ulong)slot2, Is.EqualTo(1025UL));
    }

    [Test]
    public void GenerateProof_rejects_invalid_leaf_index()
    {
        List<IndexEntry> entries = [IndexEntry.CreateBlock(TestItem.KeccakA, 0)];

        Assert.Throws<ArgumentOutOfRangeException>(() => IndexProofEngine.GenerateProof(entries, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => IndexProofEngine.GenerateProof(entries, 1));
    }

    [Test]
    public void GenerateProofs_rejects_invalid_leaf_index() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => IndexProofEngine.GenerateProofs(BuildEntries(4), [0, 4]));

    [TestCase(0, -1)]
    [TestCase(1, -1)]
    [TestCase(0, 0)]
    public void ComputeStorageSlot_rejects_values_outside_its_domain(int tableSize, long firstBlock) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => IndexProofEngine.ComputeStorageSlot(tableSize, firstBlock));

    private static List<IndexEntry> BuildEntries(int count)
    {
        List<IndexEntry> entries = new(count);
        for (int i = 0; i < count; i++)
        {
            entries.Add(IndexEntry.CreateTransaction(Keccak.Compute($"tx{i}"), (ulong)i, (uint)i, 0));
        }

        entries.Sort();
        return entries;
    }

    /// <summary>
    /// Replays a proof the way an external verifier would: hash the entry, fold in each sibling,
    /// then pair the tree root with the SSZ length chunk.
    /// </summary>
    private static UInt256 ReconstructRoot(IndexEntryProof proof)
    {
        UInt256 current = new(SHA256.HashData(proof.Entry));

        int idx = proof.LeafIndex;
        for (int i = 0; i < proof.Proof.Length - 1; i++)
        {
            UInt256 sibling = proof.Proof[i];
            current = (idx & 1) == 0
                ? IndexProofEngine.HashPair(current, sibling)
                : IndexProofEngine.HashPair(sibling, current);
            idx >>= 1;
        }

        return IndexProofEngine.HashPair(current, proof.Proof[^1]);
    }
}

// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.Trie.Pruning;
using Nethermind.Trie.Sparse;
using NUnit.Framework;

namespace Nethermind.Trie.Test.Sparse;

/// <summary>
/// Regression tests for specific structural edge cases found during development.
/// Each test verifies sparse trie root matches PatriciaTree for the exact scenario.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class SparseTrieRegressionTests
{
    [TestCase(ProofNodeKind.Leaf, 9, 0, 128)]
    [TestCase(ProofNodeKind.Extension, 65, 0, 128)]
    [TestCase(ProofNodeKind.Branch, 33, 4, 256)]
    [TestCase(ProofNodeKind.Branch, 9, 16, 256)]
    public void RevealNodes_ReservesProofNodeChildCount(
        ProofNodeKind kind,
        int proofNodeCount,
        int branchChildCount,
        int expectedCapacity)
    {
        List<ProofNode> proofNodes = new(proofNodeCount);
        TrieMask childMask = CreateChildMask(branchChildCount);
        for (int i = 0; i < proofNodeCount; i++)
        {
            proofNodes.Add(new ProofNode
            {
                Kind = kind,
                ChildMask = childMask,
                Key = kind == ProofNodeKind.Extension ? [0] : null,
            });
        }

        using SparsePatriciaTree sparse = new();
        sparse.RevealNodes(proofNodes);
        int reservedCapacity = sparse.Subtrie.ChildrenCapacity;

        int childCount = kind switch
        {
            ProofNodeKind.Branch => branchChildCount,
            ProofNodeKind.Extension => 1,
            _ => 0,
        };
        for (int i = 1; i < proofNodes.Count; i++)
            sparse.Subtrie.AllocChildren(childCount);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reservedCapacity, Is.EqualTo(expectedCapacity));
            Assert.That(sparse.Subtrie.ChildrenCapacity, Is.EqualTo(reservedCapacity));
        }
    }

    [Test]
    public void SingleLeaf_DirectSubtrie_MatchesPatricia()
    {
        Hash256 key = TestItem.KeccakA;
        byte[] value = TestItem.GenerateIndexedAccountRlp(1);

        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        tree.Set(key.Bytes, value);
        tree.UpdateRootHash();
        tree.Commit();

        byte[] nibbles = Nibbles.BytesToNibbleBytes(key.Bytes);
        SparseSubtrie subtrie = new();
        subtrie.Root = subtrie.InsertLeaf(nibbles, value);
        RlpNode sparseRlp = subtrie.UpdateCachedRlp();
        Hash256 sparseHash = sparseRlp.IsHash() ? sparseRlp.AsHash() : Keccak.Compute(sparseRlp.AsSpan());

        Assert.That(sparseHash, Is.EqualTo(tree.RootHash));
    }

    [Test]
    public void TwoLeaves_DirectSubtrie_MatchesPatricia()
    {
        Hash256 keyA = TestItem.KeccakA;
        Hash256 keyB = TestItem.KeccakB;
        byte[] valA = TestItem.GenerateIndexedAccountRlp(1);
        byte[] valB = TestItem.GenerateIndexedAccountRlp(2);

        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        tree.Set(keyA.Bytes, valA);
        tree.Set(keyB.Bytes, valB);
        tree.UpdateRootHash();
        tree.Commit();

        byte[] nibA = Nibbles.BytesToNibbleBytes(keyA.Bytes);
        byte[] nibB = Nibbles.BytesToNibbleBytes(keyB.Bytes);

        SparseSubtrie subtrie = new();
        subtrie.Root = subtrie.InsertLeaf(nibA, valA);
        subtrie.UpdateSingleLeaf(nibB, LeafUpdate.Changed(valB), out _);
        RlpNode rootRlp = subtrie.UpdateCachedRlp();
        Hash256 sparseHash = rootRlp.IsHash() ? rootRlp.AsHash() : Keccak.Compute(rootRlp.AsSpan());

        Assert.That(sparseHash, Is.EqualTo(tree.RootHash));
    }

    [Test]
    public void InsertTwoDeleteOne_CollapsesToSingleLeaf()
    {
        Hash256 keyA = TestItem.KeccakA;
        Hash256 keyB = TestItem.KeccakB;
        byte[] valA = TestItem.GenerateIndexedAccountRlp(1);
        byte[] valB = TestItem.GenerateIndexedAccountRlp(2);

        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        tree.Set(keyA.Bytes, valA);
        tree.Set(keyB.Bytes, valB);
        tree.Set(keyB.Bytes, []);
        tree.UpdateRootHash();
        tree.Commit();

        using SparsePatriciaTree sparse = new();
        sparse.UpdateLeaves(new Dictionary<ValueHash256, LeafUpdate>
        {
            [keyA] = LeafUpdate.Changed(valA),
            [keyB] = LeafUpdate.Changed(valB),
        }, null);
        sparse.UpdateLeaves(new Dictionary<ValueHash256, LeafUpdate> { [keyB] = LeafUpdate.Deleted() }, null);
        Hash256 sparseRoot = sparse.ComputeRoot();

        Assert.That(sparseRoot, Is.EqualTo(tree.RootHash));
    }

    [Test]
    public void InsertThreeDeleteOne_MatchesPatricia()
    {
        Hash256[] keys = [TestItem.Keccaks[0], TestItem.Keccaks[1], TestItem.Keccaks[2]];
        byte[][] vals =
        [
            TestItem.GenerateIndexedAccountRlp(0),
            TestItem.GenerateIndexedAccountRlp(1),
            TestItem.GenerateIndexedAccountRlp(2)
        ];

        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        for (int i = 0; i < 3; i++) tree.Set(keys[i].Bytes, vals[i]);
        tree.Set(keys[1].Bytes, []);
        tree.UpdateRootHash();
        tree.Commit();

        using SparsePatriciaTree sparse = new();
        Dictionary<ValueHash256, LeafUpdate> inserts = [];
        for (int i = 0; i < 3; i++) inserts[keys[i]] = LeafUpdate.Changed(vals[i]);
        sparse.UpdateLeaves(inserts, null);

        sparse.UpdateLeaves(new Dictionary<ValueHash256, LeafUpdate> { [keys[1]] = LeafUpdate.Deleted() }, null);
        Hash256 sparseRoot = sparse.ComputeRoot();

        Assert.That(sparseRoot, Is.EqualTo(tree.RootHash));
    }

    [Test]
    public void DirectSubtrie_InsertTwoDeleteOne_CollapsesCorrectly()
    {
        Hash256 keyA = TestItem.KeccakA;
        Hash256 keyB = TestItem.KeccakB;
        byte[] valA = TestItem.GenerateIndexedAccountRlp(1);
        byte[] valB = TestItem.GenerateIndexedAccountRlp(2);

        byte[] nibA = Nibbles.BytesToNibbleBytes(keyA.Bytes);
        byte[] nibB = Nibbles.BytesToNibbleBytes(keyB.Bytes);

        SparseSubtrie subtrie = new();
        subtrie.Root = subtrie.InsertLeaf(nibA, valA);
        subtrie.UpdateSingleLeaf(nibB, LeafUpdate.Changed(valB), out _);
        subtrie.UpdateSingleLeaf(nibB, LeafUpdate.Deleted(), out _);

        RlpNode rlp = subtrie.UpdateCachedRlp();
        Hash256 sparseHash = rlp.IsHash() ? rlp.AsHash() : Keccak.Compute(rlp.AsSpan());

        // Should match single-leaf Patricia trie
        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        tree.Set(keyA.Bytes, valA);
        tree.UpdateRootHash();
        tree.Commit();

        Assert.That(sparseHash, Is.EqualTo(tree.RootHash));
    }

    [TestCase(17)]
    [TestCase(7331)]
    public void ReusedSparseTrie_WithDeletesAndInserts_MatchesPatricia(int seed)
    {
        const int keyCount = 24;
        const int blockCount = 80;
        Random random = new(seed);
        MemDb db = new();
        PatriciaTree patricia = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        Hash256[] keys = CreateKeys(keyCount);
        bool[] present = new bool[keyCount];
        int[] versions = new int[keyCount];

        for (int i = 0; i < keyCount; i++)
        {
            if (i % 3 == 0)
                continue;
            present[i] = true;
            patricia.Set(keys[i].Bytes, CreateValue(i, versions[i]));
        }
        patricia.UpdateRootHash();
        patricia.Commit();

        Hash256 parentRoot = patricia.RootHash;
        HalfPathTrieNodeReader reader = new(new NodeStorage(db));
        using SparseStateTrie sparse = new();

        for (int block = 0; block < blockCount; block++)
        {
            Dictionary<ValueHash256, LeafUpdate> updates = [];
            int operationCount = 1 + random.Next(3);
            string operations = string.Empty;

            for (int operation = 0; operation < operationCount; operation++)
            {
                int keyIndex = random.Next(keyCount);
                ValueHash256 key = keys[keyIndex].ValueHash256;
                if (present[keyIndex] && random.Next(3) == 0)
                {
                    present[keyIndex] = false;
                    patricia.Set(keys[keyIndex].Bytes, []);
                    updates[key] = LeafUpdate.Deleted();
                    operations += $" delete({keyIndex})";
                }
                else
                {
                    present[keyIndex] = true;
                    versions[keyIndex]++;
                    byte[] value = CreateValue(keyIndex, versions[keyIndex]);
                    patricia.Set(keys[keyIndex].Bytes, value);
                    updates[key] = LeafUpdate.Changed(value);
                    operations += $" set({keyIndex},{versions[keyIndex]})";
                }
            }

            patricia.UpdateRootHash();
            patricia.Commit();
            Hash256 expectedRoot = patricia.RootHash;

            using SparseRootComputer computer = new(sparse, reader, parentRoot);
            computer.SetAccountChanges(updates);
            Hash256 actualRoot = computer.ComputeStateRoot();

            Assert.That(
                actualRoot,
                Is.EqualTo(expectedRoot),
                $"seed={seed}, block={block}, parent={parentRoot}, operations:{operations}");
            parentRoot = expectedRoot;
        }
    }

    private static Hash256[] CreateKeys(int count)
    {
        Hash256[] keys = new Hash256[count];
        for (int i = 0; i < count; i++)
        {
            byte[] bytes = new byte[32];
            bytes[28] = (byte)(i / 8);
            bytes[29] = (byte)(i / 4);
            bytes[30] = (byte)(i / 2);
            bytes[31] = (byte)i;
            keys[i] = new Hash256(bytes);
        }
        return keys;
    }

    private static TrieMask CreateChildMask(int childCount)
    {
        TrieMask mask = TrieMask.Empty;
        for (int i = 0; i < childCount; i++)
            mask = mask.SetBit(i);
        return mask;
    }

    private static byte[] CreateValue(int key, int version)
    {
        byte[] value = new byte[40];
        BitConverter.TryWriteBytes(value, key);
        BitConverter.TryWriteBytes(value.AsSpan(sizeof(int)), version);
        value[^1] = 0x7f;
        return value;
    }
}

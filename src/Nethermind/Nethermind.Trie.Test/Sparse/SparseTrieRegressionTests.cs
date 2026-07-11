// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
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
}

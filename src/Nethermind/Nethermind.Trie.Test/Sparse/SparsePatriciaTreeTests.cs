// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using Nethermind.Trie.Sparse;
using NUnit.Framework;

namespace Nethermind.Trie.Test.Sparse;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class SparsePatriciaTreeTests
{
    #region Test Helpers

    private static (PatriciaTree tree, MemDb db) CreatePatriciaTree()
    {
        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        return (tree, db);
    }

    private static Hash256 PatriciaRootAfterOps(Action<PatriciaTree> ops)
    {
        (PatriciaTree tree, _) = CreatePatriciaTree();
        ops(tree);
        tree.UpdateRootHash();
        tree.Commit();
        return tree.RootHash;
    }

    private static Hash256 SparseRootFromDirectInserts(Dictionary<ValueHash256, LeafUpdate> updates)
    {
        using SparsePatriciaTree sparse = new();
        sparse.UpdateLeaves(updates, null);
        return sparse.ComputeRoot();
    }

    private static byte[] MakeValue(int index)
    {
        byte[] value = new byte[32];
        value[0] = (byte)(index + 1);
        value[31] = (byte)(index + 2);
        return value;
    }

    private static Hash256 MakeHash(int index)
    {
        byte[] key = new byte[32];
        key[0] = (byte)(index >> 24);
        key[1] = (byte)(index >> 16);
        key[2] = (byte)(index >> 8);
        key[3] = (byte)index;
        return Keccak.Compute(key);
    }

    #endregion

    #region Category 1: Structural Mutations

    [Test]
    public void InsertIntoEmptyTrie()
    {
        Hash256 key = TestItem.KeccakA;
        byte[] value = TestItem.GenerateIndexedAccountRlp(1);

        Hash256 patriciaRoot = PatriciaRootAfterOps(t => t.Set(key.Bytes, value));
        Hash256 sparseRoot = SparseRootFromDirectInserts(new() { [key] = LeafUpdate.Changed(value) });

        Assert.That(sparseRoot, Is.EqualTo(patriciaRoot));
    }

    [Test]
    public void InsertTwoLeaves_SharedPrefix()
    {
        byte[] valA = MakeValue(1);
        byte[] valB = MakeValue(2);

        Hash256 patriciaRoot = PatriciaRootAfterOps(t =>
        {
            t.Set(TestItem.Keccaks[0].Bytes, valA);
            t.Set(TestItem.Keccaks[1].Bytes, valB);
        });

        Hash256 sparseRoot = SparseRootFromDirectInserts(new()
        {
            [TestItem.Keccaks[0]] = LeafUpdate.Changed(valA),
            [TestItem.Keccaks[1]] = LeafUpdate.Changed(valB),
        });

        Assert.That(sparseRoot, Is.EqualTo(patriciaRoot));
    }

    [Test]
    public void InsertTwoLeaves_NoSharedPrefix()
    {
        Hash256 hashA = TestItem.KeccakA;
        Hash256 hashB = TestItem.KeccakB;
        byte[] valA = MakeValue(10);
        byte[] valB = MakeValue(20);

        Hash256 patriciaRoot = PatriciaRootAfterOps(t =>
        {
            t.Set(hashA.Bytes, valA);
            t.Set(hashB.Bytes, valB);
        });

        Hash256 sparseRoot = SparseRootFromDirectInserts(new()
        {
            [hashA] = LeafUpdate.Changed(valA),
            [hashB] = LeafUpdate.Changed(valB),
        });

        Assert.That(sparseRoot, Is.EqualTo(patriciaRoot));
    }

    [Test]
    public void UpdateExistingLeaf()
    {
        Hash256 key = TestItem.KeccakA;
        byte[] val1 = MakeValue(1);
        byte[] val2 = MakeValue(2);

        Hash256 patriciaRoot = PatriciaRootAfterOps(t =>
        {
            t.Set(key.Bytes, val1);
            t.Set(key.Bytes, val2);
        });

        using SparsePatriciaTree sparse = new();
        sparse.UpdateLeaves(new() { [key] = LeafUpdate.Changed(val1) }, null);
        sparse.UpdateLeaves(new() { [key] = LeafUpdate.Changed(val2) }, null);

        Assert.That(sparse.ComputeRoot(), Is.EqualTo(patriciaRoot));
    }

    [Test]
    public void UpdateExistingLeaf_SameValue()
    {
        Hash256 key = TestItem.KeccakA;
        byte[] value = MakeValue(1);

        Hash256 patriciaRoot = PatriciaRootAfterOps(t => t.Set(key.Bytes, value));

        using SparsePatriciaTree sparse = new();
        sparse.UpdateLeaves(new() { [key] = LeafUpdate.Changed(value) }, null);
        Hash256 root1 = sparse.ComputeRoot();

        sparse.UpdateLeaves(new() { [key] = LeafUpdate.Changed(value) }, null);
        Hash256 root2 = sparse.ComputeRoot();

        Assert.That(root1, Is.EqualTo(patriciaRoot));
        Assert.That(root2, Is.EqualTo(patriciaRoot));
    }

    [Test]
    public void DeleteSingleLeaf()
    {
        Hash256 key = TestItem.KeccakA;
        byte[] value = MakeValue(1);

        using SparsePatriciaTree sparse = new();
        sparse.UpdateLeaves(new() { [key] = LeafUpdate.Changed(value) }, null);
        Assert.That(sparse.ComputeRoot(), Is.Not.EqualTo(Keccak.EmptyTreeHash));

        sparse.UpdateLeaves(new() { [key] = LeafUpdate.Deleted() }, null);
        Assert.That(sparse.ComputeRoot(), Is.EqualTo(Keccak.EmptyTreeHash));
    }

    [Test]
    public void DeleteFromBranch_CollapseToLeaf()
    {
        Hash256 keyA = TestItem.KeccakA;
        Hash256 keyB = TestItem.KeccakB;
        byte[] valA = MakeValue(1);
        byte[] valB = MakeValue(2);

        Hash256 patriciaRoot = PatriciaRootAfterOps(t =>
        {
            t.Set(keyA.Bytes, valA);
            t.Set(keyB.Bytes, valB);
            t.Set(keyB.Bytes, []);
        });

        using SparsePatriciaTree sparse = new();
        sparse.UpdateLeaves(new() { [keyA] = LeafUpdate.Changed(valA), [keyB] = LeafUpdate.Changed(valB) }, null);
        sparse.UpdateLeaves(new() { [keyB] = LeafUpdate.Deleted() }, null);

        Assert.That(sparse.ComputeRoot(), Is.EqualTo(patriciaRoot));
    }

    [Test]
    public void DeleteFromBranch_NoCollapse()
    {
        byte[] valA = MakeValue(1), valB = MakeValue(2), valC = MakeValue(3);

        Hash256 patriciaRoot = PatriciaRootAfterOps(t =>
        {
            t.Set(TestItem.Keccaks[0].Bytes, valA);
            t.Set(TestItem.Keccaks[1].Bytes, valB);
            t.Set(TestItem.Keccaks[2].Bytes, valC);
            t.Set(TestItem.Keccaks[1].Bytes, []);
        });

        using SparsePatriciaTree sparse = new();
        sparse.UpdateLeaves(new()
        {
            [TestItem.Keccaks[0]] = LeafUpdate.Changed(valA),
            [TestItem.Keccaks[1]] = LeafUpdate.Changed(valB),
            [TestItem.Keccaks[2]] = LeafUpdate.Changed(valC),
        }, null);
        sparse.UpdateLeaves(new() { [TestItem.Keccaks[1]] = LeafUpdate.Deleted() }, null);

        Assert.That(sparse.ComputeRoot(), Is.EqualTo(patriciaRoot));
    }

    [Test]
    public void DeleteNonExistentKey()
    {
        using SparsePatriciaTree sparse = new();
        sparse.UpdateLeaves(new() { [TestItem.KeccakA] = LeafUpdate.Changed(MakeValue(1)) }, null);
        Hash256 rootBefore = sparse.ComputeRoot();

        sparse.UpdateLeaves(new() { [TestItem.KeccakB] = LeafUpdate.Deleted() }, null);
        Assert.That(sparse.ComputeRoot(), Is.EqualTo(rootBefore));
    }

    [Test]
    public void EmptyStorageRoot()
    {
        using SparsePatriciaTree sparse = new();
        Assert.That(sparse.ComputeRoot(), Is.EqualTo(Keccak.EmptyTreeHash));
    }

    [Test]
    public void WipeStorage()
    {
        using SparsePatriciaTree sparse = new();
        sparse.UpdateLeaves(new()
        {
            [TestItem.KeccakA] = LeafUpdate.Changed(MakeValue(1)),
            [TestItem.KeccakB] = LeafUpdate.Changed(MakeValue(2)),
        }, null);
        Assert.That(sparse.ComputeRoot(), Is.Not.EqualTo(Keccak.EmptyTreeHash));

        sparse.WipeStorage();
        Assert.That(sparse.ComputeRoot(), Is.EqualTo(Keccak.EmptyTreeHash));
    }

    [Test]
    public void DeleteAll_ReturnsEmptyTreeHash()
    {
        using SparsePatriciaTree sparse = new();
        sparse.UpdateLeaves(new()
        {
            [TestItem.KeccakA] = LeafUpdate.Changed(MakeValue(1)),
            [TestItem.KeccakB] = LeafUpdate.Changed(MakeValue(2)),
            [TestItem.KeccakC] = LeafUpdate.Changed(MakeValue(3)),
        }, null);
        sparse.UpdateLeaves(new()
        {
            [TestItem.KeccakA] = LeafUpdate.Deleted(),
            [TestItem.KeccakB] = LeafUpdate.Deleted(),
            [TestItem.KeccakC] = LeafUpdate.Deleted(),
        }, null);

        Assert.That(sparse.ComputeRoot(), Is.EqualTo(Keccak.EmptyTreeHash));
    }

    [Test]
    public void InsertDeleteInsert_MatchesPatricia()
    {
        Hash256 key = TestItem.KeccakA;
        byte[] val1 = MakeValue(1);
        byte[] val2 = MakeValue(2);

        Hash256 patriciaRoot = PatriciaRootAfterOps(t =>
        {
            t.Set(key.Bytes, val1);
            t.Set(key.Bytes, []);
            t.Set(key.Bytes, val2);
        });

        using SparsePatriciaTree sparse = new();
        sparse.UpdateLeaves(new() { [key] = LeafUpdate.Changed(val1) }, null);
        sparse.UpdateLeaves(new() { [key] = LeafUpdate.Deleted() }, null);
        sparse.UpdateLeaves(new() { [key] = LeafUpdate.Changed(val2) }, null);

        Assert.That(sparse.ComputeRoot(), Is.EqualTo(patriciaRoot));
    }

    [Test]
    public void MultipleInserts_MatchesPatriciaTree()
    {
        int count = 50;

        Hash256 patriciaRoot = PatriciaRootAfterOps(t =>
        {
            for (int i = 0; i < count; i++)
                t.Set(TestItem.Keccaks[i].Bytes, TestItem.GenerateIndexedAccountRlp(i));
        });

        Dictionary<ValueHash256, LeafUpdate> updates = [];
        for (int i = 0; i < count; i++)
            updates[TestItem.Keccaks[i]] = LeafUpdate.Changed(TestItem.GenerateIndexedAccountRlp(i));

        Assert.That(SparseRootFromDirectInserts(updates), Is.EqualTo(patriciaRoot));
    }

    [Test]
    public void ZeroValueDeletion()
    {
        Hash256 key = TestItem.KeccakA;
        byte[] value = MakeValue(1);

        // Patricia treats Set(key, []) as deletion
        Hash256 patriciaRoot = PatriciaRootAfterOps(t =>
        {
            t.Set(key.Bytes, value);
            t.Set(key.Bytes, []);
        });

        using SparsePatriciaTree sparse = new();
        sparse.UpdateLeaves(new() { [key] = LeafUpdate.Changed(value) }, null);
        sparse.UpdateLeaves(new() { [key] = LeafUpdate.Deleted() }, null);

        Assert.That(sparse.ComputeRoot(), Is.EqualTo(patriciaRoot));
        Assert.That(patriciaRoot, Is.EqualTo(Keccak.EmptyTreeHash));
    }

    [Test]
    public void DeleteAccount_CollapseAndRootMatch()
    {
        // Two accounts, delete one via LeafUpdate.Deleted()
        Hash256 keyA = TestItem.KeccakA;
        Hash256 keyB = TestItem.KeccakB;
        byte[] valA = TestItem.GenerateIndexedAccountRlp(1);
        byte[] valB = TestItem.GenerateIndexedAccountRlp(2);

        Hash256 patriciaRoot = PatriciaRootAfterOps(t =>
        {
            t.Set(keyA.Bytes, valA);
            t.Set(keyB.Bytes, valB);
            t.Set(keyA.Bytes, []);
        });

        using SparsePatriciaTree sparse = new();
        sparse.UpdateLeaves(new()
        {
            [keyA] = LeafUpdate.Changed(valA),
            [keyB] = LeafUpdate.Changed(valB),
        }, null);
        sparse.UpdateLeaves(new() { [keyA] = LeafUpdate.Deleted() }, null);

        Assert.That(sparse.ComputeRoot(), Is.EqualTo(patriciaRoot));
    }

    [Test]
    public void MultipleInserts_200_MatchesPatriciaTree()
    {
        int count = 200;

        Hash256 patriciaRoot = PatriciaRootAfterOps(t =>
        {
            for (int i = 0; i < count; i++)
                t.Set(TestItem.Keccaks[i].Bytes, TestItem.GenerateIndexedAccountRlp(i));
        });

        Dictionary<ValueHash256, LeafUpdate> updates = [];
        for (int i = 0; i < count; i++)
            updates[TestItem.Keccaks[i]] = LeafUpdate.Changed(TestItem.GenerateIndexedAccountRlp(i));

        Assert.That(SparseRootFromDirectInserts(updates), Is.EqualTo(patriciaRoot));
    }

    #endregion

    #region Category 2: Proof-based reveal and blinded nodes

    [Test]
    public void RevealSinglePath_VerifyStructure()
    {
        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        tree.Set(TestItem.KeccakA.Bytes, TestItem.GenerateIndexedAccountRlp(1));
        tree.Commit();

        HalfPathTrieNodeReader reader = new(new NodeStorage(db));
        DecodedMultiProof proof = MultiProofReader.ReadAccountProofs(reader, tree.RootHash, [TestItem.KeccakA]);

        using SparsePatriciaTree sparse = new();
        sparse.RevealNodes(proof.AccountNodes);

        Assert.That(sparse.IsRevealed, Is.True);
        Assert.That(proof.AccountNodes, Is.Not.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void UpdateAfterReveal_MatchesPatricia(bool reverseProof)
    {
        // Build a trie with 5 keys, reveal all, update one, compare roots
        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        for (int i = 0; i < 5; i++)
            tree.Set(TestItem.Keccaks[i].Bytes, TestItem.GenerateIndexedAccountRlp(i));
        tree.Commit();

        // Build reference: patricia with the update applied
        Hash256 patriciaRoot = PatriciaRootAfterOps(t =>
        {
            for (int i = 0; i < 5; i++)
                t.Set(TestItem.Keccaks[i].Bytes, TestItem.GenerateIndexedAccountRlp(i));
            t.Set(TestItem.Keccaks[2].Bytes, MakeValue(99));
        });

        // Build sparse from proof, apply same update
        HalfPathTrieNodeReader reader = new(new NodeStorage(db));
        Hash256[] allKeys = new Hash256[5];
        for (int i = 0; i < 5; i++) allKeys[i] = TestItem.Keccaks[i];
        DecodedMultiProof proof = MultiProofReader.ReadAccountProofs(reader, tree.RootHash, allKeys);
        List<ProofNode> proofNodes = [.. proof.AccountNodes];
        if (reverseProof)
            proofNodes.Reverse();
        ProofNode[] originalOrder = [.. proofNodes];

        using SparsePatriciaTree sparse = new();
        sparse.RevealNodes(proofNodes);
        sparse.UpdateLeaves(new() { [TestItem.Keccaks[2]] = LeafUpdate.Changed(MakeValue(99)) }, null);
        Hash256 sparseRoot = sparse.ComputeRoot();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sparseRoot, Is.EqualTo(patriciaRoot));
            Assert.That(proofNodes, Is.EqualTo(originalOrder));
        }
    }

    [Test]
    public void BlindedNodeHit_EmitsProofRequest()
    {
        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        for (int i = 0; i < 10; i++)
            tree.Set(TestItem.Keccaks[i].Bytes, TestItem.GenerateIndexedAccountRlp(i));
        tree.Commit();

        HalfPathTrieNodeReader reader = new(new NodeStorage(db));
        DecodedMultiProof proof = MultiProofReader.ReadAccountProofs(reader, tree.RootHash, [TestItem.Keccaks[0]]);

        using SparsePatriciaTree sparse = new();
        sparse.RevealNodes(proof.AccountNodes);

        List<Hash256> proofRequests = [];
        sparse.UpdateLeaves(
            new() { [TestItem.Keccaks[5]] = LeafUpdate.Changed(MakeValue(99)) },
            (key, _) => proofRequests.Add(key.ToCommitment()));

        Assert.That(proofRequests, Is.Not.Empty, "Updating a key not in the proof should request a proof");
    }

    [Test]
    public void DrainApplicableLeaves_MixedHitAndMiss_DrainsOnlyHit()
    {
        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        for (int i = 0; i < 10; i++)
            tree.Set(TestItem.Keccaks[i].Bytes, TestItem.GenerateIndexedAccountRlp(i));
        tree.Commit();

        byte[] changedValue = MakeValue(99);
        Hash256 expectedRoot = PatriciaRootAfterOps(t =>
        {
            for (int i = 0; i < 10; i++)
                t.Set(TestItem.Keccaks[i].Bytes, TestItem.GenerateIndexedAccountRlp(i));
            t.Set(TestItem.Keccaks[0].Bytes, changedValue);
        });

        HalfPathTrieNodeReader reader = new(new NodeStorage(db));
        DecodedMultiProof proof = MultiProofReader.ReadAccountProofs(reader, tree.RootHash, [TestItem.Keccaks[0]]);
        using SparsePatriciaTree sparse = new();
        sparse.RevealNodes(proof.AccountNodes);

        Dictionary<ValueHash256, LeafUpdate> updates = new()
        {
            [TestItem.Keccaks[0]] = LeafUpdate.Changed(changedValue),
            [TestItem.Keccaks[5]] = LeafUpdate.Changed(MakeValue(100)),
        };
        List<(ValueHash256 Key, byte MinLength)> proofRequests = [];

        sparse.DrainApplicableLeaves(updates, (key, minLength) => proofRequests.Add((key, minLength)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(updates, Has.Count.EqualTo(1));
            Assert.That(updates.ContainsKey(TestItem.Keccaks[0]), Is.False);
            Assert.That(updates.ContainsKey(TestItem.Keccaks[5]), Is.True);
            Assert.That(proofRequests, Has.Count.EqualTo(1));
            Assert.That(proofRequests[0].Key, Is.EqualTo(TestItem.Keccaks[5].ValueHash256));
            Assert.That(proofRequests[0].MinLength, Is.LessThanOrEqualTo(64));
            Assert.That(sparse.ComputeRoot(), Is.EqualTo(expectedRoot));
        }
    }

    [Test]
    public void DrainApplicableLeaves_NoChange_RemovesPendingUpdate()
    {
        ValueHash256 key = TestItem.KeccakA;
        byte[] value = MakeValue(1);
        using SparsePatriciaTree sparse = new();
        sparse.UpdateLeaves(new() { [key] = LeafUpdate.Changed(value) }, null);
        Hash256 root = sparse.ComputeRoot();

        Dictionary<ValueHash256, LeafUpdate> updates = new() { [key] = LeafUpdate.Changed(value) };
        List<ValueHash256> proofRequests = [];

        sparse.DrainApplicableLeaves(updates, (proofKey, _) => proofRequests.Add(proofKey));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(updates, Is.Empty);
            Assert.That(proofRequests, Is.Empty);
            Assert.That(sparse.ComputeRoot(), Is.EqualTo(root));
        }
    }

    [Test]
    public void DrainApplicableLeaves_EmptyTrie_DrainsApplicableUpdates()
    {
        ValueHash256 changedKey = TestItem.KeccakA;
        byte[] value = MakeValue(1);
        Hash256 expectedRoot = PatriciaRootAfterOps(t => t.Set(changedKey.Bytes, value));
        Dictionary<ValueHash256, LeafUpdate> updates = new()
        {
            [changedKey] = LeafUpdate.Changed(value),
            [TestItem.KeccakB] = LeafUpdate.Deleted(),
            [TestItem.KeccakC] = LeafUpdate.Touched(),
        };
        List<ValueHash256> proofRequests = [];
        using SparsePatriciaTree sparse = new();

        sparse.DrainApplicableLeaves(updates, (key, _) => proofRequests.Add(key));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(updates, Is.Empty);
            Assert.That(proofRequests, Is.Empty);
            Assert.That(sparse.ComputeRoot(), Is.EqualTo(expectedRoot));
        }
    }

    [Test]
    public void RevealAndRetry_ProofThenUpdate()
    {
        // Build a trie, reveal subset, hit blinded, reveal more, retry succeeds
        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        for (int i = 0; i < 10; i++)
            tree.Set(TestItem.Keccaks[i].Bytes, TestItem.GenerateIndexedAccountRlp(i));
        tree.Commit();

        HalfPathTrieNodeReader reader = new(new NodeStorage(db));

        // First reveal: only key 0
        DecodedMultiProof proof1 = MultiProofReader.ReadAccountProofs(reader, tree.RootHash, [TestItem.Keccaks[0]]);
        using SparsePatriciaTree sparse = new();
        sparse.RevealNodes(proof1.AccountNodes);

        // Try to update key 5 — should request proof
        List<Hash256> proofRequests = [];
        sparse.UpdateLeaves(
            new() { [TestItem.Keccaks[5]] = LeafUpdate.Changed(MakeValue(99)) },
            (key, _) => proofRequests.Add(key.ToCommitment()));
        Assert.That(proofRequests, Is.Not.Empty);

        // Now reveal the path for key 5
        DecodedMultiProof proof2 = MultiProofReader.ReadAccountProofs(reader, tree.RootHash, [TestItem.Keccaks[5]]);
        sparse.RevealNodes(proof2.AccountNodes);

        // Retry the update — should succeed now
        proofRequests.Clear();
        sparse.UpdateLeaves(
            new() { [TestItem.Keccaks[5]] = LeafUpdate.Changed(MakeValue(99)) },
            (key, _) => proofRequests.Add(key.ToCommitment()));

        Assert.That(proofRequests, Is.Empty, "After revealing the path, the update should succeed without proof requests");
    }

    #endregion

    #region Category 3: Incremental Hashing

    [Test]
    public void ComputeRoot_SingleLeaf()
    {
        Hash256 key = TestItem.KeccakA;
        byte[] value = TestItem.GenerateIndexedAccountRlp(1);

        Hash256 patriciaRoot = PatriciaRootAfterOps(t => t.Set(key.Bytes, value));

        using SparsePatriciaTree sparse = new();
        sparse.UpdateLeaves(new() { [key] = LeafUpdate.Changed(value) }, null);

        Assert.That(sparse.ComputeRoot(), Is.EqualTo(patriciaRoot));
    }

    [Test]
    public void ComputeRoot_IncrementalUpdate()
    {
        Hash256 keyA = TestItem.KeccakA;
        Hash256 keyB = TestItem.KeccakB;
        byte[] valA = MakeValue(1);
        byte[] valB = MakeValue(2);
        byte[] valB2 = MakeValue(3);

        Hash256 patriciaRoot = PatriciaRootAfterOps(t =>
        {
            t.Set(keyA.Bytes, valA);
            t.Set(keyB.Bytes, valB2);
        });

        using SparsePatriciaTree sparse = new();
        sparse.UpdateLeaves(new() { [keyA] = LeafUpdate.Changed(valA), [keyB] = LeafUpdate.Changed(valB) }, null);
        sparse.ComputeRoot(); // first computation caches everything

        sparse.UpdateLeaves(new() { [keyB] = LeafUpdate.Changed(valB2) }, null);
        Assert.That(sparse.ComputeRoot(), Is.EqualTo(patriciaRoot));
    }

    [TestCase(SparsePatriciaTree.ParallelHashDirtyLeafThreshold - 1, true)]
    [TestCase(SparsePatriciaTree.ParallelHashDirtyLeafThreshold, true)]
    [TestCase(SparsePatriciaTree.ParallelHashDirtyLeafThreshold, false)]
    public void ComputeRoot_AroundParallelThreshold_MatchesPatricia(int leafCount, bool allowParallel)
    {
        (PatriciaTree patricia, _) = CreatePatriciaTree();
        Dictionary<ValueHash256, LeafUpdate> updates = [];
        for (int i = 0; i < leafCount; i++)
        {
            Hash256 key = MakeHash(i);
            byte[] value = MakeValue(i);
            patricia.Set(key.Bytes, value);
            updates[key] = LeafUpdate.Changed(value);
        }
        patricia.UpdateRootHash();
        patricia.Commit();

        using SparsePatriciaTree sparse = new();
        sparse.UpdateLeaves(updates, null);
        Assert.That(sparse.Subtrie.NumDirtyLeaves, Is.EqualTo(leafCount));

        Hash256 root = sparse.ComputeRoot(allowParallel);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(patricia.RootHash));
            Assert.That(sparse.Subtrie.NumDirtyLeaves, Is.Zero);
        }
    }

    [Test]
    public void DirtyLeafCount_TracksSplitAndCollapse()
    {
        Hash256 keyA = MakeHash(1);
        Hash256 keyB = MakeHash(2);
        byte[] valueA = MakeValue(1);
        byte[] valueB = MakeValue(2);
        Hash256 expectedRoot = PatriciaRootAfterOps(t => t.Set(keyA.Bytes, valueA));
        using SparsePatriciaTree sparse = new();

        sparse.UpdateLeaves(new() { [keyA] = LeafUpdate.Changed(valueA) }, null);
        Assert.That(sparse.Subtrie.NumDirtyLeaves, Is.EqualTo(1));
        sparse.ComputeRoot();
        Assert.That(sparse.Subtrie.NumDirtyLeaves, Is.Zero);

        sparse.UpdateLeaves(new() { [keyB] = LeafUpdate.Changed(valueB) }, null);
        Assert.That(sparse.Subtrie.NumDirtyLeaves, Is.EqualTo(2));
        sparse.ComputeRoot();
        Assert.That(sparse.Subtrie.NumDirtyLeaves, Is.Zero);

        sparse.UpdateLeaves(new() { [keyB] = LeafUpdate.Deleted() }, null);
        Assert.That(sparse.Subtrie.NumDirtyLeaves, Is.EqualTo(1));
        Assert.That(sparse.ComputeRoot(), Is.EqualTo(expectedRoot));
        Assert.That(sparse.Subtrie.NumDirtyLeaves, Is.Zero);
    }

    #endregion

    #region Category 4: Randomized Comparison

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    [TestCase(7)]
    [TestCase(8)]
    [TestCase(9)]
    public void RandomInsertDeleteCompare_100ops(int seed) =>
        RunRandomComparison(seed, 100);

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void RandomInsertDeleteCompare_1000ops(int seed) =>
        RunRandomComparison(seed, 1000);

    [TestCase(0)]
    [TestCase(1)]
    public void RandomInsertDeleteCompare_10000ops(int seed) =>
        RunRandomComparison(seed, 10000);

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void RandomMultiBlock(int seed)
    {
        Random rng = new(seed);
        (PatriciaTree patricia, _) = CreatePatriciaTree();
        using SparsePatriciaTree sparse = new();
        List<Hash256> allKeys = [];

        for (int block = 0; block < 5; block++)
        {
            int opCount = 20 + rng.Next(30);
            for (int i = 0; i < opCount; i++)
            {
                int op = rng.Next(100);
                if (op < 60 || allKeys.Count == 0)
                {
                    Hash256 key = MakeHash(rng.Next(10000));
                    byte[] value = MakeValue(rng.Next(10000));
                    patricia.Set(key.Bytes, value);
                    sparse.UpdateLeaves(new() { [key] = LeafUpdate.Changed(value) }, null);
                    if (!allKeys.Contains(key)) allKeys.Add(key);
                }
                else if (op < 85)
                {
                    Hash256 key = allKeys[rng.Next(allKeys.Count)];
                    byte[] value = MakeValue(rng.Next(10000));
                    patricia.Set(key.Bytes, value);
                    sparse.UpdateLeaves(new() { [key] = LeafUpdate.Changed(value) }, null);
                }
                else
                {
                    int idx = rng.Next(allKeys.Count);
                    Hash256 key = allKeys[idx];
                    patricia.Set(key.Bytes, []);
                    sparse.UpdateLeaves(new() { [key] = LeafUpdate.Deleted() }, null);
                    allKeys.RemoveAt(idx);
                }
            }

            patricia.UpdateRootHash();
            patricia.Commit();
            Hash256 patriciaRoot = patricia.RootHash;
            Hash256 sparseRoot = sparse.ComputeRoot();
            Assert.That(sparseRoot, Is.EqualTo(patriciaRoot), $"Seed={seed}, block={block}");
        }
    }

    private static void RunRandomComparison(int seed, int opCount)
    {
        Random rng = new(seed);
        (PatriciaTree patricia, _) = CreatePatriciaTree();
        using SparsePatriciaTree sparse = new();
        List<Hash256> insertedKeys = [];

        for (int i = 0; i < opCount; i++)
        {
            int op = rng.Next(100);
            if (op < 50 || insertedKeys.Count == 0)
            {
                Hash256 key = MakeHash(rng.Next(10000));
                byte[] value = MakeValue(rng.Next(10000));
                patricia.Set(key.Bytes, value);
                sparse.UpdateLeaves(new() { [key] = LeafUpdate.Changed(value) }, null);
                if (!insertedKeys.Contains(key)) insertedKeys.Add(key);
            }
            else if (op < 80)
            {
                Hash256 key = insertedKeys[rng.Next(insertedKeys.Count)];
                byte[] value = MakeValue(rng.Next(10000));
                patricia.Set(key.Bytes, value);
                sparse.UpdateLeaves(new() { [key] = LeafUpdate.Changed(value) }, null);
            }
            else
            {
                int idx = rng.Next(insertedKeys.Count);
                Hash256 key = insertedKeys[idx];
                patricia.Set(key.Bytes, []);
                sparse.UpdateLeaves(new() { [key] = LeafUpdate.Deleted() }, null);
                insertedKeys.RemoveAt(idx);
            }
        }

        patricia.UpdateRootHash();
        patricia.Commit();
        Assert.That(sparse.ComputeRoot(), Is.EqualTo(patricia.RootHash), $"Seed={seed}, ops={opCount}");
    }

    #endregion

    #region Category 5: MultiProofReader

    [Test]
    public void ReadAccountProof_SingleKey()
    {
        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        tree.Set(TestItem.KeccakA.Bytes, TestItem.GenerateIndexedAccountRlp(1));
        tree.Commit();

        HalfPathTrieNodeReader reader = new(new NodeStorage(db));
        DecodedMultiProof proof = MultiProofReader.ReadAccountProofs(reader, tree.RootHash, [TestItem.KeccakA]);

        Assert.That(proof.AccountNodes, Is.Not.Empty);
    }

    [Test]
    public void ReadAccountProof_MultipleKeys()
    {
        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        for (int i = 0; i < 10; i++)
            tree.Set(TestItem.Keccaks[i].Bytes, TestItem.GenerateIndexedAccountRlp(i));
        tree.Commit();

        HalfPathTrieNodeReader reader = new(new NodeStorage(db));
        DecodedMultiProof proof = MultiProofReader.ReadAccountProofs(
            reader, tree.RootHash, [TestItem.Keccaks[0], TestItem.Keccaks[5], TestItem.Keccaks[9]]);

        Assert.That(proof.AccountNodes, Is.Not.Empty);
    }

    [Test]
    public void ProofForNonExistentKey()
    {
        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        tree.Set(TestItem.KeccakA.Bytes, TestItem.GenerateIndexedAccountRlp(1));
        tree.Commit();

        HalfPathTrieNodeReader reader = new(new NodeStorage(db));
        DecodedMultiProof proof = MultiProofReader.ReadAccountProofs(reader, tree.RootHash, [TestItem.KeccakB]);

        Assert.That(proof.AccountNodes, Is.Not.Empty);
    }

    [Test]
    public void ReadProof_EmptyTrie()
    {
        HalfPathTrieNodeReader reader = new(new NodeStorage(new MemDb()));
        DecodedMultiProof proof = MultiProofReader.ReadAccountProofs(reader, Keccak.EmptyTreeHash, [TestItem.KeccakA]);

        Assert.That(proof.IsEmpty, Is.True);
    }

    [Test]
    public void ReadProof_HalfPathBackend()
    {
        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        for (int i = 0; i < 5; i++)
            tree.Set(TestItem.Keccaks[i].Bytes, TestItem.GenerateIndexedAccountRlp(i));
        tree.Commit();

        HalfPathTrieNodeReader reader = new(new NodeStorage(db));
        DecodedMultiProof proof = MultiProofReader.ReadAccountProofs(
            reader, tree.RootHash, [TestItem.Keccaks[0], TestItem.Keccaks[2]]);

        Assert.That(proof.AccountNodes, Is.Not.Empty);
    }

    [Test]
    public void ReadProof_MissingNode_HalfPath()
    {
        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        tree.Set(TestItem.KeccakA.Bytes, TestItem.GenerateIndexedAccountRlp(1));
        tree.Commit();

        // Empty DB — root hash won't exist
        HalfPathTrieNodeReader reader = new(new NodeStorage(new MemDb()));
        Assert.Throws<MissingTrieNodeException>(() =>
            MultiProofReader.ReadAccountProofs(reader, tree.RootHash, [TestItem.KeccakA]));
    }

    #endregion

    #region LeafUpdate Type Tests

    [Test]
    public void LeafUpdate_DefaultIsInvalid()
    {
        LeafUpdate update = default;
        Assert.That(update.IsValid, Is.False);
        Assert.That(update.Kind, Is.EqualTo(LeafUpdateKind.None));
    }

    [Test]
    public void LeafUpdate_Changed_RejectsEmpty() =>
        Assert.Throws<ArgumentException>(() => LeafUpdate.Changed([]));

    [Test]
    public void LeafUpdate_Changed_RejectsNull() =>
        Assert.Throws<ArgumentNullException>(() => LeafUpdate.Changed(null!));

    [Test]
    public void LeafUpdate_Deleted_IsValid()
    {
        LeafUpdate update = LeafUpdate.Deleted();
        Assert.That(update.IsValid, Is.True);
        Assert.That(update.IsDelete, Is.True);
        Assert.That(update.Value, Is.Null);
    }

    [Test]
    public void LeafUpdate_Touched_IsValid()
    {
        LeafUpdate update = LeafUpdate.Touched();
        Assert.That(update.IsValid, Is.True);
        Assert.That(update.IsDelete, Is.False);
        Assert.That(update.Kind, Is.EqualTo(LeafUpdateKind.Touched));
    }

    [Test]
    public void ClearedTrie_CanRevealNonEmptyRoot()
    {
        MemDb db = new();
        RawTrieStore store = new(db);
        PatriciaTree tree = new(store.GetTrieStore(null), LimboLogs.Instance);
        tree.Set(TestItem.KeccakA.Bytes, MakeValue(1));
        tree.Set(TestItem.KeccakB.Bytes, MakeValue(2));
        tree.UpdateRootHash();
        tree.Commit();

        byte[] rootRlp = store.LoadRlp(
            address: null,
            TreePath.Empty,
            tree.RootHash,
            Nethermind.Core.ReadFlags.None)!;
        ProofNode rootProof = MultiProofReader.DecodeProofNode(rootRlp, TreePath.Empty);

        using SparsePatriciaTree sparse = new();
        sparse.Clear();
        Assert.That(sparse.Subtrie.IsEmpty, Is.True);

        sparse.RevealNodes([rootProof]);

        Assert.That(sparse.ComputeRoot(), Is.EqualTo(tree.RootHash));
    }

    #endregion
}

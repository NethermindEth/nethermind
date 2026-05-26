// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using Nethermind.Trie.Sparse;
using NUnit.Framework;

namespace Nethermind.Trie.Test.Sparse;

/// <summary>
/// Tests that reproduce the RevealNodes deep-attach bug.
/// The core flow: build a Patricia trie, generate proofs for changed keys,
/// reveal proofs into a sparse trie, apply updates, compute root — must match Patricia.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class SparseRevealTests
{
    [Test]
    public void RevealAndUpdate_SingleKey_MatchesPatricia()
    {
        // Build a Patricia trie with 5 accounts
        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        for (int i = 0; i < 5; i++)
            tree.Set(TestItem.Keccaks[i].Bytes, TestItem.GenerateIndexedAccountRlp(i));
        tree.UpdateRootHash();
        tree.Commit();
        Hash256 originalRoot = tree.RootHash;

        // Now update key[2] with a new value
        byte[] newValue = TestItem.GenerateIndexedAccountRlp(99);
        tree.Set(TestItem.Keccaks[2].Bytes, newValue);
        tree.UpdateRootHash();
        tree.Commit();
        Hash256 patriciaNewRoot = tree.RootHash;

        // Do the same via sparse trie: read proofs, reveal, update, compute root
        HalfPathTrieNodeReader reader = new(new NodeStorage(db));

        // Step 1: Generate multiproof for key[2] from the ORIGINAL root
        DecodedMultiProof proof = MultiProofReader.ReadAccountProofs(
            reader, originalRoot, [TestItem.Keccaks[2]]);

        proof.AccountNodes.Should().NotBeEmpty("Proof should contain at least the root node");

        // Debug: print proof structure
        foreach (ProofNode pn in proof.AccountNodes)
            TestContext.Out.WriteLine($"Proof: path={pn.Path}, kind={pn.Kind}, key={pn.Key?.Length ?? 0}nib, childMask={pn.ChildMask}");

        // Step 2: Reveal proofs into sparse trie
        using SparsePatriciaTree sparse = new();
        sparse.RevealNodes(proof.AccountNodes);

        // Step 3: Apply the update
        Dictionary<Hash256, LeafUpdate> updates = new()
        {
            [TestItem.Keccaks[2]] = LeafUpdate.Changed(newValue)
        };

        List<Hash256> proofTargets = [];
        sparse.UpdateLeaves(updates, (key, _) => proofTargets.Add(key));

        TestContext.Out.WriteLine($"Proof targets after update: {proofTargets.Count}");
        foreach (Hash256 target in proofTargets)
            TestContext.Out.WriteLine($"  Target: {target}");

        // If there are proof targets, we need to reveal more and retry
        while (proofTargets.Count > 0)
        {
            DecodedMultiProof extraProof = MultiProofReader.ReadAccountProofs(
                reader, originalRoot, proofTargets.ToArray());
            sparse.RevealNodes(extraProof.AccountNodes);
            proofTargets.Clear();
            sparse.UpdateLeaves(updates, (key, _) => proofTargets.Add(key));
        }

        // Step 4: Compute root
        Hash256 sparseRoot = sparse.ComputeRoot();

        TestContext.Out.WriteLine($"Patricia new root: {patriciaNewRoot}");
        TestContext.Out.WriteLine($"Sparse root:       {sparseRoot}");
        TestContext.Out.WriteLine($"Original root:     {originalRoot}");

        sparseRoot.Should().Be(patriciaNewRoot, "Sparse trie root after reveal+update must match Patricia");
    }

    [Test]
    public void RevealOnly_NoUpdate_RootMatchesOriginal()
    {
        // Build a trie, generate proofs, reveal into sparse, compute root WITHOUT any updates
        // Root should match the original (all sibling hashes are preserved)
        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        for (int i = 0; i < 5; i++)
            tree.Set(TestItem.Keccaks[i].Bytes, TestItem.GenerateIndexedAccountRlp(i));
        tree.UpdateRootHash();
        tree.Commit();
        Hash256 originalRoot = tree.RootHash;

        // Generate proofs for ALL keys (fully reveal the trie)
        HalfPathTrieNodeReader reader = new(new NodeStorage(db));
        Hash256[] allKeys = new Hash256[5];
        for (int i = 0; i < 5; i++) allKeys[i] = TestItem.Keccaks[i];

        DecodedMultiProof proof = MultiProofReader.ReadAccountProofs(reader, originalRoot, allKeys);

        TestContext.Out.WriteLine($"Proof nodes: {proof.AccountNodes.Count}");
        foreach (ProofNode pn in proof.AccountNodes)
            TestContext.Out.WriteLine($"  path={pn.Path}, kind={pn.Kind}, key={pn.Key?.Length ?? 0}nib, " +
                $"childMask={pn.ChildMask}, childRlps={pn.ChildRlps?.Length ?? 0}");

        // Reveal into sparse trie
        using SparsePatriciaTree sparse = new();
        sparse.RevealNodes(proof.AccountNodes);

        // Compute root WITHOUT any updates — should match original
        Hash256 sparseRoot = sparse.ComputeRoot();

        TestContext.Out.WriteLine($"Original root: {originalRoot}");
        TestContext.Out.WriteLine($"Sparse root:   {sparseRoot}");

        sparseRoot.Should().Be(originalRoot, "Sparse trie root after full reveal (no updates) must match original");
    }

    [Test]
    public void ProofDecoding_BranchHasCorrectChildren()
    {
        // Verify that MultiProofReader correctly decodes branch children
        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        for (int i = 0; i < 10; i++)
            tree.Set(TestItem.Keccaks[i].Bytes, TestItem.GenerateIndexedAccountRlp(i));
        tree.UpdateRootHash();
        tree.Commit();

        HalfPathTrieNodeReader reader = new(new NodeStorage(db));
        DecodedMultiProof proof = MultiProofReader.ReadAccountProofs(
            reader, tree.RootHash, [TestItem.Keccaks[0]]);

        // Root should be a branch with multiple children
        ProofNode root = proof.AccountNodes[0];
        root.Kind.Should().Be(ProofNodeKind.Branch);
        root.ChildMask.CountBits().Should().BeGreaterThan(1, "Root branch should have multiple children");

        TestContext.Out.WriteLine($"Root: kind={root.Kind}, childMask=0x{root.ChildMask.Raw:X4}, " +
            $"children={root.ChildMask.CountBits()}");

        for (int n = 0; n < 16; n++)
        {
            if (!root.ChildMask.IsBitSet(n)) continue;
            RlpNode childRlp = root.ChildRlps![n];
            TestContext.Out.WriteLine($"  Child[{n:X}]: isHash={childRlp.IsHash()}, len={childRlp.Length}");
        }
    }

    [Test]
    public void SparseRootComputer_Flow_MatchesPatricia()
    {
        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        for (int i = 0; i < 5; i++)
            tree.Set(TestItem.Keccaks[i].Bytes, TestItem.GenerateIndexedAccountRlp(i));
        tree.UpdateRootHash();
        tree.Commit();
        Hash256 originalRoot = tree.RootHash;

        byte[] newAccountRlp = TestItem.GenerateIndexedAccountRlp(99);
        tree.Set(TestItem.Keccaks[2].Bytes, newAccountRlp);
        tree.UpdateRootHash();
        tree.Commit();
        Hash256 patriciaNewRoot = tree.RootHash;

        HalfPathTrieNodeReader reader = new(new NodeStorage(db));
        using State.Flat.SparseRootComputer computer = new(reader, originalRoot);

        Dictionary<Hash256, LeafUpdate> accountUpdates = new()
        {
            [TestItem.Keccaks[2]] = LeafUpdate.Changed(newAccountRlp)
        };
        computer.SetAccountChanges(accountUpdates);

        Hash256 sparseRoot = computer.ComputeStateRoot();

        TestContext.Out.WriteLine($"Patricia new root: {patriciaNewRoot}");
        TestContext.Out.WriteLine($"Sparse root:       {sparseRoot}");

        sparseRoot.Should().Be(patriciaNewRoot,
            "SparseRootComputer flow must produce same root as Patricia");
    }

    [Test]
    public void SparseRootComputer_MultipleAccounts_MatchesPatricia()
    {
        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        for (int i = 0; i < 50; i++)
            tree.Set(TestItem.Keccaks[i].Bytes, TestItem.GenerateIndexedAccountRlp(i));
        tree.UpdateRootHash();
        tree.Commit();
        Hash256 originalRoot = tree.RootHash;

        // Update 10 accounts
        Dictionary<Hash256, LeafUpdate> updates = [];
        for (int i = 0; i < 10; i++)
        {
            byte[] newRlp = TestItem.GenerateIndexedAccountRlp(100 + i);
            tree.Set(TestItem.Keccaks[i].Bytes, newRlp);
            updates[TestItem.Keccaks[i]] = LeafUpdate.Changed(newRlp);
        }
        tree.UpdateRootHash();
        tree.Commit();
        Hash256 patriciaNewRoot = tree.RootHash;

        HalfPathTrieNodeReader reader = new(new NodeStorage(db));
        using State.Flat.SparseRootComputer computer = new(reader, originalRoot);
        computer.SetAccountChanges(updates);

        Hash256 sparseRoot = computer.ComputeStateRoot();

        sparseRoot.Should().Be(patriciaNewRoot,
            "SparseRootComputer with 10 account updates must match Patricia");
    }

    [Test]
    public void SparseRootComputer_TwoConsecutiveBlocks_MatchesPatricia()
    {
        // This reproduces the EXPB flow: two consecutive blocks where
        // block 2 updates accounts that exist from block 1.
        MemDb db = new();

        // Block 1: Insert 20 accounts
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        for (int i = 0; i < 20; i++)
            tree.Set(TestItem.Keccaks[i].Bytes, TestItem.GenerateIndexedAccountRlp(i));
        tree.UpdateRootHash();
        tree.Commit();
        Hash256 block1Root = tree.RootHash;

        TestContext.Out.WriteLine($"Block 1 root: {block1Root}");

        // Block 2: Update 5 existing accounts
        byte[][] newRlps = new byte[5][];
        for (int i = 0; i < 5; i++)
        {
            newRlps[i] = TestItem.GenerateIndexedAccountRlp(100 + i);
            tree.Set(TestItem.Keccaks[i].Bytes, newRlps[i]);
        }
        tree.UpdateRootHash();
        tree.Commit();
        Hash256 block2Root = tree.RootHash;

        TestContext.Out.WriteLine($"Block 2 root: {block2Root}");

        // Now reproduce block 2 via SparseRootComputer using block1Root as previous
        HalfPathTrieNodeReader reader = new(new NodeStorage(db));
        using State.Flat.SparseRootComputer computer = new(reader, block1Root);

        Dictionary<Hash256, LeafUpdate> updates = [];
        for (int i = 0; i < 5; i++)
            updates[TestItem.Keccaks[i]] = LeafUpdate.Changed(newRlps[i]);

        computer.SetAccountChanges(updates);
        Hash256 sparseRoot = computer.ComputeStateRoot();

        TestContext.Out.WriteLine($"Sparse root:  {sparseRoot}");

        sparseRoot.Should().Be(block2Root,
            "Two-block SparseRootComputer must match Patricia for block 2");
    }

    [Test]
    public void SparseRootComputer_200Accounts_Update50_MatchesPatricia()
    {
        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        for (int i = 0; i < 200; i++)
            tree.Set(TestItem.Keccaks[i].Bytes, TestItem.GenerateIndexedAccountRlp(i));
        tree.UpdateRootHash();
        tree.Commit();
        Hash256 originalRoot = tree.RootHash;

        Dictionary<Hash256, LeafUpdate> updates = [];
        for (int i = 0; i < 50; i++)
        {
            byte[] newRlp = TestItem.GenerateIndexedAccountRlp(500 + i);
            tree.Set(TestItem.Keccaks[i].Bytes, newRlp);
            updates[TestItem.Keccaks[i]] = LeafUpdate.Changed(newRlp);
        }
        tree.UpdateRootHash();
        tree.Commit();
        Hash256 patriciaNewRoot = tree.RootHash;

        HalfPathTrieNodeReader reader = new(new NodeStorage(db));
        using State.Flat.SparseRootComputer computer = new(reader, originalRoot);
        computer.SetAccountChanges(updates);

        Hash256 sparseRoot = computer.ComputeStateRoot();

        sparseRoot.Should().Be(patriciaNewRoot,
            "SparseRootComputer with 50/200 account updates must match Patricia");
    }
}

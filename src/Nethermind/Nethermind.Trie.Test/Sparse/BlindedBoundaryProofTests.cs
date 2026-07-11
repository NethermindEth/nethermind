// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
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
/// Tests for the blinded-boundary proof reader path. The goal of this path is to
/// avoid root-to-leaf DB walks for targets whose ancestors are already revealed in
/// the sparse trie. The tests below feed targets that hit blinded boundaries in
/// various trie shapes and assert that the resulting roots match Patricia.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class BlindedBoundaryProofTests
{
    /// <summary>
    /// Baseline: trie with several keys, fully revealed root, update one leaf via the
    /// blinded-boundary proof path. Should match Patricia.
    /// </summary>
    [Test]
    public void UpdateLeaf_ViaBlindedBoundary_MatchesPatricia()
    {
        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        for (int i = 0; i < 5; i++)
            tree.Set(TestItem.Keccaks[i].Bytes, TestItem.GenerateIndexedAccountRlp(i));
        tree.UpdateRootHash();
        tree.Commit();
        Hash256 originalRoot = tree.RootHash;

        byte[] newValue = TestItem.GenerateIndexedAccountRlp(99);
        tree.Set(TestItem.Keccaks[2].Bytes, newValue);
        tree.UpdateRootHash();
        tree.Commit();
        Hash256 patriciaNewRoot = tree.RootHash;

        HalfPathTrieNodeReader reader = new(new NodeStorage(db));

        // Step 1: reveal only the root node into the sparse trie.
        DecodedMultiProof rootOnlyProof = MultiProofReader.ReadAccountProofs(
            reader, originalRoot, [TestItem.Keccaks[2]], new byte[] { 0 });
        Assert.That(rootOnlyProof.AccountNodes, Is.Not.Empty);
        using SparsePatriciaTree sparse = new();
        sparse.RevealNodes([rootOnlyProof.AccountNodes[0]]);

        // Step 2: apply the update — should hit a blinded boundary.
        Dictionary<ValueHash256, LeafUpdate> updates = new()
        {
            [TestItem.Keccaks[2]] = LeafUpdate.Changed(newValue)
        };

        const int maxRetries = 8;
        for (int retry = 0; retry < maxRetries; retry++)
        {
            List<Hash256> targets = [];
            sparse.UpdateLeaves(updates, (key, _) => targets.Add(key.ToCommitment()));
            if (targets.Count == 0) break;

            List<MultiProofReader.BlindedProofTarget> blinded = [];
            foreach (Hash256 key in targets)
            {
                byte[] nibbles = Nibbles.BytesToNibbleBytes(key.Bytes);
                bool found = sparse.Subtrie.TryFindBlindedEntryOnPath(
                    nibbles, out TreePath bPath, out RlpNode bRlp, out int _);
                Assert.That(found, Is.True, $"sparse trie should know where blinding starts for {key}");
                blinded.Add(new MultiProofReader.BlindedProofTarget(bPath, bRlp, nibbles));
            }

            DecodedMultiProof proof = MultiProofReader.ReadProofsFromBlinded(reader, null, blinded);
            Assert.That(proof.AccountNodes, Is.Not.Empty, "blinded-boundary proof must return at least the subtrie root");
            sparse.RevealNodes(proof.AccountNodes);
        }

        Hash256 sparseRoot = sparse.ComputeRoot();
        Assert.That(sparseRoot, Is.EqualTo(patriciaNewRoot), $"Blinded-boundary proof read must produce the same root as Patricia. " +
            $"Patricia={patriciaNewRoot}, Sparse={sparseRoot}, OriginalRoot={originalRoot}");
    }

    /// <summary>
    /// Targeted test: trigger the "extension-only" reveal state. A trie where the proof
    /// reader emits an Extension node WITHOUT its inner Branch creates a sparse wrapper
    /// with StateMask=Empty + non-empty ShortKey. The next update needs the inner Branch.
    /// Failure mode: if TryFindBlindedEntryOnPath reports the boundary at post-shortKey
    /// (after consuming the extension key), the proof walker mis-aligns its descent and
    /// fails to fetch the inner branch — producing a wrong root.
    /// </summary>
    [Test]
    public void ExtensionOnlyReveal_ThenUpdate_MatchesPatricia()
    {
        // Build a trie whose root path is an extension over a long shared prefix.
        // Two keys with shared 8+ leading nibbles → root is Extension wrapping a Branch.
        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        Hash256 k0 = new("0x0000000000000000aabbccddeeff00112233445566778899aabbccddeeff0011");
        Hash256 k1 = new("0x0000000000000000aabbccddeeff00112233445566778899aabbccddeeff0022");
        Hash256 k2 = new("0x0000000000000000aabbccddeeff00112233445566778899aabbccddeeff0033");
        tree.Set(k0.Bytes, TestItem.GenerateIndexedAccountRlp(0));
        tree.Set(k1.Bytes, TestItem.GenerateIndexedAccountRlp(1));
        tree.Set(k2.Bytes, TestItem.GenerateIndexedAccountRlp(2));
        tree.UpdateRootHash();
        tree.Commit();
        Hash256 originalRoot = tree.RootHash;

        // Update k0
        byte[] newValue = TestItem.GenerateIndexedAccountRlp(99);
        tree.Set(k0.Bytes, newValue);
        tree.UpdateRootHash();
        tree.Commit();
        Hash256 patriciaNewRoot = tree.RootHash;

        HalfPathTrieNodeReader reader = new(new NodeStorage(db));

        // Reveal ONLY the root extension wrapper (do not reveal its inner branch).
        // This creates the extension-only state in the sparse trie.
        DecodedMultiProof rootOnly = MultiProofReader.ReadAccountProofs(
            reader, originalRoot, [k0], new byte[] { 0 });
        Assert.That(rootOnly.AccountNodes, Is.Not.Empty);
        using SparsePatriciaTree sparse = new();
        // Reveal ONLY the first node (the extension wrapper). Skip the inner branch.
        sparse.RevealNodes([rootOnly.AccountNodes[0]]);

        Dictionary<ValueHash256, LeafUpdate> updates = new()
        {
            [k0] = LeafUpdate.Changed(newValue)
        };

        const int maxRetries = 8;
        for (int retry = 0; retry < maxRetries; retry++)
        {
            List<Hash256> targets = [];
            sparse.UpdateLeaves(updates, (key, _) => targets.Add(key.ToCommitment()));
            if (targets.Count == 0) break;

            List<MultiProofReader.BlindedProofTarget> blinded = [];
            foreach (Hash256 key in targets)
            {
                byte[] nibbles = Nibbles.BytesToNibbleBytes(key.Bytes);
                bool found = sparse.Subtrie.TryFindBlindedEntryOnPath(
                    nibbles, out TreePath bPath, out RlpNode bRlp, out int _);
                Assert.That(found, Is.True, $"sparse trie should report blinded boundary for {key}");
                blinded.Add(new MultiProofReader.BlindedProofTarget(bPath, bRlp, nibbles));
            }

            DecodedMultiProof proof = MultiProofReader.ReadProofsFromBlinded(reader, null, blinded);
            sparse.RevealNodes(proof.AccountNodes);
        }

        Hash256 sparseRoot = sparse.ComputeRoot();
        Assert.That(sparseRoot, Is.EqualTo(patriciaNewRoot), $"Extension-only reveal should walk inner branch correctly. " +
            $"Patricia={patriciaNewRoot}, Sparse={sparseRoot}");
    }

    /// <summary>
    /// Companion to ExtensionOnlyReveal_ThenUpdate_MatchesPatricia, but for DELETE. A delete
    /// whose target path matches the extension's shortKey in full has to descend INTO the
    /// inner branch to find (and remove) the leaf. UpdateAtBranch must request a proof in
    /// that case â€” the empty StateMask below would otherwise turn the delete into a silent
    /// no-op via the "StateMask.IsBitSet(nibble) == false" branch, and the deleted key would
    /// linger in the sparse root forever.
    /// </summary>
    [Test]
    public void ExtensionOnlyReveal_ThenDelete_MatchesPatricia()
    {
        // Same shape as the update test: three keys sharing 8+ leading nibbles produce a root
        // Extension wrapping a Branch.
        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        Hash256 k0 = new("0x0000000000000000aabbccddeeff00112233445566778899aabbccddeeff0011");
        Hash256 k1 = new("0x0000000000000000aabbccddeeff00112233445566778899aabbccddeeff0022");
        Hash256 k2 = new("0x0000000000000000aabbccddeeff00112233445566778899aabbccddeeff0033");
        tree.Set(k0.Bytes, TestItem.GenerateIndexedAccountRlp(0));
        tree.Set(k1.Bytes, TestItem.GenerateIndexedAccountRlp(1));
        tree.Set(k2.Bytes, TestItem.GenerateIndexedAccountRlp(2));
        tree.UpdateRootHash();
        tree.Commit();
        Hash256 originalRoot = tree.RootHash;

        // Patricia: delete k0
        tree.Set(k0.Bytes, []);
        tree.UpdateRootHash();
        tree.Commit();
        Hash256 patriciaNewRoot = tree.RootHash;

        HalfPathTrieNodeReader reader = new(new NodeStorage(db));
        DecodedMultiProof rootOnly = MultiProofReader.ReadAccountProofs(
            reader, originalRoot, [k0], new byte[] { 0 });
        Assert.That(rootOnly.AccountNodes, Is.Not.Empty);
        using SparsePatriciaTree sparse = new();
        sparse.RevealNodes([rootOnly.AccountNodes[0]]);

        Dictionary<ValueHash256, LeafUpdate> updates = new() { [k0] = LeafUpdate.Deleted() };

        const int maxRetries = 8;
        int retries = 0;
        for (int retry = 0; retry < maxRetries; retry++)
        {
            List<Hash256> targets = [];
            sparse.UpdateLeaves(updates, (key, _) => targets.Add(key.ToCommitment()));
            if (targets.Count == 0) break;

            List<MultiProofReader.BlindedProofTarget> blinded = [];
            foreach (Hash256 key in targets)
            {
                byte[] nibbles = Nibbles.BytesToNibbleBytes(key.Bytes);
                if (sparse.Subtrie.TryFindBlindedEntryOnPath(nibbles, out TreePath bPath, out RlpNode bRlp, out int _))
                    blinded.Add(new MultiProofReader.BlindedProofTarget(bPath, bRlp, nibbles));
            }
            if (blinded.Count == 0) break;
            DecodedMultiProof proof = MultiProofReader.ReadProofsFromBlinded(reader, null, blinded);
            sparse.RevealNodes(proof.AccountNodes);
            retries = retry + 1;
        }

        Assert.That(retries, Is.GreaterThan(0), "delete through extension-only must request a proof; if the pre-check let it fall " +
            "through to NoChange, retries would stay 0 and sparseRoot would equal originalRoot");

        Hash256 sparseRoot = sparse.ComputeRoot();
        Assert.That(sparseRoot, Is.EqualTo(patriciaNewRoot), $"Extension-only delete should match Patricia. Patricia={patriciaNewRoot}, Sparse={sparseRoot}");
        Assert.That(sparseRoot, Is.Not.EqualTo(originalRoot), "if delete was silently dropped, sparse would still produce the original root");
    }

    [Test]
    public void ExtensionOnlyReveal_ThenTouch_RequestsProof()
    {
        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        Hash256 k0 = new("0x0000000000000000aabbccddeeff00112233445566778899aabbccddeeff0011");
        Hash256 k1 = new("0x0000000000000000aabbccddeeff00112233445566778899aabbccddeeff0022");
        Hash256 k2 = new("0x0000000000000000aabbccddeeff00112233445566778899aabbccddeeff0033");
        tree.Set(k0.Bytes, TestItem.GenerateIndexedAccountRlp(0));
        tree.Set(k1.Bytes, TestItem.GenerateIndexedAccountRlp(1));
        tree.Set(k2.Bytes, TestItem.GenerateIndexedAccountRlp(2));
        tree.UpdateRootHash();
        tree.Commit();

        HalfPathTrieNodeReader reader = new(new NodeStorage(db));
        DecodedMultiProof proof = MultiProofReader.ReadAccountProofs(
            reader,
            tree.RootHash,
            [k0],
            new byte[] { 0 });
        Assert.That(proof.AccountNodes[0].Kind, Is.EqualTo(ProofNodeKind.Extension));

        using SparsePatriciaTree sparse = new();
        sparse.RevealNodes([proof.AccountNodes[0]]);
        List<ValueHash256> targets = [];
        sparse.UpdateLeaves(
            new Dictionary<ValueHash256, LeafUpdate> { [k0] = LeafUpdate.Touched() },
            (key, _) => targets.Add(key));

        Assert.That(targets, Is.EqualTo(new[] { k0.ValueHash256 }));
    }

    /// <summary>
    /// Multi-block cross-reuse: simulate a sparse trie that has been used for several
    /// consecutive blocks (always retaining revealed structure). For each block's update,
    /// proof reads go through the blinded-boundary path. Roots must match Patricia at
    /// every step.
    /// </summary>
    [Test]
    public void MultiBlockReuse_BlindedBoundaryReads_MatchPatriciaEveryBlock()
    {
        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);

        // Initial state with 20 accounts to give branching + extension structure.
        for (int i = 0; i < 20; i++)
            tree.Set(TestItem.Keccaks[i].Bytes, TestItem.GenerateIndexedAccountRlp(i));
        tree.UpdateRootHash();
        tree.Commit();

        HalfPathTrieNodeReader reader = new(new NodeStorage(db));
        using SparsePatriciaTree sparse = new();

        // Cold reveal: load just the root into sparse.
        Hash256 currentRoot = tree.RootHash;
        DecodedMultiProof rootProof = MultiProofReader.ReadAccountProofs(
            reader, currentRoot, [TestItem.Keccaks[0]], new byte[] { 0 });
        sparse.RevealNodes([rootProof.AccountNodes[0]]);

        for (int block = 1; block <= 8; block++)
        {
            // Block changes: update a different account each block.
            int idx = block % 20;
            byte[] newValue = TestItem.GenerateIndexedAccountRlp(100 + block);
            tree.Set(TestItem.Keccaks[idx].Bytes, newValue);
            tree.UpdateRootHash();
            tree.Commit();
            Hash256 patriciaRoot = tree.RootHash;

            // Update sparse via the blinded-boundary proof path.
            Dictionary<ValueHash256, LeafUpdate> updates = new()
            {
                [TestItem.Keccaks[idx]] = LeafUpdate.Changed(newValue)
            };
            const int maxRetries = 8;
            for (int retry = 0; retry < maxRetries; retry++)
            {
                List<Hash256> targets = [];
                sparse.UpdateLeaves(updates, (key, _) => targets.Add(key.ToCommitment()));
                if (targets.Count == 0) break;

                List<MultiProofReader.BlindedProofTarget> blinded = [];
                foreach (Hash256 key in targets)
                {
                    byte[] nibbles = Nibbles.BytesToNibbleBytes(key.Bytes);
                    bool found = sparse.Subtrie.TryFindBlindedEntryOnPath(
                        nibbles, out TreePath bPath, out RlpNode bRlp, out int _);
                    Assert.That(found, Is.True, $"block {block}, retry {retry}: sparse trie should know where blinding starts for {key}");
                    blinded.Add(new MultiProofReader.BlindedProofTarget(bPath, bRlp, nibbles));
                }

                DecodedMultiProof proof = MultiProofReader.ReadProofsFromBlinded(reader, null, blinded);
                Assert.That(proof.AccountNodes, Is.Not.Empty, $"block {block}, retry {retry}: blinded-boundary proof must return nodes");
                sparse.RevealNodes(proof.AccountNodes);
            }

            Hash256 sparseRoot = sparse.ComputeRoot();
            Assert.That(sparseRoot, Is.EqualTo(patriciaRoot), $"Block {block} root mismatch: Patricia={patriciaRoot}, Sparse={sparseRoot}");

            currentRoot = patriciaRoot;
        }
    }
}

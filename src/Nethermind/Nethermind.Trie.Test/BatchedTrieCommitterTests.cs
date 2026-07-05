// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class BatchedTrieCommitterTests
{
    private static readonly IKeccakBatchHasher Hasher = new PerMessageKeccakBatchHasher();

    /// <summary>Builds a fresh uncommitted trie from the given key/value pairs; nodes stay dirty (never persisted).</summary>
    private static PatriciaTree BuildTrie(IReadOnlyList<(byte[] key, byte[] value)> entries)
    {
        // Simplest in-memory tree ctor: dirty RootRef, no commit, no scoping needed.
        PatriciaTree tree = new(new MemDb());
        foreach ((byte[] key, byte[] value) in entries)
        {
            tree.Set(key, value);
        }

        return tree;
    }

    /// <summary>Recursive baseline root for the same entries.</summary>
    private static Hash256 RecursiveRoot(IReadOnlyList<(byte[] key, byte[] value)> entries)
    {
        PatriciaTree tree = BuildTrie(entries);
        tree.UpdateRootHash();
        return tree.RootHash;
    }

    /// <summary>Batched root for the same entries (built on a separate tree - hashing mutates node state).</summary>
    private static Hash256 BatchedRoot(IReadOnlyList<(byte[] key, byte[] value)> entries)
    {
        PatriciaTree tree = BuildTrie(entries);
        BatchedTrieCommitter.UpdateRootHashBatched(tree, Hasher);
        return tree.RootHash;
    }

    private static void AssertRootsMatch(IReadOnlyList<(byte[] key, byte[] value)> entries)
    {
        Hash256 expected = RecursiveRoot(entries);
        Hash256 actual = BatchedRoot(entries);
        Assert.That(actual, Is.EqualTo(expected));
    }

    /// <summary>Commits <paramref name="entries"/> to a fresh store and returns the store plus the committed root.</summary>
    private static (ITrieStore store, Hash256 root) CommitToStore(IReadOnlyList<(byte[] key, byte[] value)> entries)
    {
        ITrieStore store = TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance);
        PatriciaTree tree = new(store.GetTrieStore(null), LimboLogs.Instance);
        foreach ((byte[] key, byte[] value) in entries)
        {
            tree.Set(key, value);
        }

        using (store.BeginBlockCommit(0)) { tree.Commit(); }

        return (store, tree.RootHash);
    }

    /// <summary>Re-resolves a committed root from the store into a fresh tree (nodes read from the store, RLP retained).</summary>
    private static PatriciaTree Reopen(ITrieStore store, Hash256 root)
    {
        PatriciaTree tree = new(store.GetTrieStore(null), LimboLogs.Instance);
        tree.RootHash = root;
        return tree;
    }

    [Test]
    public void T5_1_Differential_fuzz_matches_recursive_root()
    {
        const int iterations = 500;
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            // Deterministic per-iteration seed so a failing case is reproducible from the printed seed alone.
            int seed = unchecked(0x5EED_0000 + iteration);
            List<(byte[] key, byte[] value)> entries = GenerateRandomEntries(seed);

            // Print BEFORE the batched call so a hang/throw still leaves the seed on record.
            TestContext.Out.WriteLine($"[T5.1] iteration {iteration} seed 0x{seed:X8} keys {entries.Count}");

            Hash256 expected = RecursiveRoot(entries);
            Hash256 actual = BatchedRoot(entries);
            Assert.That(actual, Is.EqualTo(expected), $"seed 0x{seed:X8} ({entries.Count} keys)");
        }
    }

    private static List<(byte[] key, byte[] value)> GenerateRandomEntries(int seed)
    {
        Random rng = new(seed);
        int count = rng.Next(1, 3001); // 1..3000 keys
        Dictionary<string, byte[]> byKey = new(count);
        for (int i = 0; i < count; i++)
        {
            // 32-byte keys (state/storage key width); random so structure varies (branches, extensions, leaves).
            byte[] key = new byte[32];
            rng.NextBytes(key);

            // Values 1..64 bytes; short values (1..31) produce <32-byte leaf RLP, exercising the inline rule.
            int valueLength = rng.Next(1, 65);
            byte[] value = new byte[valueLength];
            rng.NextBytes(value);
            // Leading zero byte is legal RLP content; keep as-is to widen coverage.
            byKey[Convert.ToHexString(key)] = value;
        }

        List<(byte[] key, byte[] value)> entries = new(byKey.Count);
        foreach (KeyValuePair<string, byte[]> kv in byKey)
        {
            entries.Add((Convert.FromHexString(kv.Key), kv.Value));
        }

        return entries;
    }

    [Test]
    public void T5_2_Single_leaf_trie()
    {
        byte[] key = new byte[32];
        key[0] = 0xAB;
        AssertRootsMatch([(key, [0x01, 0x02, 0x03])]);
    }

    [Test]
    public void T5_2_Single_leaf_trie_short_value_inline_leaf_root_still_hashed()
    {
        // Root is always hashed even though a 1-byte value gives a <32-byte leaf RLP.
        byte[] key = new byte[32];
        key[31] = 0x07;
        AssertRootsMatch([(key, [0x42])]);
    }

    [Test]
    public void T5_3_Deep_extension_chains()
    {
        // Keys sharing a long common prefix force long extension nodes above the branch.
        List<(byte[] key, byte[] value)> entries = [];
        for (int i = 0; i < 4; i++)
        {
            byte[] key = new byte[32];
            key[31] = (byte)i; // differ only in the final nibble pair -> shared 62-nibble prefix
            entries.Add((key, [(byte)(0x80 + i), 0x01]));
        }

        AssertRootsMatch(entries);
    }

    [Test]
    public void T5_4_Branch_with_all_inline_children()
    {
        // Two keys diverging at the first nibble, each with a short value -> a branch whose two child
        // leaves have <32-byte RLP and are spliced inline (Keccak stays null on them).
        byte[] keyA = new byte[1];
        keyA[0] = 0x0A;
        byte[] keyB = new byte[1];
        keyB[0] = 0xB0;
        AssertRootsMatch([(keyA, [0x01]), (keyB, [0x02])]);
    }

    [Test]
    public void T5_5_Empty_trie()
    {
        Hash256 expected = RecursiveRoot([]);
        PatriciaTree tree = BuildTrie([]);
        BatchedTrieCommitter.UpdateRootHashBatched(tree, Hasher);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.RootHash, Is.EqualTo(expected));
            Assert.That(tree.RootHash, Is.EqualTo(PatriciaTree.EmptyTreeHash));
        }
    }

    [Test]
    public void T5_6_Committed_then_one_key_mutated_matches_recursive()
    {
        // Regression: a committed branch that is partially re-dirtied keeps its pre-mutation RLP while its non-traversed
        // sibling slots stay null. Those siblings' refs live in the retained RLP and must be spliced out, not emitted as
        // the absent-child 0x80 byte. This shape is unreachable by an uncommitted-tree build, so it needs a real store.
        List<(byte[] key, byte[] value)> entries = [];
        for (int i = 0; i < 8; i++)
        {
            byte[] key = new byte[32];
            key[0] = (byte)(i * 0x11); // spread across the first nibble -> a wide branch at/near the root
            key[31] = (byte)i;
            entries.Add((key, MakeValue(0x40 + i, 40))); // >=32B leaf RLP so children are keccak refs, not inline
        }

        (ITrieStore store, Hash256 root) = CommitToStore(entries);

        // Mutate exactly one existing key on two independent re-resolved trees, one per hashing path.
        byte[] mutateKey = entries[3].key;
        byte[] newValue = MakeValue(0xEE, 40);

        PatriciaTree recursiveTree = Reopen(store, root);
        recursiveTree.Set(mutateKey, newValue);
        recursiveTree.UpdateRootHash();

        PatriciaTree batchedTree = Reopen(store, root);
        batchedTree.Set(mutateKey, newValue);
        BatchedTrieCommitter.UpdateRootHashBatched(batchedTree, Hasher);

        Assert.That(batchedTree.RootHash, Is.EqualTo(recursiveTree.RootHash));
    }

    [Test]
    public void T5_7_Clean_sibling_contributes_existing_keccak()
    {
        // After re-resolving a committed trie and dirtying one leaf, a sibling that gets resolved as a clean TrieNode
        // (Keccak set, IsDirty false) must contribute its existing keccak with zero work - not be re-encoded.
        List<(byte[] key, byte[] value)> entries = [];
        for (int i = 0; i < 4; i++)
        {
            byte[] key = new byte[32];
            key[0] = (byte)(i * 0x40);
            key[31] = (byte)i;
            entries.Add((key, MakeValue(0x50 + i, 40)));
        }

        (ITrieStore store, Hash256 root) = CommitToStore(entries);

        byte[] mutateKey = entries[0].key;
        byte[] newValue = MakeValue(0xCD, 40);

        PatriciaTree recursiveTree = Reopen(store, root);
        // Touch a sibling's value via Get so it materializes as a clean resolved node before mutation.
        recursiveTree.Get(entries[1].key);
        recursiveTree.Set(mutateKey, newValue);
        recursiveTree.UpdateRootHash();

        PatriciaTree batchedTree = Reopen(store, root);
        batchedTree.Get(entries[1].key);
        batchedTree.Set(mutateKey, newValue);
        BatchedTrieCommitter.UpdateRootHashBatched(batchedTree, Hasher);

        Assert.That(batchedTree.RootHash, Is.EqualTo(recursiveTree.RootHash));
    }

    [Test]
    public void T5_9_Committed_inline_child_ref_spliced_from_retained_rlp()
    {
        // Pins the INLINE-ref splice branch (the keccak-ref splice is already covered by T5.6/T5.8, but 32-byte keys
        // never produce an inline committed child). Short keys (1 byte -> 2 nibbles) with 1-byte values make each child
        // leaf's RLP <32 bytes, so it is stored INLINE inside the root branch's RLP. Enough distinct first nibbles keep
        // the branch itself >=32 bytes, so it persists as a keccak node. Mutating one key leaves the other children's
        // inline refs as reference-null slots on the re-resolved branch, which must be spliced from its retained RLP.
        List<(byte[] key, byte[] value)> entries = [];
        for (int i = 0; i < 12; i++)
        {
            entries.Add(([(byte)((i << 4) | i)], [(byte)(0x01 + i)])); // first nibble = i (distinct), tiny value
        }

        (ITrieStore store, Hash256 root) = CommitToStore(entries);

        byte[] mutateKey = entries[5].key;
        byte[] newValue = [0x7F];

        PatriciaTree recursiveTree = Reopen(store, root);
        recursiveTree.Set(mutateKey, newValue);
        recursiveTree.UpdateRootHash();

        PatriciaTree batchedTree = Reopen(store, root);
        batchedTree.Set(mutateKey, newValue);
        BatchedTrieCommitter.UpdateRootHashBatched(batchedTree, Hasher);

        Assert.That(batchedTree.RootHash, Is.EqualTo(recursiveTree.RootHash));
    }

    [Test]
    public void T5_8_Differential_fuzz_committed_then_mutated_matches_recursive()
    {
        // Second fuzz mode: build, commit, re-resolve, then mutate a random subset (updates + deletes) and add new keys.
        // Exercises retained-RLP null siblings and Hash256 child refs across many shapes.
        const int iterations = 200;
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            int seed = unchecked(0x0DED_0000 + iteration);
            List<(byte[] key, byte[] value)> initial = GenerateRandomEntries(seed);

            TestContext.Out.WriteLine($"[T5.8] iteration {iteration} seed 0x{seed:X8} keys {initial.Count}");

            (ITrieStore store, Hash256 root) = CommitToStore(initial);
            List<(byte[] key, byte[] value)> mutations = GenerateMutations(seed, initial);

            PatriciaTree recursiveTree = Reopen(store, root);
            foreach ((byte[] key, byte[] value) in mutations) recursiveTree.Set(key, value);
            recursiveTree.UpdateRootHash();

            PatriciaTree batchedTree = Reopen(store, root);
            foreach ((byte[] key, byte[] value) in mutations) batchedTree.Set(key, value);
            BatchedTrieCommitter.UpdateRootHashBatched(batchedTree, Hasher);

            Assert.That(batchedTree.RootHash, Is.EqualTo(recursiveTree.RootHash),
                $"seed 0x{seed:X8} ({initial.Count} initial, {mutations.Count} mutations)");
        }
    }

    private static List<(byte[] key, byte[] value)> GenerateMutations(int seed, List<(byte[] key, byte[] value)> initial)
    {
        Random rng = new(unchecked(seed + 0x1111));
        List<(byte[] key, byte[] value)> mutations = [];

        // Update or delete a random subset of existing keys.
        foreach ((byte[] key, byte[] _) in initial)
        {
            int roll = rng.Next(3);
            if (roll == 0)
            {
                byte[] value = new byte[rng.Next(1, 65)];
                rng.NextBytes(value);
                mutations.Add((key, value)); // update
            }
            else if (roll == 1)
            {
                mutations.Add((key, [])); // delete
            }
            // roll == 2: leave unchanged
        }

        // Add some brand-new keys.
        int adds = rng.Next(0, 50);
        for (int i = 0; i < adds; i++)
        {
            byte[] key = new byte[32];
            rng.NextBytes(key);
            byte[] value = new byte[rng.Next(1, 65)];
            rng.NextBytes(value);
            mutations.Add((key, value));
        }

        return mutations;
    }

    private static byte[] MakeValue(int firstByte, int length)
    {
        byte[] value = new byte[length];
        value[0] = (byte)firstByte;
        return value;
    }

    // ---- merged cross-trie wave (UpdateRootHashesBatched) ------------------------------------------------------

    [Test]
    // Empty list must not throw and must do nothing.
    public void MergedWave_empty_list_is_noop() =>
        Assert.DoesNotThrow(() => BatchedTrieCommitter.UpdateRootHashesBatched([], Hasher));

    [Test]
    public void MergedWave_single_tree_equals_single_tree_path()
    {
        // A single tree through the merged wave must yield the exact root the single-tree path produces.
        List<(byte[] key, byte[] value)> entries = GenerateRandomEntries(0x11111111);

        Hash256 singlePath = BatchedRoot(entries);

        PatriciaTree merged = BuildTrie(entries);
        BatchedTrieCommitter.UpdateRootHashesBatched([merged], Hasher);

        Assert.That(merged.RootHash, Is.EqualTo(singlePath));
    }

    [Test]
    public void MergedWave_multiple_fresh_tries_each_match_recursive()
    {
        // M random fresh tries of varied sizes hashed in one merged wave; each root must equal its recursive root.
        const int treeCount = 12;
        List<(byte[] key, byte[] value)>[] entriesPerTree = new List<(byte[], byte[])>[treeCount];
        Hash256[] expected = new Hash256[treeCount];
        PatriciaTree[] trees = new PatriciaTree[treeCount];
        for (int t = 0; t < treeCount; t++)
        {
            entriesPerTree[t] = GenerateRandomEntries(unchecked(0x7A3E_0000 + t));
            expected[t] = RecursiveRoot(entriesPerTree[t]);
            trees[t] = BuildTrie(entriesPerTree[t]);
        }

        BatchedTrieCommitter.UpdateRootHashesBatched(trees, Hasher);

        using (Assert.EnterMultipleScope())
        {
            for (int t = 0; t < treeCount; t++)
            {
                Assert.That(trees[t].RootHash, Is.EqualTo(expected[t]), $"tree {t} ({entriesPerTree[t].Count} keys)");
            }
        }
    }

    [Test]
    public void MergedWave_tries_of_very_different_depths()
    {
        // Trees of deliberately different depths (1 key vs deep-extension vs wide-branch) exercise the shrinking wave.
        List<(byte[] key, byte[] value)> tiny = [(SizedKey(0xAB, 0x01), [0x01])];

        List<(byte[] key, byte[] value)> deep = [];
        for (int i = 0; i < 4; i++) deep.Add((SizedKey(0x11, (byte)i), MakeValue(0x80 + i, 40)));

        List<(byte[] key, byte[] value)> wide = [];
        for (int i = 0; i < 30; i++) wide.Add((SizedKey((byte)(i * 8), (byte)i), MakeValue(0x50 + i, 40)));

        AssertMergedMatchesRecursive([tiny, deep, wide]);
    }

    [Test]
    public void MergedWave_all_inline_tiny_tries()
    {
        // Several all-inline tiny tries in one wave: each root is still hashed (root-always-hashed per tree).
        List<(byte[] key, byte[] value)> a = [([0x0A], [0x01]), ([0xB0], [0x02])];
        List<(byte[] key, byte[] value)> b = [([0x1C], [0x03])];
        List<(byte[] key, byte[] value)> c = [([0x2D], [0x04]), ([0xE5], [0x05])];

        AssertMergedMatchesRecursive([a, b, c]);
    }

    [Test]
    public void MergedWave_clean_root_tree_is_skipped_and_others_hashed()
    {
        // One tree whose re-resolved root is clean (no mutation) is published untouched; a mutated sibling still hashes.
        List<(byte[] key, byte[] value)> cleanEntries = [];
        for (int i = 0; i < 6; i++) cleanEntries.Add((SizedKey((byte)(i * 0x20), (byte)i), MakeValue(0x30 + i, 40)));
        (ITrieStore cleanStore, Hash256 cleanRoot) = CommitToStore(cleanEntries);
        PatriciaTree cleanTree = Reopen(cleanStore, cleanRoot); // never mutated -> root stays clean

        List<(byte[] key, byte[] value)> mutatedEntries = GenerateRandomEntries(0x2222_2222);
        Hash256 mutatedExpected = RecursiveRoot(mutatedEntries);
        PatriciaTree mutatedTree = BuildTrie(mutatedEntries);

        BatchedTrieCommitter.UpdateRootHashesBatched([cleanTree, mutatedTree], Hasher);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleanTree.RootHash, Is.EqualTo(cleanRoot), "clean tree root must be unchanged");
            Assert.That(mutatedTree.RootHash, Is.EqualTo(mutatedExpected), "mutated tree root must match recursive");
        }
    }

    [Test]
    public void MergedWave_committed_then_mutated_retained_rlp_across_multiple_trees()
    {
        // The retained-RLP null-sibling splice shape, in several trees at once, hashed in one merged wave.
        const int treeCount = 5;
        PatriciaTree[] recursiveTrees = new PatriciaTree[treeCount];
        PatriciaTree[] batchedTrees = new PatriciaTree[treeCount];
        for (int t = 0; t < treeCount; t++)
        {
            List<(byte[] key, byte[] value)> entries = [];
            for (int i = 0; i < 8; i++)
            {
                byte[] key = new byte[32];
                key[0] = (byte)(i * 0x11);
                key[31] = (byte)i;
                entries.Add((key, MakeValue(0x40 + i + t, 40)));
            }

            (ITrieStore store, Hash256 root) = CommitToStore(entries);
            byte[] mutateKey = entries[3].key;
            byte[] newValue = MakeValue(0xEE - t, 40);

            recursiveTrees[t] = Reopen(store, root);
            recursiveTrees[t].Set(mutateKey, newValue);
            recursiveTrees[t].UpdateRootHash();

            batchedTrees[t] = Reopen(store, root);
            batchedTrees[t].Set(mutateKey, newValue);
        }

        BatchedTrieCommitter.UpdateRootHashesBatched(batchedTrees, Hasher);

        using (Assert.EnterMultipleScope())
        {
            for (int t = 0; t < treeCount; t++)
            {
                Assert.That(batchedTrees[t].RootHash, Is.EqualTo(recursiveTrees[t].RootHash), $"tree {t}");
            }
        }
    }

    [Test]
    public void MergedWave_emptied_tree_interleaved_with_deep_and_wide_tries()
    {
        // Real block shape: a storage trie whose every slot is DELETED collapses to EmptyTreeHash (RootRef null), and
        // must be interleaved safely in the wave bookkeeping alongside a deep and a wide trie. Each root must match its
        // recursive counterpart, and the emptied trie must equal the empty-tree hash.
        List<(byte[] key, byte[] value)> emptiedEntries = [];
        for (int i = 0; i < 6; i++) emptiedEntries.Add((SizedKey((byte)(i * 0x22), (byte)i), MakeValue(0x40 + i, 40)));
        (ITrieStore emptiedStore, Hash256 emptiedRoot) = CommitToStore(emptiedEntries);

        // Delete every slot on independent re-resolved trees, one per hashing path -> RootRef collapses to null.
        PatriciaTree recursiveEmptied = Reopen(emptiedStore, emptiedRoot);
        foreach ((byte[] key, byte[] _) in emptiedEntries) recursiveEmptied.Set(key, []);
        recursiveEmptied.UpdateRootHash();

        PatriciaTree batchedEmptied = Reopen(emptiedStore, emptiedRoot);
        foreach ((byte[] key, byte[] _) in emptiedEntries) batchedEmptied.Set(key, []);

        List<(byte[] key, byte[] value)> deepEntries = [];
        for (int i = 0; i < 4; i++) deepEntries.Add((SizedKey(0x11, (byte)i), MakeValue(0x80 + i, 40)));
        List<(byte[] key, byte[] value)> wideEntries = [];
        for (int i = 0; i < 30; i++) wideEntries.Add((SizedKey((byte)(i * 8), (byte)i), MakeValue(0x50 + i, 40)));

        Hash256 deepExpected = RecursiveRoot(deepEntries);
        Hash256 wideExpected = RecursiveRoot(wideEntries);
        PatriciaTree deepTree = BuildTrie(deepEntries);
        PatriciaTree wideTree = BuildTrie(wideEntries);

        BatchedTrieCommitter.UpdateRootHashesBatched([batchedEmptied, deepTree, wideTree], Hasher);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(batchedEmptied.RootHash, Is.EqualTo(recursiveEmptied.RootHash), "emptied trie");
            Assert.That(batchedEmptied.RootHash, Is.EqualTo(PatriciaTree.EmptyTreeHash), "emptied trie must be the empty-tree hash");
            Assert.That(deepTree.RootHash, Is.EqualTo(deepExpected), "deep trie");
            Assert.That(wideTree.RootHash, Is.EqualTo(wideExpected), "wide trie");
        }
    }

    [Test]
    public void MergedWave_step0_reaches_full_leaf_width()
    {
        // The observability seam: step 0 must batch every tree's leaf level at once (the wide-dispatch property that
        // makes the merged wave worthwhile). With M single-leaf tries, step 0 must see M messages (each root hashed).
        const int treeCount = 50;
        PatriciaTree[] trees = new PatriciaTree[treeCount];
        for (int t = 0; t < treeCount; t++)
        {
            trees[t] = BuildTrie([(SizedKey((byte)t, (byte)(t + 1)), MakeValue(0x60 + (t % 16), 40))]);
        }

        List<(int step, int width)> steps = [];
        BatchedTrieCommitter.UpdateRootHashesBatched(trees, Hasher, (step, width) => steps.Add((step, width)));

        Assert.That(steps, Is.Not.Empty);
        Assert.That(steps[0].step, Is.EqualTo(0));
        Assert.That(steps[0].width, Is.EqualTo(treeCount), "step 0 must batch every tree's root leaf together");
    }

    private static byte[] SizedKey(byte first, byte last)
    {
        byte[] key = new byte[32];
        key[0] = first;
        key[31] = last;
        return key;
    }

    /// <summary>Asserts that hashing all trees in one merged wave yields, per tree, the recursive root of the same entries.</summary>
    private static void AssertMergedMatchesRecursive(IReadOnlyList<List<(byte[] key, byte[] value)>> entriesPerTree)
    {
        Hash256[] expected = new Hash256[entriesPerTree.Count];
        PatriciaTree[] trees = new PatriciaTree[entriesPerTree.Count];
        for (int t = 0; t < entriesPerTree.Count; t++)
        {
            expected[t] = RecursiveRoot(entriesPerTree[t]);
            trees[t] = BuildTrie(entriesPerTree[t]);
        }

        BatchedTrieCommitter.UpdateRootHashesBatched(trees, Hasher);

        using (Assert.EnterMultipleScope())
        {
            for (int t = 0; t < trees.Length; t++)
            {
                Assert.That(trees[t].RootHash, Is.EqualTo(expected[t]), $"tree {t}");
            }
        }
    }
}

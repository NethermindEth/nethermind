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
}

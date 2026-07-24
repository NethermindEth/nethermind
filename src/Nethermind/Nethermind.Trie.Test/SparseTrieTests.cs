// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using Nethermind.Trie.Sparse;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

[TestFixture]
public class SparseTrieTests
{
    private sealed class NodeStorageSource(NodeStorage storage) : ISparseTrieNodeSource
    {
        public HashSet<ValueHash256> Corrupted { get; } = [];
        public HashSet<ValueHash256> Missing { get; } = [];
        public int ResolveCalls { get; private set; }
        public int ResolvedNodes { get; private set; }

        public void Resolve(ReadOnlySpan<SparseNodeRequest> requests, Span<CappedArray<byte>> results)
        {
            ResolveCalls++;
            for (int i = 0; i < requests.Length; i++)
            {
                if (Missing.Contains(requests[i].Hash))
                {
                    results[i] = CappedArray<byte>.Null;
                    continue;
                }

                byte[]? rlp = storage.Get(null, requests[i].Path, requests[i].Hash.ToCommitment());
                if (rlp is not null)
                {
                    if (Corrupted.Contains(requests[i].Hash))
                    {
                        rlp = (byte[])rlp.Clone();
                        rlp[^1] ^= 0xFF;
                    }
                    else if (ValueKeccak.Compute(rlp) != requests[i].Hash)
                    {
                        throw new TrieException($"Node hash mismatch at {requests[i].Path}");
                    }

                    ResolvedNodes++;
                }

                results[i] = rlp is null ? CappedArray<byte>.Null : rlp;
            }
        }
    }

    // ---------------- scenario harness ----------------

    private static ValueHash256 K(string hexPrefix) => new(hexPrefix.PadRight(64, '0'));

    private static byte[] V(int seed, int length)
    {
        byte[] value = new byte[length];
        for (int i = 0; i < length; i++)
        {
            value[i] = (byte)(0x11 + seed * 7 + i);
        }

        return value;
    }

    // Differential harness for an internal trie data structure: the reference and the system
    // under test are constructed directly (as the other trie unit tests do) because production
    // DI wires world-state machinery this component must stay independent of.
    private static (NodeStorage Storage, Hash256 Root) BuildPatricia(IReadOnlyList<(ValueHash256 Key, byte[] Value)> entries)
    {
        NodeStorage storage = new(new MemDb());
        PatriciaTree tree = new(new RawScopedTrieStore(storage), Keccak.EmptyTreeHash, true, NullLogManager.Instance);
        foreach ((ValueHash256 key, byte[] value) in entries)
        {
            tree.Set(key.Bytes, value);
        }

        tree.Commit();
        return (storage, tree.RootHash);
    }

    /// <summary>
    /// Applies the updates through both implementations over the same committed parents and
    /// asserts identical roots and identical persisted (path, hash, RLP) node sets.
    /// </summary>
    private static void AssertMatchesPatricia(
        IReadOnlyList<(ValueHash256 Key, byte[] Value)> parentEntries,
        IReadOnlyList<(ValueHash256 Key, byte[]? Value)> updates,
        bool canBeParallel = false)
    {
        (NodeStorage storage, Hash256 parentRoot) = BuildPatricia(parentEntries);

        TrieRootFixture.RecordingTrieStore recorder = new(new RawScopedTrieStore(storage));
        PatriciaTree patricia = new(recorder, parentRoot, true, NullLogManager.Instance);
        foreach ((ValueHash256 key, byte[]? value) in updates)
        {
            patricia.Set(key.Bytes, value ?? []);
        }

        patricia.Commit();

        NodeStorageSource source = new(storage);
        using SparseTrie sparse = new(source, parentRoot.ValueHash256);
        SparseTrieUpdate[] sparseUpdates = new SparseTrieUpdate[updates.Count];
        for (int i = 0; i < updates.Count; i++)
        {
            sparseUpdates[i] = new SparseTrieUpdate(updates[i].Key, updates[i].Value);
        }

        sparse.Apply(sparseUpdates);
        ValueHash256 sparseRoot = sparse.CalculateRoot(canBeParallel);

        Assert.That(sparseRoot, Is.EqualTo(patricia.RootHash.ValueHash256), "root");
        AssertSameNodes(recorder.Committed, DrainStaged(sparse));
    }

    [TestCase("", 256)]
    [TestCase("abc0", 256)]
    [TestCase("abc0", 1)]
    public void Parallel_root_encoding_matches_patricia(string sharedPrefix, int updateCount)
    {
        List<(ValueHash256 Key, byte[]? Value)> updates = new(updateCount);
        for (int i = 0; i < updateCount; i++)
        {
            updates.Add((new ValueHash256($"{sharedPrefix}{i:x2}".PadRight(64, '0')), V(i, 32)));
        }

        AssertMatchesPatricia([], updates, canBeParallel: true);
    }

    private static List<SparseTrieStagedNode> DrainStaged(SparseTrie sparse)
    {
        using ArrayPoolList<SparseTrieStagedNode> drained = new(16);
        sparse.DrainUnpublished(drained);
        List<SparseTrieStagedNode> result = [];
        foreach (SparseTrieStagedNode node in drained.AsSpan())
        {
            result.Add(node);
        }

        return result;
    }

    /// <summary>
    /// Asserts the staged set equals the Patricia-committed set. When
    /// <paramref name="committedStore"/> is given (multi-generation runs), staged extras are
    /// permitted only if byte-identical to a node already committed at the same path — the
    /// harmless over-publication a structural restore across intermediate roots can produce.
    /// </summary>
    private static void AssertSameNodes(IReadOnlyList<TrieRootFixture.PersistedTrieNode> expected, IReadOnlyList<SparseTrieStagedNode> actual, NodeStorage? committedStore = null)
    {
        HashSet<(TreePath, ValueHash256, string)> expectedSet = [];
        foreach (TrieRootFixture.PersistedTrieNode node in expected)
        {
            expectedSet.Add((node.Path, node.Hash.ValueHash256, Convert.ToHexString(node.Rlp)));
        }

        HashSet<(TreePath, ValueHash256, string)> actualSet = [];
        foreach (SparseTrieStagedNode node in actual)
        {
            actualSet.Add((node.Path, node.Hash, Convert.ToHexString(node.Rlp)));
        }

        System.Text.StringBuilder detail = new();
        foreach ((TreePath, ValueHash256, string) item in actualSet)
        {
            if (!expectedSet.Contains(item))
            {
                byte[]? committed = committedStore?.Get(null, item.Item1, item.Item2.ToCommitment());
                if (committed is not null && Convert.ToHexString(committed) == item.Item3)
                {
                    continue; // restored node identical to its committed original
                }

                detail.AppendLine($"EXTRA path={item.Item1} len={item.Item1.Length} hash={item.Item2} rlp={item.Item3}");
            }
        }

        foreach ((TreePath, ValueHash256, string) item in expectedSet)
        {
            if (!actualSet.Contains(item))
            {
                detail.AppendLine($"MISSING path={item.Item1} len={item.Item1.Length} hash={item.Item2} rlp={item.Item3}");
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actualSet.Count, Is.EqualTo(actual.Count), "duplicate staged nodes");
            Assert.That(detail.Length, Is.Zero,
                $"staged nodes differ: {actual.Count} staged vs {expected.Count} expected;\n{detail}");
        }
    }

    private static int IntersectCount(HashSet<(TreePath, ValueHash256, string)> a, HashSet<(TreePath, ValueHash256, string)> b)
    {
        int count = 0;
        foreach ((TreePath, ValueHash256, string) item in a)
        {
            if (b.Contains(item))
            {
                count++;
            }
        }

        return count;
    }

    // ---------------- canonical shape scenarios ----------------

    private static IEnumerable<TestCaseData> ShapeScenarios()
    {
        byte[] v1 = V(1, 20);
        byte[] v2 = V(2, 20);
        byte[] small = V(3, 1);

        yield return new TestCaseData(
            Array.Empty<(ValueHash256, byte[])>(),
            new (ValueHash256, byte[]?)[] { (K("11"), v1) }).SetName("insert_into_empty");

        yield return new TestCaseData(
            new[] { (K("11"), v1) },
            new (ValueHash256, byte[]?)[] { (K("11"), v1) }).SetName("identical_update_is_noop");

        yield return new TestCaseData(
            new[] { (K("11"), v1) },
            new (ValueHash256, byte[]?)[] { (K("11"), v2) }).SetName("leaf_value_update");

        // Divergence at nibbles 0, 1, 31, 62, 63.
        foreach (int position in (int[])[0, 1, 31, 62, 63])
        {
            string baseKey = new('a', 64);
            char[] diverged = baseKey.ToCharArray();
            diverged[position] = 'b';
            yield return new TestCaseData(
                new[] { (new ValueHash256(baseKey), v1) },
                new (ValueHash256, byte[]?)[] { (new ValueHash256(new string(diverged)), v2) }).SetName($"divergence_at_nibble_{position}");
        }

        // Full 16-child branch, then update one child.
        (ValueHash256, byte[])[] fullBranch = new (ValueHash256, byte[])[16];
        for (int i = 0; i < 16; i++)
        {
            fullBranch[i] = (K(i.ToString("x")), V(i, 20));
        }

        yield return new TestCaseData(fullBranch, new (ValueHash256, byte[]?)[] { (K("7"), v2) }).SetName("full_branch_update");
        yield return new TestCaseData(fullBranch, new (ValueHash256, byte[]?)[] { (K("7"), null) }).SetName("full_branch_delete_one");

        // Alternating branch mask.
        (ValueHash256, byte[])[] alternating = new (ValueHash256, byte[])[8];
        for (int i = 0; i < 8; i++)
        {
            alternating[i] = (K((i * 2).ToString("x")), V(i, 20));
        }

        yield return new TestCaseData(alternating, new (ValueHash256, byte[]?)[] { (K("3"), v1), (K("5"), v2) }).SetName("alternating_branch_inserts");

        // Long extension: split it at the start, middle, end.
        (ValueHash256, byte[])[] longExt =
        [
            (new ValueHash256("ab".PadRight(40, 'c').PadRight(64, '1')), v1),
            (new ValueHash256("ab".PadRight(40, 'c').PadRight(64, '2')), v2),
        ];

        yield return new TestCaseData(longExt, new (ValueHash256, byte[]?)[] { (K("ff"), v1) }).SetName("extension_split_at_start");
        yield return new TestCaseData(longExt, new (ValueHash256, byte[]?)[] { (new ValueHash256("ab".PadRight(20, 'c').PadRight(64, 'f')), v1) }).SetName("extension_split_in_middle");
        yield return new TestCaseData(longExt, new (ValueHash256, byte[]?)[] { (new ValueHash256("ab".PadRight(39, 'c').PadRight(64, 'f')), v1) }).SetName("extension_split_at_end");

        // Inline boundary: single-byte values under deep divergence produce sub-32-byte leaves.
        (ValueHash256, byte[])[] inlineLeaves =
        [
            (new ValueHash256(new string('a', 63) + "0"), small),
            (new ValueHash256(new string('a', 63) + "1"), V(4, 1)),
        ];

        yield return new TestCaseData(inlineLeaves, new (ValueHash256, byte[]?)[] { (new ValueHash256(new string('a', 63) + "2"), V(5, 1)) }).SetName("inline_leaf_insert");
        yield return new TestCaseData(inlineLeaves, new (ValueHash256, byte[]?)[] { (new ValueHash256(new string('a', 63) + "0"), V(6, 25)) }).SetName("inline_leaf_grows_to_hashed");

        // Account-sized values (56..255 bytes) use the long-form RLP string (0xB8): exercise the
        // revealed-leaf decode and re-encode of that form via update, insert-split, and delete.
        (ValueHash256, byte[])[] longValues = [(K("1a"), V(1, 70)), (K("2b"), V(2, 90))];
        yield return new TestCaseData(longValues, new (ValueHash256, byte[]?)[] { (K("1a"), V(3, 80)) }).SetName("long_form_value_update");
        yield return new TestCaseData(longValues, new (ValueHash256, byte[]?)[] { (K("1b"), V(4, 60)) }).SetName("long_form_value_insert_split");
        yield return new TestCaseData(longValues, new (ValueHash256, byte[]?)[] { (K("1a"), null) }).SetName("long_form_value_delete_collapse");

        // Exact inline/hashed RLP boundary: with a 1-nibble suffix leaf, value lengths 27-29
        // produce node RLP of 30/31/32 bytes, crossing the embed-vs-hash threshold.
        foreach (int valueLength in (int[])[26, 27, 28, 29, 30])
        {
            (ValueHash256, byte[])[] boundary =
            [
                (new ValueHash256(new string('b', 63) + "0"), V(1, valueLength)),
                (new ValueHash256(new string('b', 63) + "1"), V(2, valueLength)),
            ];

            yield return new TestCaseData(boundary, new (ValueHash256, byte[]?)[]
            {
                (new ValueHash256(new string('b', 63) + "0"), V(3, valueLength)),
            }).SetName($"inline_boundary_value_{valueLength}");
        }

        // Collapse shapes: two-child branch loses one; survivor is a leaf / extension / branch.
        (ValueHash256, byte[])[] twoLeaves = [(K("1a"), v1), (K("2b"), v2)];
        yield return new TestCaseData(twoLeaves, new (ValueHash256, byte[]?)[] { (K("1a"), null) }).SetName("collapse_survivor_leaf");

        (ValueHash256, byte[])[] survivorExtension =
        [
            (K("1a"), v1),
            (new ValueHash256("2bb".PadRight(64, '1')), v1),
            (new ValueHash256("2bb".PadRight(64, '2')), v2),
        ];

        yield return new TestCaseData(survivorExtension, new (ValueHash256, byte[]?)[] { (K("1a"), null) }).SetName("collapse_survivor_extension");

        (ValueHash256, byte[])[] survivorBranch =
        [
            (K("1a"), v1),
            (new ValueHash256("2b1".PadRight(64, '0')), v1),
            (new ValueHash256("2b2".PadRight(64, '0')), v2),
        ];

        yield return new TestCaseData(survivorBranch, new (ValueHash256, byte[]?)[] { (K("1a"), null) }).SetName("collapse_survivor_branch");

        // Extension merge after a mid-trie collapse.
        (ValueHash256, byte[])[] extMerge =
        [
            (new ValueHash256("aaa1".PadRight(64, '0')), v1),
            (new ValueHash256("aaa2".PadRight(64, '0')), v2),
            (new ValueHash256("aaa2".PadRight(64, '1')), v1),
        ];

        yield return new TestCaseData(extMerge, new (ValueHash256, byte[]?)[] { (new ValueHash256("aaa1".PadRight(64, '0')), null) }).SetName("collapse_merges_extensions");

        // Deletes reducing to a single leaf, then to empty.
        yield return new TestCaseData(twoLeaves, new (ValueHash256, byte[]?)[] { (K("1a"), null), (K("2b"), null) }).SetName("delete_to_empty");
        yield return new TestCaseData(twoLeaves, new (ValueHash256, byte[]?)[] { (K("3c"), null) }).SetName("absent_delete_is_noop");

        // Same-batch create and delete of different keys.
        yield return new TestCaseData(twoLeaves, new (ValueHash256, byte[]?)[] { (K("3c"), v1), (K("1a"), null) }).SetName("same_batch_create_and_delete");

        // A big mixed batch over a shared-prefix cluster.
        (ValueHash256, byte[])[] cluster = new (ValueHash256, byte[])[12];
        for (int i = 0; i < 12; i++)
        {
            cluster[i] = (new ValueHash256($"abcd{i:x}".PadRight(64, '9')), V(i, 20));
        }

        yield return new TestCaseData(cluster, new (ValueHash256, byte[]?)[]
        {
            (new ValueHash256("abcd0".PadRight(64, '9')), null),
            (new ValueHash256("abcd1".PadRight(64, '9')), V(30, 24)),
            (new ValueHash256("abcdf".PadRight(64, '9')), V(31, 24)),
            (new ValueHash256("abce0".PadRight(64, '9')), V(32, 24)),
        }).SetName("mixed_batch_on_cluster");
    }

    [TestCaseSource(nameof(ShapeScenarios))]
    public void Matches_patricia(
        (ValueHash256 Key, byte[] Value)[] parentEntries,
        (ValueHash256 Key, byte[]? Value)[] updates) =>
        AssertMatchesPatricia(parentEntries, updates);

    [Test]
    public void Empty_batch_keeps_anchor_root_without_reads()
    {
        (NodeStorage storage, Hash256 parentRoot) = BuildPatricia([(K("11"), V(1, 20))]);
        NodeStorageSource source = new(storage);
        using SparseTrie sparse = new(source, parentRoot.ValueHash256);

        sparse.Apply([]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sparse.CalculateRoot(), Is.EqualTo(parentRoot.ValueHash256));
            Assert.That(source.ResolveCalls, Is.Zero);
        }
    }

    [Test]
    public void Delete_with_unrevealed_sibling_defers_and_resolves()
    {
        // Two leaves under one branch: deleting one forces the collapse to reveal the other.
        (NodeStorage storage, Hash256 parentRoot) = BuildPatricia([(K("1a"), V(1, 20)), (K("2b"), V(2, 20))]);

        TrieRootFixture.RecordingTrieStore recorder = new(new RawScopedTrieStore(storage));
        PatriciaTree patricia = new(recorder, parentRoot, true, NullLogManager.Instance);
        patricia.Set(K("1a").Bytes, []);
        patricia.Commit();

        NodeStorageSource source = new(storage);
        using SparseTrie sparse = new(source, parentRoot.ValueHash256);
        sparse.Apply([new SparseTrieUpdate(K("1a"), null)]);

        Assert.That(sparse.CalculateRoot(), Is.EqualTo(patricia.RootHash.ValueHash256));
        AssertSameNodes(recorder.Committed, DrainStaged(sparse));
    }

    // Regression: splitting an extension at its final nibble adopts the unrevealed original
    // child as a blinded node; a later batch must be able to reveal through it (update case)
    // and to collapse into it (delete case).
    [TestCase(true)]
    [TestCase(false)]
    public void Blinded_child_from_extension_split_is_usable_in_later_batches(bool updateUnderBlinded)
    {
        // Parent: extension "1" -> branch(a, b). Inserting 2c... splits the extension at its
        // only nibble without revealing the branch, leaving it blinded under the new root branch.
        (NodeStorage storage, Hash256 parentRoot) = BuildPatricia([(K("1a"), V(1, 20)), (K("1b"), V(2, 20))]);

        NodeStorageSource source = new(storage);
        using SparseTrie sparse = new(source, parentRoot.ValueHash256);
        sparse.Apply([new SparseTrieUpdate(K("2c"), V(3, 20))]);
        sparse.CalculateRoot();

        PatriciaTree patricia = new(new RawScopedTrieStore(storage), parentRoot, true, NullLogManager.Instance);
        patricia.Set(K("2c").Bytes, V(3, 20));

        if (updateUnderBlinded)
        {
            sparse.Apply([new SparseTrieUpdate(K("1a"), V(9, 24))]);
            patricia.Set(K("1a").Bytes, V(9, 24));
        }
        else
        {
            // Deleting the inserted key makes the blinded subtree the collapse survivor.
            sparse.Apply([new SparseTrieUpdate(K("2c"), null)]);
            patricia.Set(K("2c").Bytes, []);
        }

        patricia.UpdateRootHash();
        Assert.That(sparse.CalculateRoot(), Is.EqualTo(patricia.RootHash.ValueHash256));
    }

    [Test]
    public void Malformed_short_leaf_is_rejected()
    {
        // A source that lies about validation: the returned root RLP is a leaf whose prefix does
        // not complete the 64-nibble key, which a canonical trie cannot contain.
        byte[] malformed = MakeShortLeafRlp();
        LyingSource source = new(malformed);
        using SparseTrie sparse = new(source, ValueKeccak.Compute(malformed));
        Assert.Throws<TrieException>(() => sparse.Apply([new SparseTrieUpdate(K("ab"), V(1, 8))]));
    }

    private static byte[] MakeShortLeafRlp()
    {
        // Leaf list: hexprefix "2081" (leaf flag, even, a 2-nibble prefix — 62 nibbles short of a
        // full key) followed by a 32-byte value.
        byte[] value = V(7, 32);
        byte[] rlp = new byte[37];
        rlp[0] = 0xC0 + 36;
        rlp[1] = 0x82;
        rlp[2] = 0x20;
        rlp[3] = 0x81;
        rlp[4] = 0xA0;
        value.CopyTo(rlp, 5);
        return rlp;
    }

    private sealed class LyingSource(byte[] rlp) : ISparseTrieNodeSource
    {
        public void Resolve(ReadOnlySpan<SparseNodeRequest> requests, Span<CappedArray<byte>> results)
        {
            for (int i = 0; i < requests.Length; i++)
            {
                results[i] = rlp;
            }
        }
    }

    private sealed class MapSource(Dictionary<ValueHash256, byte[]> nodes) : ISparseTrieNodeSource
    {
        public void Resolve(ReadOnlySpan<SparseNodeRequest> requests, Span<CappedArray<byte>> results)
        {
            for (int i = 0; i < requests.Length; i++)
            {
                results[i] = nodes.TryGetValue(requests[i].Hash, out byte[]? rlp) ? rlp : CappedArray<byte>.Null;
            }
        }
    }

    [Test]
    public void Malformed_collapse_survivor_is_rejected()
    {
        // Root branch with a valid leaf under nibble 1 and a malformed short leaf under nibble 2.
        // Deleting the valid key makes the malformed node the collapse survivor, revealed through
        // the deferred-collapse path that no target range walks afterwards.
        byte[] validLeaf = MakeLeafRlp(0x3a, 31, V(1, 20)); // odd 63-nibble suffix: 'a' + 31 'aa' bytes
        byte[] malformed = MakeShortLeafRlp();
        byte[] root = MakeTwoChildBranchRlp(1, ValueKeccak.Compute(validLeaf), 2, ValueKeccak.Compute(malformed));

        Dictionary<ValueHash256, byte[]> nodes = new()
        {
            [ValueKeccak.Compute(root)] = root,
            [ValueKeccak.Compute(validLeaf)] = validLeaf,
            [ValueKeccak.Compute(malformed)] = malformed,
        };

        using SparseTrie sparse = new(new MapSource(nodes), ValueKeccak.Compute(root));
        ValueHash256 deleteKey = new("1" + new string('a', 63));
        Assert.Throws<TrieException>(() =>
        {
            sparse.Apply([new SparseTrieUpdate(deleteKey, null)]);
            sparse.CalculateRoot();
        });
    }

    private static IEnumerable<TestCaseData> MalformedNodeRlp()
    {
        // Nested long list item: ItemLength must reject it without overflowing.
        yield return new TestCaseData((object)new byte[] { 0xC2, 0xF8, 0x00 }).SetName("nested_long_list_item");

        // 0xB9 long-form string cannot occur inside a trie node.
        yield return new TestCaseData((object)new byte[] { 0xC3, 0xB9, 0x01, 0x00 }).SetName("oversize_long_string_item");

        // 0xB8 string truncated at the end of the buffer.
        yield return new TestCaseData((object)new byte[] { 0xC1, 0xB8 }).SetName("truncated_long_string_item");

        // Long list header with a leading-zero length byte.
        byte[] leadingZero = new byte[60];
        leadingZero[0] = 0xF9;
        leadingZero[1] = 0x00;
        leadingZero[2] = 57;
        for (int i = 3; i < 60; i++)
        {
            leadingZero[i] = 0x80;
        }

        yield return new TestCaseData((object)leadingZero).SetName("leading_zero_list_length");

        // Overlong single-byte string (0x81 followed by a byte below 0x80).
        yield return new TestCaseData((object)new byte[] { 0xC3, 0x81, 0x20, 0x41 }).SetName("overlong_single_byte_key");

        // Hex-prefix flag byte with high bits set.
        yield return new TestCaseData((object)new byte[] { 0xC4, 0x82, 0x45, 0xAA, 0x41 }).SetName("invalid_hex_prefix_flags");

        // Even-length hex prefix with a non-zero padding nibble.
        yield return new TestCaseData((object)new byte[] { 0xC4, 0x82, 0x21, 0xAA, 0x41 }).SetName("even_prefix_with_padding_nibble");

        // Branch with a non-empty 17th (value) item.
        byte[] branchWithValue = new byte[18];
        branchWithValue[0] = 0xC0 + 17;
        for (int i = 1; i < 17; i++)
        {
            branchWithValue[i] = 0x80;
        }

        branchWithValue[17] = 0x41;
        yield return new TestCaseData((object)branchWithValue).SetName("branch_with_value");
    }

    [TestCaseSource(nameof(MalformedNodeRlp))]
    public void Malformed_node_rlp_is_rejected(byte[] rlp)
    {
        using SparseTrie sparse = new(new LyingSource(rlp), ValueKeccak.Compute(rlp));
        Assert.Throws<TrieException>(() => sparse.Apply([new SparseTrieUpdate(K("ab"), V(1, 8))]));
    }

    [Test]
    public void Leaf_shrinking_to_inline_releases_its_old_rlp()
    {
        // Two hashed leaves with 1-nibble suffixes; shrinking one value to a single byte turns
        // its leaf inline, and the abandoned RLP region must be accounted dead.
        (ValueHash256, byte[])[] parent =
        [
            (new ValueHash256(new string('b', 63) + "0"), V(1, 29)),
            (new ValueHash256(new string('b', 63) + "1"), V(2, 29)),
        ];

        (NodeStorage storage, Hash256 parentRoot) = BuildPatricia(parent);
        NodeStorageSource source = new(storage);
        using SparseTrie sparse = new(source, parentRoot.ValueHash256);

        sparse.Apply([new SparseTrieUpdate(new ValueHash256(new string('b', 63) + "0"), V(3, 1))]);
        long deadBefore = sparse.DeadBytes;
        sparse.CalculateRoot();

        PatriciaTree patricia = new(new RawScopedTrieStore(storage), parentRoot, true, NullLogManager.Instance);
        patricia.Set(new ValueHash256(new string('b', 63) + "0").Bytes, V(3, 1));
        patricia.UpdateRootHash();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sparse.RootHash, Is.EqualTo(patricia.RootHash.ValueHash256), "root");
            Assert.That(sparse.DeadBytes, Is.GreaterThan(deadBefore), "old leaf RLP accounted dead");
        }
    }

    [Test]
    public void Split_leaf_shrinking_to_inline_moves_its_aliasing_value()
    {
        // A revealed 33-byte leaf (2-nibble suffix, 28-byte value) is split by an insert that
        // diverges at its first suffix nibble; reattached with a 1-nibble suffix its RLP drops
        // to 31 bytes (inline) while its untouched value still aliases the old RLP region.
        string stem = new('c', 61);
        ValueHash256 key1 = new(stem + "0aa");
        ValueHash256 key2 = new(stem + "1bb");
        ValueHash256 key3 = new(stem + "0ba");

        (NodeStorage storage, Hash256 parentRoot) = BuildPatricia([(key1, V(1, 28)), (key2, V(2, 28))]);
        NodeStorageSource source = new(storage);
        using SparseTrie sparse = new(source, parentRoot.ValueHash256);

        sparse.Apply([new SparseTrieUpdate(key3, V(3, 28))]);
        long deadBefore = sparse.DeadBytes;
        ValueHash256 root = sparse.CalculateRoot();

        PatriciaTree patricia = new(new RawScopedTrieStore(storage), parentRoot, true, NullLogManager.Instance);
        patricia.Set(key3.Bytes, V(3, 28));
        patricia.UpdateRootHash();

        using (Assert.EnterMultipleScope())
        {
            // The DeadBytes growth is the observable effect of the alias move: without it the
            // old region could not be released while the value still pointed into it. The move
            // itself cannot be distinguished behaviorally because released regions are
            // accounting-only and never reused; the batches below instead prove the moved bytes
            // are read correctly wherever the value is consumed.
            Assert.That(root, Is.EqualTo(patricia.RootHash.ValueHash256), "root");
            Assert.That(sparse.DeadBytes, Is.GreaterThan(deadBefore), "old leaf RLP accounted dead");
        }

        // No-op update: the value compare must read the moved bytes.
        sparse.Apply([new SparseTrieUpdate(key1, V(1, 28))]);
        Assert.That(sparse.CalculateRoot(), Is.EqualTo(patricia.RootHash.ValueHash256), "no-op after move");

        // Collapse back: deleting the inserted key re-merges the leaf, whose re-encode reads the
        // moved value; a wrong copy would produce a wrong root here.
        sparse.Apply([new SparseTrieUpdate(key3, null)]);
        Assert.That(sparse.CalculateRoot(), Is.EqualTo(parentRoot.ValueHash256), "restored root after collapse re-encodes the moved value");
    }

    [Test]
    public void Branch_at_depth_64_is_rejected()
    {
        // Chain: extension (62 nibbles) -> branch@62 -> branch@63 -> branch@64 (illegal).
        byte[] branch64 = MakeTwoChildBranchRlp(3, ValueKeccak.Compute([1]), 4, ValueKeccak.Compute([2]));
        byte[] branch63 = MakeTwoChildBranchRlp(1, ValueKeccak.Compute(branch64), 2, ValueKeccak.Compute([3]));
        byte[] branch62 = MakeTwoChildBranchRlp(1, ValueKeccak.Compute(branch63), 2, ValueKeccak.Compute([4]));
        byte[] rootExt = MakeExtensionRlp62(0xc, ValueKeccak.Compute(branch62));

        Dictionary<ValueHash256, byte[]> nodes = new()
        {
            [ValueKeccak.Compute(rootExt)] = rootExt,
            [ValueKeccak.Compute(branch62)] = branch62,
            [ValueKeccak.Compute(branch63)] = branch63,
            [ValueKeccak.Compute(branch64)] = branch64,
        };

        using SparseTrie sparse = new(new MapSource(nodes), ValueKeccak.Compute(rootExt));
        ValueHash256 key = new(new string('c', 62) + "11");
        Assert.Throws<TrieException>(() => sparse.Apply([new SparseTrieUpdate(key, V(1, 8))]));
    }

    private static byte[] MakeExtensionRlp62(byte nibble, in ValueHash256 childHash)
    {
        // Extension with a 62-nibble prefix of one repeated nibble and a hashed child.
        int contentLength = 1 + 32 + 33; // 32-byte hex-prefix string + 33-byte hash item
        byte[] rlp = new byte[2 + contentLength];
        rlp[0] = 0xF8;
        rlp[1] = (byte)contentLength;
        rlp[2] = 0x80 + 32;
        rlp[3] = 0x00; // even-length extension flag
        for (int i = 4; i < 35; i++)
        {
            rlp[i] = (byte)((nibble << 4) | nibble);
        }

        rlp[35] = 0xA0;
        childHash.Bytes.CopyTo(rlp.AsSpan(36));
        return rlp;
    }

    [Test]
    public void Oversized_inline_child_is_rejected()
    {
        // A branch child item that is an embedded list of exactly 32 bytes: canonical tries hash
        // children at that size, so decode must reject it.
        byte[] root = new byte[49];
        root[0] = 0xC0 + 48;
        root[1] = 0xDF; // 32-byte inline list
        for (int i = 2; i < 33; i++)
        {
            root[i] = 0x01;
        }

        for (int i = 33; i < 49; i++)
        {
            root[i] = 0x80;
        }

        using SparseTrie sparse = new(new LyingSource(root), ValueKeccak.Compute(root));
        Assert.Throws<TrieException>(() => sparse.Apply([new SparseTrieUpdate(K("0"), V(1, 8))]));
    }

    private static byte[] MakeLeafRlp(byte firstPrefixByte, int extraPrefixBytes, byte[] value)
    {
        // Leaf list: hex-prefix string of (1 + extraPrefixBytes) bytes, then the value string.
        int keyLength = 1 + extraPrefixBytes;
        int contentLength = 1 + keyLength + 1 + value.Length;
        byte[] rlp = new byte[1 + contentLength];
        rlp[0] = (byte)(0xC0 + contentLength);
        rlp[1] = (byte)(0x80 + keyLength);
        rlp[2] = firstPrefixByte;
        for (int i = 0; i < extraPrefixBytes; i++)
        {
            rlp[3 + i] = 0xaa;
        }

        rlp[3 + extraPrefixBytes] = (byte)(0x80 + value.Length);
        value.CopyTo(rlp, 4 + extraPrefixBytes);
        return rlp;
    }

    private static byte[] MakeTwoChildBranchRlp(int nibbleA, in ValueHash256 hashA, int nibbleB, in ValueHash256 hashB)
    {
        int contentLength = 14 + 33 + 33 + 1;
        byte[] rlp = new byte[2 + contentLength];
        rlp[0] = 0xF8;
        rlp[1] = (byte)contentLength;
        int position = 2;
        for (int nibble = 0; nibble < 16; nibble++)
        {
            if (nibble == nibbleA || nibble == nibbleB)
            {
                rlp[position++] = 0xA0;
                (nibble == nibbleA ? hashA : hashB).Bytes.CopyTo(rlp.AsSpan(position));
                position += 32;
            }
            else
            {
                rlp[position++] = 0x80;
            }
        }

        rlp[position] = 0x80;
        return rlp;
    }

    [Test]
    public void Delete_to_empty_then_reinsert_across_batches()
    {
        (NodeStorage storage, Hash256 parentRoot) = BuildPatricia([(K("1a"), V(1, 20))]);

        NodeStorageSource source = new(storage);
        using SparseTrie sparse = new(source, parentRoot.ValueHash256);
        sparse.Apply([new SparseTrieUpdate(K("1a"), null)]);
        Assert.That(sparse.CalculateRoot(), Is.EqualTo(ValueKeccak.EmptyTreeHash), "empty after delete");

        sparse.Apply([new SparseTrieUpdate(K("2b"), V(2, 20))]);

        PatriciaTree patricia = new(new RawScopedTrieStore(new NodeStorage(new MemDb())), Keccak.EmptyTreeHash, true, NullLogManager.Instance);
        patricia.Set(K("2b").Bytes, V(2, 20));
        patricia.UpdateRootHash();
        Assert.That(sparse.CalculateRoot(), Is.EqualTo(patricia.RootHash.ValueHash256), "rebuilt from empty");
    }

    [Test]
    public void Value_reverted_across_batches_restores_root_and_republishes_only_committed_bytes()
    {
        (NodeStorage storage, Hash256 parentRoot) = BuildPatricia([(K("1a"), V(1, 20)), (K("2b"), V(2, 20))]);

        NodeStorageSource source = new(storage);
        using SparseTrie sparse = new(source, parentRoot.ValueHash256);
        sparse.Apply([new SparseTrieUpdate(K("1a"), V(9, 24))]);
        sparse.CalculateRoot();
        sparse.Apply([new SparseTrieUpdate(K("1a"), V(1, 20))]); // restore the committed value
        ValueHash256 root = sparse.CalculateRoot();

        Assert.That(root, Is.EqualTo(parentRoot.ValueHash256), "root restored");
        // Restored nodes are re-staged, but every drained node must be byte-identical to one
        // already committed (idempotent over-publication).
        List<SparseTrieStagedNode> drained = DrainStaged(sparse);
        Assert.That(drained, Is.Not.Empty, "restored nodes are re-staged");
        AssertSameNodes([], drained, storage);
    }

    [Test]
    public void Inline_subtree_survives_across_batches()
    {
        // Tiny values under a 63-nibble shared prefix produce an inline branch whose children
        // stay unmaterialized; a second batch must re-descend it correctly after the first
        // calculation re-encoded ancestors.
        (ValueHash256, byte[])[] parent =
        [
            (new ValueHash256(new string('a', 63) + "0"), V(1, 1)),
            (new ValueHash256(new string('a', 63) + "1"), V(2, 1)),
            (K("bb"), V(3, 20)),
        ];

        (NodeStorage storage, Hash256 parentRoot) = BuildPatricia(parent);

        NodeStorageSource source = new(storage);
        using SparseTrie sparse = new(source, parentRoot.ValueHash256);
        sparse.Apply([new SparseTrieUpdate(K("bb"), V(4, 20))]);
        sparse.CalculateRoot();
        sparse.Apply([new SparseTrieUpdate(new ValueHash256(new string('a', 63) + "1"), V(5, 1))]);

        PatriciaTree patricia = new(new RawScopedTrieStore(storage), parentRoot, true, NullLogManager.Instance);
        patricia.Set(K("bb").Bytes, V(4, 20));
        patricia.Set(new ValueHash256(new string('a', 63) + "1").Bytes, V(5, 1));
        patricia.UpdateRootHash();
        Assert.That(sparse.CalculateRoot(), Is.EqualTo(patricia.RootHash.ValueHash256));
    }

    [Test]
    public void Delete_then_recreate_across_batches()
    {
        (NodeStorage storage, Hash256 parentRoot) = BuildPatricia([(K("1a"), V(1, 20)), (K("2b"), V(2, 20)), (K("3c"), V(3, 20))]);

        NodeStorageSource source = new(storage);
        using SparseTrie sparse = new(source, parentRoot.ValueHash256);
        sparse.Apply([new SparseTrieUpdate(K("1a"), null)]);
        ValueHash256 intermediate = sparse.CalculateRoot();
        sparse.Apply([new SparseTrieUpdate(K("1a"), V(9, 24))]);
        ValueHash256 final = sparse.CalculateRoot();

        PatriciaTree patricia = new(new RawScopedTrieStore(storage), parentRoot, true, NullLogManager.Instance);
        patricia.Set(K("1a").Bytes, []);
        patricia.UpdateRootHash();
        Assert.That(intermediate, Is.EqualTo(patricia.RootHash.ValueHash256), "after delete");

        patricia.Set(K("1a").Bytes, V(9, 24));
        patricia.UpdateRootHash();
        Assert.That(final, Is.EqualTo(patricia.RootHash.ValueHash256), "after recreate");
    }

    [Test]
    public void Repeated_calculation_without_writes_is_idempotent_and_free()
    {
        (NodeStorage storage, Hash256 parentRoot) = BuildPatricia([(K("1a"), V(1, 20)), (K("2b"), V(2, 20))]);
        NodeStorageSource source = new(storage);
        using SparseTrie sparse = new(source, parentRoot.ValueHash256);

        sparse.Apply([new SparseTrieUpdate(K("1a"), V(5, 24))]);
        ValueHash256 first = sparse.CalculateRoot();
        int resolveCalls = source.ResolveCalls;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sparse.CalculateRoot(), Is.EqualTo(first));
            Assert.That(source.ResolveCalls, Is.EqualTo(resolveCalls), "no reads for a cached root");
        }
    }

    [Test]
    public void Dispose_is_idempotent()
    {
        NodeStorageSource source = new(new NodeStorage(new MemDb()));
        SparseTrie sparse = new(source, ValueKeccak.EmptyTreeHash);
        sparse.Apply([new SparseTrieUpdate(K("11"), V(1, 8))]);
        sparse.CalculateRoot();
        sparse.Dispose();
        Assert.DoesNotThrow(sparse.Dispose);
    }

    [Test]
    public void Duplicate_update_keys_throw()
    {
        NodeStorageSource source = new(new NodeStorage(new MemDb()));
        using SparseTrie sparse = new(source, ValueKeccak.EmptyTreeHash);

        SparseTrieUpdate[] updates = [new(K("11"), V(1, 4)), new(K("11"), V(2, 4))];
        Assert.Throws<ArgumentException>(() => sparse.Apply(updates));
    }

    [Test]
    public void Missing_parent_node_throws()
    {
        (NodeStorage storage, Hash256 parentRoot) = BuildPatricia([(K("1a"), V(1, 20)), (K("2b"), V(2, 20))]);
        NodeStorageSource source = new(storage);
        source.Missing.Add(parentRoot.ValueHash256);

        using SparseTrie sparse = new(source, parentRoot.ValueHash256);
        Assert.Throws<MissingTrieNodeException>(() => sparse.Apply([new SparseTrieUpdate(K("1a"), V(9, 4))]));
    }

    [Test]
    public void Corrupted_parent_node_throws()
    {
        (NodeStorage storage, Hash256 parentRoot) = BuildPatricia([(K("1a"), V(1, 20)), (K("2b"), V(2, 20))]);
        NodeStorageSource source = new(storage);
        source.Corrupted.Add(parentRoot.ValueHash256);

        using SparseTrie sparse = new(source, parentRoot.ValueHash256);
        // Corrupt bytes decode to garbage; any deterministic trie/RLP failure is acceptable,
        // and a validating source would have thrown NodeHashMismatch before decode.
        Assert.That(() => sparse.Apply([new SparseTrieUpdate(K("1a"), V(9, 4))]), Throws.InstanceOf<Exception>());
    }

    // ---------------- randomized multi-generation differential ----------------

    [TestCase(1, 5, 400, 60)]
    [TestCase(2, 5, 400, 60)]
    [TestCase(3, 8, 150, 90)]
    [TestCase(4, 3, 1500, 200)]
    public void Randomized_generations_match_patricia(int seed, int generations, int parentCount, int updatesPerGeneration)
    {
        Dictionary<ValueHash256, byte[]> model = [];
        List<(ValueHash256, byte[])> parentEntries = [];
        for (int i = 0; i < parentCount; i++)
        {
            ValueHash256 key = DeriveKey(seed, 0, i);
            byte[] value = DeriveValue(key, 0);
            model[key] = value;
            parentEntries.Add((key, value));
        }

        (NodeStorage storage, Hash256 parentRoot) = BuildPatricia(parentEntries);
        NodeStorageSource source = new(storage);
        using SparseTrie sparse = new(source, parentRoot.ValueHash256);

        List<ValueHash256> liveKeys = [.. model.Keys];
        int insertCounter = 0;

        for (int generation = 1; generation <= generations; generation++)
        {
            SparseTrieUpdate[] updates = BuildGenerationUpdates(seed, generation, updatesPerGeneration, model, liveKeys, ref insertCounter);

            // Reference: fresh Patricia over the same committed parents, whole-model replay is
            // avoided by applying only the delta to a tree anchored at the previous root.
            PatriciaTree patricia = new(new RawScopedTrieStore(storage), parentRoot, true, NullLogManager.Instance);
            foreach (SparseTrieUpdate update in updates)
            {
                patricia.Set(update.Key.Bytes, update.Value ?? []);
            }

            patricia.Commit();
            parentRoot = patricia.RootHash;

            sparse.Apply(updates);
            Assert.That(sparse.CalculateRoot(), Is.EqualTo(parentRoot.ValueHash256), $"root after generation {generation}");
        }

        // The drained staged set must equal a one-shot Patricia application of the net changes
        // from the original parents; values are never reused, so no staged node can coincide
        // with a committed parent node.
        (NodeStorage originalStorage, Hash256 originalRoot) = BuildPatricia(parentEntries);
        TrieRootFixture.RecordingTrieStore recorder = new(new RawScopedTrieStore(originalStorage));
        PatriciaTree oneShot = new(recorder, originalRoot, true, NullLogManager.Instance);
        Dictionary<ValueHash256, byte[]> originalModel = [];
        foreach ((ValueHash256 key, byte[] value) in parentEntries)
        {
            originalModel[key] = value;
        }

        foreach ((ValueHash256 key, byte[] value) in model)
        {
            if (!originalModel.TryGetValue(key, out byte[]? original) || !value.AsSpan().SequenceEqual(original))
            {
                oneShot.Set(key.Bytes, value);
            }
        }

        foreach ((ValueHash256 key, byte[] value) in originalModel)
        {
            if (!model.ContainsKey(key))
            {
                oneShot.Set(key.Bytes, []);
            }
        }

        oneShot.Commit();
        Assert.That(oneShot.RootHash, Is.EqualTo(parentRoot), "one-shot root");
        AssertSameNodes(recorder.Committed, DrainStaged(sparse), originalStorage);
    }

    // Retention reuses one SparseTrie across blocks: Apply -> CalculateRoot -> DrainUnpublished
    // (publish), then Apply again on the SAME instance. Draining nulls the staging list and
    // clears every unpublished flag/record; per-block dispose meant that drain-then-reapply
    // path was never exercised. Each generation's root must still match a fresh Patricia
    // anchored at the previous root.
    [TestCase(1, 5, 500, 120)]
    [TestCase(2, 8, 900, 160)]
    [TestCase(3, 6, 1500, 220)]
    public void Reused_trie_with_parallel_root_drained_each_generation_matches_patricia(
        int seed,
        int generations,
        int parentCount,
        int updatesPerGeneration)
    {
        Dictionary<ValueHash256, byte[]> model = [];
        List<(ValueHash256, byte[])> parentEntries = [];
        for (int i = 0; i < parentCount; i++)
        {
            ValueHash256 key = DeriveKey(seed, 0, i);
            byte[] value = DeriveValue(key, 0);
            model[key] = value;
            parentEntries.Add((key, value));
        }

        (NodeStorage storage, Hash256 parentRoot) = BuildPatricia(parentEntries);
        NodeStorageSource source = new(storage);
        using SparseTrie sparse = new(source, parentRoot.ValueHash256);

        List<ValueHash256> liveKeys = [.. model.Keys];
        int insertCounter = 0;

        for (int generation = 1; generation <= generations; generation++)
        {
            SparseTrieUpdate[] updates = BuildGenerationUpdates(seed, generation, updatesPerGeneration, model, liveKeys, ref insertCounter);

            PatriciaTree patricia = new(new RawScopedTrieStore(storage), parentRoot, true, NullLogManager.Instance);
            foreach (SparseTrieUpdate update in updates)
            {
                patricia.Set(update.Key.Bytes, update.Value ?? []);
            }

            patricia.Commit();
            parentRoot = patricia.RootHash;

            sparse.Apply(updates);
            Assert.That(
                sparse.CalculateRoot(canBeParallel: true),
                Is.EqualTo(parentRoot.ValueHash256),
                $"reused root after generation {generation}");

            // Publish and clear staging exactly as retention does between blocks, then loop and
            // re-Apply on the same warm instance.
            using ArrayPoolList<SparseTrieStagedNode> drained = new(16);
            sparse.DrainUnpublished(drained);
            Assert.That(drained.Count, Is.GreaterThan(0), $"generation {generation} published nodes");
            Assert.That(sparse.IsDirty, Is.False, $"generation {generation} clean after drain");
        }
    }

    // A drained trie reused across blocks must resolve blinded children against the NEXT scope's
    // committed reader, not the one it was constructed with.
    [Test]
    public void RebindSource_directs_later_reveals_to_the_new_source()
    {
        // Two keys diverging at the first nibble: the root branch has children at 0x0.. and 0x1..
        byte[] valueA = V(1, 4);
        byte[] valueB = V(2, 4);
        (NodeStorage storage, Hash256 root) = BuildPatricia(
        [
            (K("0"), valueA),
            (K("1"), valueB),
        ]);

        NodeStorageSource original = new(storage);
        using SparseTrie sparse = new(original, root.ValueHash256);

        // Touch only the 0x0.. side: reveals the root branch and the 0x0 child, leaving the 0x1
        // child blinded in the arena.
        SparseTrieUpdate[] first = [new SparseTrieUpdate(K("0"), V(3, 5))];
        sparse.Apply(first);
        sparse.CalculateRoot();
        using (ArrayPoolList<SparseTrieStagedNode> drained = new(8))
        {
            sparse.DrainUnpublished(drained);
        }

        NodeStorageSource rebound = new(storage);
        sparse.RebindSource(rebound);
        int originalCallsBeforeReveal = original.ResolveCalls;

        // Touch the 0x1.. side: the blinded child must be revealed through the new source.
        SparseTrieUpdate[] second = [new SparseTrieUpdate(K("1"), V(4, 6))];
        sparse.Apply(second);
        sparse.CalculateRoot();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rebound.ResolveCalls, Is.GreaterThan(0), "reveal went to the rebound source");
            Assert.That(original.ResolveCalls, Is.EqualTo(originalCallsBeforeReveal), "old source untouched after rebind");
        }
    }

    private static SparseTrieUpdate[] BuildGenerationUpdates(
        int seed,
        int generation,
        int count,
        Dictionary<ValueHash256, byte[]> model,
        List<ValueHash256> liveKeys,
        ref int insertCounter)
    {
        List<SparseTrieUpdate> updates = [];
        HashSet<ValueHash256> used = [];
        for (int i = 0; i < count; i++)
        {
            ValueHash256 decision = DeriveKey(seed, generation * 1000 + 1, i);
            int action = decision.Bytes[0] % 10;
            if (action < 5 && liveKeys.Count > 0)
            {
                // Update an existing key.
                ValueHash256 key = liveKeys[(int)(BinaryPrimitives.ReadUInt32LittleEndian(decision.Bytes[1..]) % (uint)liveKeys.Count)];
                if (!used.Add(key))
                {
                    continue;
                }

                byte[] value = DeriveValue(key, generation);
                model[key] = value;
                updates.Add(new SparseTrieUpdate(key, value));
            }
            else if (action < 8)
            {
                // Insert a new key.
                ValueHash256 key = DeriveKey(seed, 2, insertCounter++);
                if (!used.Add(key))
                {
                    continue;
                }

                byte[] value = DeriveValue(key, generation);
                model[key] = value;
                liveKeys.Add(key);
                updates.Add(new SparseTrieUpdate(key, value));
            }
            else if (liveKeys.Count > 0)
            {
                // Delete an existing key (or an absent one, occasionally).
                bool absent = decision.Bytes[2] % 8 == 0;
                ValueHash256 key = absent
                    ? DeriveKey(seed, 3, insertCounter + i)
                    : liveKeys[(int)(BinaryPrimitives.ReadUInt32LittleEndian(decision.Bytes[1..]) % (uint)liveKeys.Count)];
                if (!used.Add(key))
                {
                    continue;
                }

                if (!absent)
                {
                    model.Remove(key);
                    liveKeys.Remove(key);
                }

                updates.Add(new SparseTrieUpdate(key, null));
            }
        }

        return [.. updates];
    }

    private static ValueHash256 DeriveKey(int seed, int domain, int index)
    {
        Span<byte> material = stackalloc byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(material, seed);
        BinaryPrimitives.WriteInt32LittleEndian(material[4..], domain);
        BinaryPrimitives.WriteInt32LittleEndian(material[8..], index);
        return ValueKeccak.Compute(material);
    }

    private static byte[] DeriveValue(in ValueHash256 key, int generation)
    {
        Span<byte> material = stackalloc byte[36];
        key.Bytes.CopyTo(material);
        BinaryPrimitives.WriteInt32LittleEndian(material[32..], generation);
        ValueHash256 derived = ValueKeccak.Compute(material);
        int length = 1 + derived.Bytes[0] % 28;
        byte[] value = derived.Bytes.Slice(1, length).ToArray();
        if (value[0] == 0)
        {
            value[0] = 1;
        }

        return value;
    }

    // ---------------- steady-state allocation ----------------

    [Test]
    public void Prepared_calculation_allocates_only_staged_output()
    {
        TrieRootFixture fixture = TrieRootFixture.Create("alloc", TrieRootFixture.TrieKind.Storage, seed: 11, parentCount: 20_000, modifyCount: 150, insertCount: 30, deleteCount: 20);
        SparseTrieUpdate[] pristine = new SparseTrieUpdate[fixture.Updates.Length];
        for (int i = 0; i < pristine.Length; i++)
        {
            pristine[i] = new SparseTrieUpdate(fixture.Updates[i].Path, fixture.Updates[i].Value.Length == 0 ? null : fixture.Updates[i].Value);
        }

        NodeStorageSource source = new(fixture.ParentStorage);
        SparseTrieUpdate[] scratch = new SparseTrieUpdate[pristine.Length];

        long stagedBytes = RunOnce(); // warm-up: pools, source caches, JIT
        long allocated = -GC.GetAllocatedBytesForCurrentThread();
        stagedBytes = RunOnce();
        allocated += GC.GetAllocatedBytesForCurrentThread();

        // The dominant allocation must be the staged RLP arrays plus per-node record/collection
        // slack; parent reads through MemDb-backed NodeStorage allocate the returned copies,
        // which the Flat source will not, so allow a bounded per-revealed-node overhead.
        Assert.That(allocated, Is.LessThan(stagedBytes * 3 + 512 * 1024),
            $"allocated {allocated} bytes for {stagedBytes} staged RLP bytes");

        long RunOnce()
        {
            pristine.CopyTo(scratch, 0);
            using SparseTrie sparse = new(source, fixture.ParentRoot.ValueHash256, nodeCapacityHint: 4096);
            sparse.Apply(scratch);
            ValueHash256 root = sparse.CalculateRoot();
            if (root != fixture.ExpectedRoot.ValueHash256)
            {
                throw new InvalidOperationException("root mismatch");
            }

            long bytes = 0;
            using ArrayPoolList<SparseTrieStagedNode> drained = new(1024);
            sparse.DrainUnpublished(drained);
            foreach (SparseTrieStagedNode node in drained.AsSpan())
            {
                bytes += node.Rlp.Length;
            }

            return bytes;
        }
    }
}

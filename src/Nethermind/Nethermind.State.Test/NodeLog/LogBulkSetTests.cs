// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.NodeLog;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Store.Test.NodeLog;

internal class LogBulkSetTests
{
    private const int SmallGenerationBytes = 64 * 1024;

    private static readonly byte[] ExistingValue = Bytes.FromHexString("0x7777777777777777777777777777777777777777777777777777777777777777");
    private static readonly byte[] NewValue = Bytes.FromHexString("0x010203");
    private static readonly byte[] FillerValue = Enumerable.Repeat((byte)0x77, 1000).ToArray();

    public static IEnumerable<TestCaseData> PatriciaTreeCases()
    {
        List<(Hash256 key, byte[] value)> existing = PatriciaTreeBulkSetterTests.GenRandomOfLength(10000, seed: 1);
        List<(Hash256 key, byte[] value)> changes =
        [
            .. existing.Take(2500).Select(item => (item.key, (byte[])[.. item.value, 1])),
            .. existing.Skip(2500).Take(2500).Select(item => (item.key, (byte[])null)),
            .. PatriciaTreeBulkSetterTests.GenRandomOfLength(5000, seed: 2),
        ];
        // Nine keys under each first nibble, then one sharing all but the last nibble with the first, so deleting enough of them
        // collapses a full root branch, updated in parallel, onto a leaf or an extension under nibble 0.
        List<(Hash256 key, byte[] value)> fullRoot =
        [
            .. Enumerable.Range(0, 144).Select(i => (Key((byte)((i / 9) << 4 | i % 9), 0), ExistingValue)),
            (Key(0, 1), ExistingValue),
        ];
        IEnumerable<TestCaseData> cases = PatriciaTreeBulkSetterTests.BulkSetTestGen()
            .Append(new TestCaseData(existing, changes).SetName("10k inserts then 2.5k updates, 2.5k deletes and 5k inserts"))
            .Append(new TestCaseData(fullRoot, Deletes(fullRoot[1..])).SetName("Full root then deletes of all but one leaf"))
            .Append(new TestCaseData(fullRoot, Deletes(fullRoot[1..^1])).SetName("Full root then deletes of all but an extension"));
        foreach (TestCaseData testCase in cases)
        {
            foreach (bool smallGenerations in new[] { false, true })
            {
                foreach (bool fromSource in new[] { false, true })
                {
                    foreach (bool canParallelize in new[] { false, true })
                    {
                        yield return new TestCaseData(testCase.Arguments[0], testCase.Arguments[1], smallGenerations, fromSource, canParallelize)
                            .SetName($"{testCase.TestName}, small generations {smallGenerations}, from source {fromSource}, can parallelize {canParallelize}");
                    }
                }
            }
        }

        static Hash256 Key(byte first, byte last)
        {
            byte[] key = new byte[Hash256.Size];
            key[0] = first;
            key[^1] = last;
            return new Hash256(key);
        }

        static List<(Hash256 key, byte[] value)> Deletes(List<(Hash256 key, byte[] value)> items) => [.. items.Select(item => (item.key, (byte[])null))];
    }

    [TestCaseSource(nameof(PatriciaTreeCases))]
    public void Root_and_reachable_nodes_match_patricia_tree(List<(Hash256 key, byte[] value)> existingItems, List<(Hash256 key, byte[] value)> items, bool smallGenerations, bool fromSource, bool canParallelize)
    {
        CommitRecordingTrieStore trieStore = new();
        PatriciaTree tree = new(new ScopedTrieStore(trieStore, null), LimboLogs.Instance);
        foreach ((Hash256 key, byte[] value) in existingItems) tree.Set(key.Bytes, value);
        tree.Commit();
        Hash256 expectedExistingRoot = tree.RootHash;
        Dictionary<TreePath, byte[]> existingNodes = trieStore.TakeCommitted();
        using (ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = Entries(items)) tree.BulkSet(entries);
        tree.Commit();
        Dictionary<TreePath, byte[]> expectedWritten = trieStore.TakeCommitted();

        int generationBytes = smallGenerations ? SmallGenerationBytes : TrieNodeLog.DefaultGenerationBytes;
        using TrieNodeLog existingLog = new(0, generationBytes);
        using TrieNodeLogRetain existingRetain = existingLog.RetainActive();
        DictionaryNodeSource source = new(existingNodes, new WarmedNodes(existingNodes.Count), null);
        TrieRoot existingRoot = BulkSet(existingLog, source, null, TrieRoot.Empty, existingItems, canParallelize);
        // From the source, the existing trie stands for one persisted before this log was created.
        using TrieNodeLog log = fromSource ? new(0, generationBytes) : existingLog;
        using TrieNodeLogRetain retain = log.RetainActive();
        TrieRoot root = BulkSet(log, source, null, fromSource ? new TrieRoot(expectedExistingRoot.ValueHash256, 0) : existingRoot, items, canParallelize);
        int fetchCount = source.FetchCount;

        Dictionary<Hash256, byte[]> expectedValues = [];
        foreach ((Hash256 key, byte[] value) in existingItems.Concat(items))
        {
            if (value is null || value.Length == 0) expectedValues.Remove(key);
            else expectedValues[key] = value;
        }

        (_, Dictionary<TreePath, byte[]> expectedReachable) = BuildExpectedTrie(expectedValues);
        Dictionary<TreePath, byte[]> expectedLogNodes = fromSource
            ? expectedWritten.Where(node => expectedReachable.TryGetValue(node.Key, out byte[] rlp) && node.Value.AsSpan().SequenceEqual(rlp)).ToDictionary()
            : expectedReachable;
        IEnumerable<Hash256> keys = existingItems.Concat(items).Select(item => item.key).Distinct();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(existingRoot.Hash, Is.EqualTo(expectedExistingRoot.ValueHash256), "existing root");
            Assert.That(root.Hash, Is.EqualTo(tree.RootHash.ValueHash256), "root");
            Assert.That(InvalidRecords(existingLog, existingRoot).Concat(InvalidRecords(log, root)), Is.Empty, "reachable records hash to their hash and parse");
            Assert.That(Rlps(log, root), Is.EquivalentTo(expectedLogNodes), "reachable nodes");
            if (!fromSource) Assert.That(fetchCount, Is.Zero, "nodes the first bulk set wrote are read from the log");
            Assert.That(keys.Select(key => LogBulkSet.TryGet(log, source, null, root, key.ValueHash256)), Is.EqualTo(keys.Select(key => expectedValues.GetValueOrDefault(key))), "values");
        }
    }

    [Test]
    public void Repeated_batches_keep_final_roots_and_reachable_rlps_including_deletion_to_empty([Values] bool canParallelize)
    {
        List<(Hash256 key, byte[] value)> initial = PatriciaTreeBulkSetterTests.GenRandomOfLength(1024, seed: 7);
        List<(Hash256 key, byte[] value)> added = PatriciaTreeBulkSetterTests.GenRandomOfLength(256, seed: 8);
        List<(Hash256 key, byte[] value)>[] batches =
        [
            initial,
            [.. initial.Take(512).Select(item => (item.key, NewValue)), .. initial.Skip(512).Select(item => (item.key, (byte[])null)), .. added],
            [.. initial.Take(512).Concat(added).Select(item => (item.key, (byte[])null))],
            added,
        ];
        using TrieNodeLog log = new(0, SmallGenerationBytes);
        using TrieNodeLogRetain retain = log.RetainActive();
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), new WarmedNodes(1), null);
        Dictionary<Hash256, byte[]> values = [];
        TrieRoot root = TrieRoot.Empty;
        foreach (List<(Hash256 key, byte[] value)> batch in batches)
        {
            root = BulkSet(log, source, null, root, batch, canParallelize);
            foreach ((Hash256 key, byte[] value) in batch)
            {
                if (value is null) values.Remove(key);
                else values[key] = value;
            }
            (Hash256 expectedRoot, Dictionary<TreePath, byte[]> expectedNodes) = BuildExpectedTrie(values);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(root.Hash, Is.EqualTo(expectedRoot.ValueHash256));
                Assert.That(Rlps(log, root), Is.EquivalentTo(expectedNodes));
                Assert.That(InvalidRecords(log, root), Is.Empty);
                Assert.That(source.FetchCount, Is.Zero);
            }
        }
    }

    [TestCase(TestName = "No entries")]
    [TestCase("a100000000000000000000000000000000000000000000000000000000000000", "a200000000000000000000000000000000000000000000000000000000000000", TestName = "Deletes of absent keys diverging at a leaf")]
    [TestCase("b100000000000000000000000000000000000000000000000000000000000000", "b200000000000000000000000000000000000000000000000000000000000000", TestName = "Deletes of absent keys diverging at an extension")]
    [TestCase("aaaa000000000000000000000000000000000000000000000000000000000000", "bbbb100000000000000000000000000000000000000000000000000000000000", TestName = "Same-value sets")]
    [TestCase("aaaa000000000000000000000000000000000000000000000000000000000000", "a100000000000000000000000000000000000000000000000000000000000000", TestName = "Same-value set and delete of an absent key at a leaf")]
    public void No_op_batches_leave_the_root_unchanged_and_write_nothing(params string[] keys)
    {
        // A root branch holding a leaf under a and an extension under b.
        string[] existingKeys =
        [
            "aaaa000000000000000000000000000000000000000000000000000000000000",
            "bbbb100000000000000000000000000000000000000000000000000000000000",
            "bbbb200000000000000000000000000000000000000000000000000000000000",
        ];
        using TrieNodeLog log = new(0, SmallGenerationBytes);
        using TrieNodeLogRetain retain = log.RetainActive();
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), new WarmedNodes(1), null);
        TrieRoot root = BulkSet(log, source, null, TrieRoot.Empty, existingKeys.Select(key => (new Hash256(key), ExistingValue)).ToList(), canParallelize: false);

        ulong before = log.Reserve(0, out _, out _);
        TrieRoot after = BulkSet(log, source, null, root, keys.Select(key => (new Hash256(key), existingKeys.Contains(key) ? ExistingValue : null)).ToList(), canParallelize: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(after.Hash, Is.EqualTo(root.Hash), "root hash");
            Assert.That(after.Pointer, Is.EqualTo(root.Pointer), "root pointer");
            Assert.That(log.Reserve(0, out _, out _), Is.EqualTo(before), "no log bytes reserved");
        }
    }

    [Test]
    public void WarmUp_fetches_only_nodes_outside_the_log_and_bulk_set_then_reads_them_without_fetching()
    {
        Hash256 address = TestItem.KeccakA;
        List<(Hash256 key, byte[] value)> items = PatriciaTreeBulkSetterTests.GenRandomOfLength(1000, seed: 4);
        using TrieNodeLog log = new(0, SmallGenerationBytes);
        using TrieNodeLogRetain retain = log.RetainActive();
        TrieRoot root = BulkSet(log, new DictionaryNodeSource(new Dictionary<TreePath, byte[]>(), new WarmedNodes(1), address), address, TrieRoot.Empty, items, canParallelize: false);

        // A log without the trie stands for one written before the trie was persisted.
        using TrieNodeLog persistedLog = new(0, SmallGenerationBytes);
        TrieRoot persistedRoot = LogBulkSet.ResolveRoot(persistedLog, address, root.Hash);
        Dictionary<TreePath, byte[]> persisted = Rlps(log, root);
        DictionaryNodeSource source = new(persisted, new WarmedNodes(persisted.Count), address);
        int fromLog = WarmUp(log, root, items[0].key);
        int fromSource = WarmUp(persistedLog, persistedRoot, items[0].key);
        int again = WarmUp(persistedLog, persistedRoot, items[0].key);
        foreach ((Hash256 key, _) in items) WarmUp(persistedLog, persistedRoot, key);
        int fetchesBeforeBulkSet = source.FetchCount;
        TrieRoot newRoot = BulkSet(persistedLog, source, address, persistedRoot, [(items[0].key, NewValue)], canParallelize: false);

        PatriciaTree tree = new(new RawScopedTrieStore(new MemDb()), LimboLogs.Instance);
        foreach ((Hash256 key, byte[] value) in items) tree.Set(key.Bytes, value);
        tree.Set(items[0].key.Bytes, NewValue);
        tree.UpdateRootHash();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(LogBulkSet.ResolveRoot(log, address, root.Hash).Pointer, Is.EqualTo(root.Pointer), "indexed root");
            Assert.That(persistedRoot.Pointer, Is.Zero, "root of another log");
            Assert.That(fromLog, Is.Zero, "warm-up through the log");
            Assert.That(fromSource, Is.GreaterThan(1), "warm-up through the source");
            Assert.That(again, Is.Zero, "warm-up of warmed nodes");
            Assert.That(source.FetchCount, Is.EqualTo(fetchesBeforeBulkSet), "bulk set over warmed nodes");
            Assert.That(newRoot.Hash, Is.EqualTo(tree.RootHash.ValueHash256), "root");
        }

        int WarmUp(TrieNodeLog walkedLog, TrieRoot walkedRoot, Hash256 key)
        {
            int before = source.FetchCount;
            LogBulkSet.WarmUp(walkedLog, source, address, walkedRoot, key.ValueHash256);
            return source.FetchCount - before;
        }
    }

    [Test]
    public void Delete_warming_loads_only_the_sibling_needed_by_a_collapsing_branch([Values] bool forDelete, [Values] bool threeChildren, [Values] bool inlineDeletedLeaf)
    {
        Hash256 keyA = new("0x1000000000000000000000000000000000000000000000000000000000000001");
        Hash256 keyB = new("0x1000000000000000000000000000000000000000000000000000000000000002");
        Hash256 keyC = new("0x2000000000000000000000000000000000000000000000000000000000000000");
        Hash256 keyD = new("0x1000000000000000000000000000000000000000000000000000000000000003");
        List<(Hash256 key, byte[] value)> items = [(keyA, inlineDeletedLeaf ? [1] : ExistingValue), (keyB, ExistingValue), (keyC, ExistingValue)];
        if (threeChildren) items.Add((keyD, ExistingValue));
        using TrieNodeLog original = new(0, SmallGenerationBytes);
        using TrieNodeLogRetain retain = original.RetainActive();
        TrieRoot root = BulkSet(original, new DictionaryNodeSource(new Dictionary<TreePath, byte[]>(), new WarmedNodes(1), null), null, TrieRoot.Empty, items, false);
        DictionaryNodeSource source = new(Rlps(original, root), new WarmedNodes(1024), null);
        using TrieNodeLog log = new(0, SmallGenerationBytes);
        using TrieNodeLogRetain newRetain = log.RetainActive();
        TrieRoot persistedRoot = new(root.Hash, 0);

        LogBulkSet.WarmUp(log, source, null, persistedRoot, keyA.ValueHash256, forDelete);
        int before = source.FetchCount;
        TrieRoot updated = BulkSet(log, source, null, persistedRoot, [(keyA, null)], false);
        PatriciaTree expected = new(new RawScopedTrieStore(new MemDb()), LimboLogs.Instance);
        foreach ((Hash256 key, byte[] value) in items.Where(item => item.key != keyA)) expected.Set(key.Bytes, value);
        expected.UpdateRootHash();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(before, Is.EqualTo(3 + (inlineDeletedLeaf ? 0 : 1) + (forDelete && !threeChildren ? 1 : 0)), "only a collapsing branch needs a node beyond the path");
            Assert.That(source.FetchCount - before, Is.EqualTo(forDelete || threeChildren ? 0 : 1), "only a path-only warm-up leaves the collapse sibling unread");
            Assert.That(updated.Hash, Is.EqualTo(expected.RootHash.ValueHash256));
        }
    }

    [Test]
    public void Delete_warming_of_a_missing_key_loads_no_sibling([Values] bool divergingLeaf)
    {
        Hash256 keyA = new("0x1000000000000000000000000000000000000000000000000000000000000001");
        Hash256 keyB = new("0x2000000000000000000000000000000000000000000000000000000000000002");
        Hash256 missing = new(divergingLeaf
            ? "0x1000000000000000000000000000000000000000000000000000000000000004"
            : "0x3000000000000000000000000000000000000000000000000000000000000004");
        using TrieNodeLog original = new(0, SmallGenerationBytes);
        using TrieNodeLogRetain retain = original.RetainActive();
        TrieRoot root = BulkSet(original, new DictionaryNodeSource(new Dictionary<TreePath, byte[]>(), new WarmedNodes(1), null), null, TrieRoot.Empty,
            [(keyA, ExistingValue), (keyB, ExistingValue)], false);
        Dictionary<TreePath, byte[]> nodes = Rlps(original, root);
        DictionaryNodeSource pathSource = new(nodes, new WarmedNodes(1024), null);
        DictionaryNodeSource deleteSource = new(nodes, new WarmedNodes(1024), null);
        using TrieNodeLog log = new(0, SmallGenerationBytes);
        TrieRoot persistedRoot = new(root.Hash, 0);

        LogBulkSet.WarmUp(log, pathSource, null, persistedRoot, missing.ValueHash256);
        LogBulkSet.WarmUp(log, deleteSource, null, persistedRoot, missing.ValueHash256, forDelete: true);

        Assert.That(deleteSource.FetchCount, Is.EqualTo(pathSource.FetchCount));
    }

    [Test]
    public void Compaction_drops_rewritten_paths_and_keeps_unchanged_subtrees_reachable_by_pointer()
    {
        const ulong compressedBudget = 1UL << 20;
        const int fillerBatch = 16;
        using TrieNodeLog log = new(compressedBudget, SmallGenerationBytes);
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), new WarmedNodes(1), null);
        List<(Hash256 key, byte[] value)> items = PatriciaTreeBulkSetterTests.GenRandomOfLength(100, seed: 5);
        TrieRoot fillerRoot = TrieRoot.Empty;
        int fillerSeed = 0;
        TrieRoot root = BulkSet(log, source, null, TrieRoot.Empty, items, canParallelize: false);
        (TreePath Path, ulong Pointer)[] nodes = log.EnumerateNodes(root.Pointer).Select(node => (node.Path, node.Pointer)).ToArray();
        ulong newestTrieGeneration = nodes.Max(node => TrieNodeLogTests.GenerationOf(node.Pointer));
        FillUntil(newestTrieGeneration + 1);
        TrieRoot newRoot = BulkSet(log, source, null, root, [(items[0].key, NewValue)], canParallelize: false);
        HashSet<TreePath> rewrittenPaths = [.. nodes.Where(node => !log.TryGetNodePointer(newRoot.Pointer, node.Path, out ulong pointer) || pointer != node.Pointer).Select(node => node.Path)];
        // Few large filler leaves per generation keep false positives out of the blooms the dropped trie is compacted against.
        FillUntil(newestTrieGeneration + TrieNodeLogTests.LiveGenerations);
        Assert.That(() => TrieNodeLogTests.IsLocatable(log, root.Pointer), Is.False.After(TrieNodeLogTests.CompactionTimeoutMilliseconds, TrieNodeLogTests.PollingMilliseconds),
            "the generations of the trie are compacted");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(TrieNodeLogTests.GenerationOf(newRoot.Pointer), Is.GreaterThan(newestTrieGeneration), "generation of the rewrite");
            Assert.That(nodes.Select(node => TrieNodeLogTests.IsLocatable(log, node.Pointer)), Is.EqualTo(nodes.Select(node => !rewrittenPaths.Contains(node.Path))), "records resolve unless their path was rewritten");
            Assert.That(items.Select(item => LogBulkSet.TryGet(log, source, null, newRoot, item.key.ValueHash256)), Is.EqualTo(items.Select((item, index) => index == 0 ? NewValue : item.value)), "values");
            Assert.That(source.FetchCount, Is.Zero, "nodes read from the log");
        }

        void FillUntil(ulong generation)
        {
            while (fillerRoot.Pointer == 0 || TrieNodeLogTests.GenerationOf(fillerRoot.Pointer) < generation)
            {
                List<(Hash256 key, byte[] value)> filler = [.. Enumerable.Range(fillerSeed, fillerBatch).Select(seed => (Keccak.Compute(BitConverter.GetBytes(seed)), FillerValue))];
                fillerRoot = BulkSet(log, source, TestItem.KeccakA, TrieRoot.Empty, filler, canParallelize: false);
                fillerSeed += fillerBatch;
            }
        }
    }

    [Test]
    public void Traversal_finds_exact_node_paths_and_enumerates_in_depth_first_order([Values] bool inlineLeaves)
    {
        using TrieNodeLog log = new(0, SmallGenerationBytes);
        using TrieNodeLogRetain retain = log.RetainActive();
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), new WarmedNodes(1), null);
        byte[] value = inlineLeaves ? [1] : ExistingValue;
        TrieRoot root = BulkSet(log, source, null, TrieRoot.Empty,
        [
            (new Hash256("1000000000000000000000000000000000000000000000000000000000000001"), value),
            (new Hash256("1000000000000000000000000000000000000000000000000000000000000002"), ExistingValue),
            (new Hash256("2000000000000000000000000000000000000000000000000000000000000000"), ExistingValue),
        ], false);
        string branchPath = "1" + new string('0', 62);
        string[] expectedPaths = inlineLeaves
            ? ["", "1", branchPath, branchPath + "2", "2"]
            : ["", "1", branchPath, branchPath + "1", branchPath + "2", "2"];
        (TreePath Path, ulong Pointer)[] actual = log.EnumerateNodes(root.Pointer).Select(node => (node.Path, node.Pointer)).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.Select(node => node.Path), Is.EqualTo(expectedPaths.Select(TreePath.FromHexString)));
            foreach ((TreePath path, ulong pointer) in actual)
            {
                Assert.That(log.TryGetNodePointer(root.Pointer, path, out ulong found), Is.True, path.ToString());
                Assert.That(found, Is.EqualTo(pointer), path.ToString());
                Assert.That(log.TryGetNode(root.Pointer, path, out TrieNode node), Is.True, path.ToString());
                Assert.That(node?.FullRlp.ToArray(), Is.EqualTo(log.GetRecord(pointer).Rlp.ToArray()), path.ToString());
            }
            foreach (string path in new[] { "3", "10", "11", "20" })
            {
                Assert.That(log.TryGetNodePointer(root.Pointer, TreePath.FromHexString(path), out _), Is.False, path);
            }
            TreePath leafPath = TreePath.FromHexString("1000000000000000000000000000000000000000000000000000000000000001");
            Assert.That(log.TryGetNode(root.Pointer, leafPath, out _), Is.EqualTo(!inlineLeaves), "inline leaves have no separate log record");
            Assert.That(source.FetchCount, Is.Zero);
        }
    }

    [Test]
    public void Traversal_preserves_sparse_child_order_with_inline_and_unknown_children()
    {
        using TrieNodeLog log = new(0, SmallGenerationBytes);
        using TrieNodeLogRetain retain = log.RetainActive();
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), new WarmedNodes(1), null);
        string prefix = new('0', 63);
        TrieRoot root = BulkSet(log, source, null, TrieRoot.Empty,
        [
            (new Hash256(prefix + "1"), new byte[] { 1 }),
            (new Hash256(prefix + "5"), ExistingValue),
            (new Hash256(prefix + "9"), ExistingValue),
            (new Hash256(prefix + "f"), ExistingValue),
        ], false);
        NodeRecordRef extension = log.GetRecord(root.Pointer);
        ulong branchPointer = extension.View.Child(0).Pointer;
        NodeRecordRef branch = log.GetRecord(branchPointer);
        ulong firstChild = branch.View.Child(5).Pointer;
        ulong lastChild = branch.View.Child(15).Pointer;
        ulong unlinkedBranch = CopyWithPointer(branch, 1, 0);
        ulong unlinkedRoot = CopyWithPointer(extension, 0, unlinkedBranch);

        (TreePath Path, ulong Pointer)[] actual = log.EnumerateNodes(unlinkedRoot).Select(node => (node.Path, node.Pointer)).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(branch.Kind, Is.EqualTo(NodeRecordKind.InlineBranch));
            Assert.That(actual, Is.EqualTo(new[]
            {
                (TreePath.Empty, unlinkedRoot),
                (TreePath.FromHexString(prefix), unlinkedBranch),
                (TreePath.FromHexString(prefix + "5"), firstChild),
                (TreePath.FromHexString(prefix + "f"), lastChild),
            }));
            Assert.That(source.FetchCount, Is.Zero);
        }

        ulong CopyWithPointer(NodeRecordRef original, int ordinal, ulong pointer)
        {
            ulong copy = log.Reserve(NodeRecord.AlignedLength(original.Span.Length), out LogBuffer buffer, out int offset);
            original.Span.CopyTo(buffer.AsSpan(offset));
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset + NodeRecord.HeaderLength + ordinal * NodeRecord.PointerLength), pointer);
            return copy;
        }
    }

    [Test]
    public void Traversal_prunes_inherited_subtrees_at_an_inclusive_same_generation_boundary([Values] bool canParallelize)
    {
        using TrieNodeLog log = new(0, SmallGenerationBytes);
        using TrieNodeLogRetain originalRetain = log.RetainActive();
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), new WarmedNodes(1), null);
        Hash256 inheritedKey = new("1000000000000000000000000000000000000000000000000000000000000000");
        Hash256 updatedKey = new("f000000000000000000000000000000000000000000000000000000000000000");
        TrieRoot original = BulkSet(log, source, null, TrieRoot.Empty, [(inheritedKey, ExistingValue), (updatedKey, ExistingValue)], canParallelize);
        Assert.That(log.TryGetNodePointer(original.Pointer, TreePath.Empty.Append(1), out ulong inherited), Is.True);
        using TrieNodeLogRetain updateRetain = log.RetainActive();
        TrieRoot updated = BulkSet(log, source, null, original, [(updatedKey, NewValue)], canParallelize);
        (TreePath Path, ulong Pointer)[] delta = log.EnumerateNodes(updated.Pointer, updateRetain.WriteBoundary).Select(node => (node.Path, node.Pointer)).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated.Pointer >> 32, Is.EqualTo(original.Pointer >> 32));
            Assert.That(delta.Select(node => node.Path), Is.EqualTo(new[] { TreePath.Empty, TreePath.Empty.Append(15) }));
            Assert.That(delta.Min(node => node.Pointer), Is.EqualTo(updateRetain.WriteBoundary), "the first reservation belongs to this range");
            Assert.That(log.EnumerateNodes(original.Pointer, updateRetain.WriteBoundary), Is.Empty);
            Assert.That(log.TryGetNodePointer(updated.Pointer, TreePath.Empty.Append(1), out ulong found), Is.True);
            Assert.That(found, Is.EqualTo(inherited), "point reads still follow inherited records");
            Assert.That(log.GetRecord(found).Rlp.ToArray(), Is.Not.Empty);
            Assert.That(source.FetchCount, Is.Zero);
        }
    }

    [Test]
    public void Traversal_stops_at_unknown_child_pointers_without_loading_the_source()
    {
        using TrieNodeLog log = new(0, SmallGenerationBytes);
        using TrieNodeLogRetain retain = log.RetainActive();
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), new WarmedNodes(1), null);
        TrieRoot root = BulkSet(log, source, null, TrieRoot.Empty,
        [
            (new Hash256("1000000000000000000000000000000000000000000000000000000000000000"), ExistingValue),
            (new Hash256("2000000000000000000000000000000000000000000000000000000000000000"), ExistingValue),
        ], false);
        NodeRecordRef rootRecord = log.GetRecord(root.Pointer);
        ulong unlinked = log.AppendRecordFromRlp(null, TreePath.Empty, rootRecord.Hash, rootRecord.Rlp);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(log.EnumerateNodes(unlinked).Select(node => (node.Path, node.Pointer)).ToArray(), Is.EqualTo(new[] { (TreePath.Empty, unlinked) }));
            Assert.That(log.TryGetNodePointer(unlinked, TreePath.Empty, out ulong found), Is.True);
            Assert.That(found, Is.EqualTo(unlinked));
            Assert.That(log.TryGetNodePointer(unlinked, TreePath.FromHexString("1"), out _), Is.False);
            Assert.That(log.TryGetNode(unlinked, TreePath.FromHexString("2"), out _), Is.False);
            Assert.That(log.EnumerateNodes(0), Is.Empty);
            Assert.That(log.TryGetNodePointer(0, TreePath.Empty, out _), Is.False);
            Assert.That(log.TryGetNode(0, TreePath.Empty, out _), Is.False);
            Assert.That(source.FetchCount, Is.Zero);
        }
    }

    [Test]
    public void Traversal_skips_expired_inherited_children_but_keeps_the_new_root()
    {
        using TrieNodeLog log = new(0, SmallGenerationBytes);
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), new WarmedNodes(1), null);
        Hash256 first = new("1000000000000000000000000000000000000000000000000000000000000000");
        Hash256 second = new("2000000000000000000000000000000000000000000000000000000000000000");
        log.Reserve(0, out LogBuffer original, out _);
        TrieRoot root;
        using (log.RetainActive()) root = BulkSet(log, source, null, TrieRoot.Empty, [(first, ExistingValue), (second, ExistingValue)], false);
        log.Reserve(SmallGenerationBytes, out _, out _);
        using TrieNodeLogRetain retain = log.RetainActive();
        TrieRoot updated = BulkSet(log, source, null, root, [(first, NewValue)], false);
        while (TrieNodeLogTests.GenerationOf(log.Reserve(SmallGenerationBytes, out _, out _)) < TrieNodeLogTests.LiveGenerations + 1) { }
        Assert.That(() => original.IsDisposed, Is.True.After(TrieNodeLogTests.CompactionTimeoutMilliseconds, TrieNodeLogTests.PollingMilliseconds));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(log.TryGetNode(updated.Pointer, TreePath.Empty, out _), Is.True);
            Assert.That(log.TryGetNode(updated.Pointer, TreePath.FromHexString("1"), out _), Is.True);
            Assert.That(log.TryGetNode(updated.Pointer, TreePath.FromHexString("2"), out _), Is.False);
            Assert.That(log.EnumerateNodes(updated.Pointer).Select(node => node.Path), Is.EqualTo(new[] { TreePath.Empty, TreePath.FromHexString("1") }));
            Assert.That(log.EnumerateNodes(root.Pointer), Is.Empty);
            Assert.That(log.TryGetNode(root.Pointer, TreePath.Empty, out _), Is.False);
            Assert.That(source.FetchCount, Is.Zero);
        }
    }

    [Test]
    public void Traversal_handles_branches_at_every_key_depth()
    {
        using TrieNodeLog log = new(0, SmallGenerationBytes);
        using TrieNodeLogRetain retain = log.RetainActive();
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), new WarmedNodes(1), null);
        List<(Hash256 key, byte[] value)> items = [(Keccak.Zero, ExistingValue)];
        for (int depth = 0; depth < 64; depth++)
        {
            byte[] key = new byte[32];
            key[depth / 2] = (byte)((depth & 1) == 0 ? 0x10 : 0x01);
            items.Add((new Hash256(key), ExistingValue));
        }
        TrieRoot root = BulkSet(log, source, null, TrieRoot.Empty, items, false);
        byte[] expectedLeafRlp = new byte[NodeRlp.LeafLength([], ExistingValue)];
        NodeRlp.WriteLeaf([], ExistingValue, expectedLeafRlp);
        using (Assert.EnterMultipleScope())
        {
            IEnumerable<TreePath> expected = Enumerable.Range(0, 64).Select(depth => TreePath.FromHexString(new string('0', depth)))
                .Concat(Enumerable.Range(0, 64).Select(depth => TreePath.FromHexString(new string('0', depth) + "1")))
                .Append(TreePath.FromHexString(new string('0', 64)));
            Assert.That(log.EnumerateNodes(root.Pointer).Select(node => node.Path), Is.EquivalentTo(expected));
            Assert.That(log.TryGetNode(root.Pointer, TreePath.FromHexString(new string('0', 64)), out TrieNode leaf), Is.True);
            Assert.That(leaf?.FullRlp.ToArray(), Is.EqualTo(expectedLeafRlp));
        }
    }

    [Test]
    public void Traversal_enumerator_keeps_native_records_alive_until_completion_or_disposal([Values] bool complete, [Values] bool compressed)
    {
        using TrieNodeLog log = new(64 * 1024, SmallGenerationBytes);
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), new WarmedNodes(1), null);
        log.Reserve(0, out LogBuffer original, out _);
        TrieRoot root;
        using (log.RetainActive())
        {
            root = BulkSet(log, source, null, TrieRoot.Empty, PatriciaTreeBulkSetterTests.GenRandomOfLength(16, seed: 6), false);
        }
        if (compressed)
        {
            while (TrieNodeLogTests.GenerationOf(log.Reserve(SmallGenerationBytes, out _, out _)) < TrieNodeLogTests.LiveGenerations + 1) { }
            Assert.That(() => original.IsDisposed, Is.True.After(TrieNodeLogTests.CompactionTimeoutMilliseconds, TrieNodeLogTests.PollingMilliseconds));
        }

        int expectedCount = log.EnumerateNodes(root.Pointer).Count();
        using IEnumerator<(TreePath Path, ulong Pointer, NodeRecordRef Record)> enumerator = log.EnumerateNodes(root.Pointer).GetEnumerator();
        Assert.That(enumerator.MoveNext(), Is.True);
        Assert.That(log.TryLocate(enumerator.Current.Pointer, out NodeRecordRef borrowed), Is.True);
        byte[] expected = borrowed.Rlp.ToArray();
        log.Dispose();
        Assert.That(borrowed.Rlp.ToArray(), Is.EqualTo(expected), "the lease spans yielded pointers");
        if (complete)
        {
            int count = 1;
            while (enumerator.MoveNext()) count++;
            Assert.That(count, Is.EqualTo(expectedCount));
        }
        else enumerator.Dispose();
        enumerator.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => borrowed.Rlp.ToArray(), Throws.InstanceOf<ObjectDisposedException>(), "completion or early disposal releases the operation");
            Assert.That(() => log.TryGetNodePointer(root.Pointer, TreePath.Empty, out _), Throws.InstanceOf<ObjectDisposedException>());
        }
    }

    [Test]
    public void Traversal_keeps_retired_pointers_lookupable_until_enumerator_disposal([Values] bool compressed)
    {
        const int generationBytes = 4096;
        using TrieNodeLog log = new(compressed ? 2048UL : 0, generationBytes);
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), new WarmedNodes(1), null);
        log.Reserve(0, out LogBuffer original, out _);
        TrieRoot root;
        using (log.RetainActive()) root = BulkSet(log, source, null, TrieRoot.Empty, [(TestItem.KeccakA, FillerValue)], false);
        if (compressed)
        {
            AdvanceTo(TrieNodeLogTests.LiveGenerations + 1);
            Assert.That(() => original.IsDisposed, Is.True.After(TrieNodeLogTests.CompactionTimeoutMilliseconds, TrieNodeLogTests.PollingMilliseconds));
        }

        using IEnumerator<(TreePath Path, ulong Pointer, NodeRecordRef Record)> enumerator = log.EnumerateNodes(root.Pointer).GetEnumerator();
        Assert.That(enumerator.MoveNext(), Is.True);
        NodeRecordRef borrowed = enumerator.Current.Record;
        byte[] expected = borrowed.Rlp.ToArray();
        ulong compactedPointer = root.Pointer;
        if (compressed)
        {
            compactedPointer = log.AppendRecordFromRlp(TestItem.KeccakB, TreePath.Empty, borrowed.Hash, borrowed.Rlp);
            AdvanceTo(TrieNodeLogTests.GenerationOf(compactedPointer) + TrieNodeLogTests.LiveGenerations);
        }
        else AdvanceTo(TrieNodeLogTests.LiveGenerations + 1);
        Assert.That(() => log.FindBloom(compactedPointer) is null, Is.True.After(TrieNodeLogTests.CompactionTimeoutMilliseconds, TrieNodeLogTests.PollingMilliseconds));
        // Synchronize with completion of the compactor's retirement/eviction under the log lock.
        using (log.BeginRead())
        using (Assert.EnterMultipleScope())
        {
            Assert.That(borrowed.Rlp.ToArray(), Is.EqualTo(expected));
            Assert.That(log.GetRecord(root.Pointer).Rlp.ToArray(), Is.EqualTo(expected), "yielded pointers remain lookupable while enumeration is active");
            Assert.That(log.TryGetNode(root.Pointer, TreePath.Empty, out _), Is.True);
        }
        enumerator.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => borrowed.Rlp.ToArray(), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(log.TryGetNode(root.Pointer, TreePath.Empty, out _), Is.False, "new operations do not restore an already evicted pointer");
        }

        void AdvanceTo(ulong generation)
        {
            while (TrieNodeLogTests.GenerationOf(log.Reserve(generationBytes, out _, out _)) < generation) { }
        }
    }

    [Test]
    public void Traversal_node_lookup_returns_owned_rlp()
    {
        using TrieNodeLog log = new(0, SmallGenerationBytes);
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), new WarmedNodes(1), null);
        TrieRoot root;
        using (log.RetainActive()) root = BulkSet(log, source, null, TrieRoot.Empty, [(TestItem.KeccakA, ExistingValue)], false);
        Assert.That(log.TryGetNode(root.Pointer, TreePath.Empty, out TrieNode node), Is.True);
        byte[] expected = node.FullRlp.ToArray();
        log.Dispose();
        Assert.That(node.FullRlp.ToArray(), Is.EqualTo(expected));
    }

    [Test]
    public void Traversal_records_can_be_buffered_under_an_outer_read_lease()
    {
        using TrieNodeLog log = new(0, SmallGenerationBytes);
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), new WarmedNodes(1), null);
        TrieRoot root;
        using (log.RetainActive()) root = BulkSet(log, source, null, TrieRoot.Empty, [(TestItem.KeccakA, ExistingValue)], false);
        NodeRecordRef borrowed;
        byte[] expected;
        using (log.BeginRead())
        {
            NodeRecordRef[] buffered = log.EnumerateNodes(root.Pointer).Select(node => node.Record).ToArray();
            borrowed = buffered[0];
            expected = borrowed.Rlp.ToArray();
            log.Dispose();
            Assert.That(borrowed.Rlp.ToArray(), Is.EqualTo(expected), "the outer lease covers records after enumerator disposal");
        }
        Assert.That(() => borrowed.Rlp.ToArray(), Throws.InstanceOf<ObjectDisposedException>());
    }

    [Test]
    public void Traversal_cannot_start_after_log_disposal()
    {
        using TrieNodeLog log = new(0, SmallGenerationBytes);
        log.Dispose();
        Assert.That(() => log.EnumerateNodes(0).GetEnumerator(), Throws.InstanceOf<ObjectDisposedException>());
    }

    [Test]
    public void Traversal_foreach_releases_its_lease_when_the_consumer_throws()
    {
        using TrieNodeLog log = new(0, SmallGenerationBytes);
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), new WarmedNodes(1), null);
        TrieRoot root;
        using (log.RetainActive()) root = BulkSet(log, source, null, TrieRoot.Empty, [(TestItem.KeccakA, ExistingValue)], false);
        log.Reserve(0, out LogBuffer buffer, out _);
        Assert.That(() =>
        {
            foreach ((TreePath _, ulong _, NodeRecordRef _) in log.EnumerateNodes(root.Pointer))
            {
                log.Dispose();
                Assert.That(buffer.IsDisposed, Is.False);
                throw new InvalidOperationException("consumer failed");
            }
        }, Throws.InstanceOf<InvalidOperationException>());
        Assert.That(buffer.IsDisposed, Is.True);
    }

    [Test]
    public void Bulk_set_keeps_pending_native_values_alive_until_commit_when_their_generation_is_evicted()
    {
        const int generationBytes = 4096;
        using TrieNodeLog log = new(1800, generationBytes);
        DictionaryNodeSource empty = new(new Dictionary<TreePath, byte[]>(), new WarmedNodes(1), null);
        Hash256 earlyKey = new("1000000000000000000000000000000000000000000000000000000000000000");
        Hash256 addedKey = new("1100000000000000000000000000000000000000000000000000000000000000");
        Hash256 lateKey = new("f000000000000000000000000000000000000000000000000000000000000000");
        TrieRoot root;
        Dictionary<TreePath, byte[]> persisted;
        ulong earlyPointer;
        log.Reserve(0, out LogBuffer original, out _);
        using (log.RetainActive())
        {
            root = BulkSet(log, empty, null, TrieRoot.Empty, [(earlyKey, FillerValue), (lateKey, FillerValue)], canParallelize: false);
            persisted = Rlps(log, root);
            Assert.That(log.TryGetNodePointer(root.Pointer, TreePath.Empty.Append(1), out earlyPointer), Is.True);
        }

        log.Reserve(generationBytes, out _, out _);
        using TrieNodeLogRetain laterRetain = log.RetainActive();
        TreePath latePath = TreePath.Empty.Append(15);
        byte[] lateRlp = persisted[latePath];
        log.AppendRecordFromRlp(null, latePath, ValueKeccak.Compute(lateRlp), lateRlp);
        while (TrieNodeLogTests.GenerationOf(log.Reserve(generationBytes, out _, out _)) < TrieNodeLogTests.LiveGenerations + 3) { }
        Assert.That(() => original.IsDisposed, Is.True.After(TrieNodeLogTests.CompactionTimeoutMilliseconds, TrieNodeLogTests.PollingMilliseconds));

        NodeRecordRef borrowed;
        using (log.BeginRead()) Assert.That(log.TryLocate(earlyPointer, out borrowed), Is.True);
        DictionaryNodeSource persistedSource = new(persisted, new WarmedNodes(persisted.Count), null);
        CallbackNodeSource source = new(persistedSource, () =>
        {
            // The early leaf's value is pending when loading the later, rewritten leaf falls back to persistence.
            laterRetain.Dispose();
            Assert.That(() => log.FindBloom(laterRetain.Generation << TrieNodeLog.OffsetBits) is null, Is.True.After(TrieNodeLogTests.CompactionTimeoutMilliseconds, TrieNodeLogTests.PollingMilliseconds));
            using (log.BeginRead())
            using (Assert.EnterMultipleScope())
            {
                Assert.That(log.TryLocate(root.Pointer, out _), Is.False, "the root was already missing before this operation");
                Assert.That(log.TryLocate(earlyPointer, out _), Is.True, "the bulk-set reader preserves a record retired during the operation");
            }
            Assert.That(borrowed.LeafValue.Span.ToArray(), Is.EqualTo(FillerValue));
        });
        using TrieNodeLogRetain current = log.RetainActive();
        TrieRoot updated = BulkSet(log, source, null, root, [(addedKey, NewValue), (lateKey, NewValue)], canParallelize: false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.Called, Is.True);
            Assert.That(LogBulkSet.TryGet(log, persistedSource, null, updated, earlyKey.ValueHash256), Is.EqualTo(FillerValue));
            Assert.That(LogBulkSet.TryGet(log, persistedSource, null, updated, addedKey.ValueHash256), Is.EqualTo(NewValue));
            Assert.That(LogBulkSet.TryGet(log, persistedSource, null, updated, lateKey.ValueHash256), Is.EqualTo(NewValue));
            Assert.That(() => borrowed.Rlp.ToArray(), Throws.InstanceOf<ObjectDisposedException>(), "bulk-set completion freed the evicted compressed owner");
        }
    }

    private sealed class CallbackNodeSource(INodeRecordSource source, Action beforeLoad) : INodeRecordSource
    {
        public bool Called { get; private set; }

        public bool TryLoad(in TreePath path, in ValueHash256 hash, out NodeRecordRef record)
        {
            if (!Called)
            {
                Called = true;
                beforeLoad();
            }
            return source.TryLoad(path, hash, out record);
        }
    }

    private static TrieRoot BulkSet(TrieNodeLog log, INodeRecordSource source, Hash256 address, TrieRoot root, List<(Hash256 key, byte[] value)> items, bool canParallelize)
    {
        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = Entries(items);
        return LogBulkSet.BulkSet(log, source, address, root, entries, canParallelize);
    }

    private static ArrayPoolListRef<PatriciaTree.BulkSetEntry> Entries(List<(Hash256 key, byte[] value)> items)
    {
        ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(items.Count);
        foreach ((Hash256 key, byte[] value) in items) entries.Add(new PatriciaTree.BulkSetEntry(key.ValueHash256, value));
        return entries;
    }

    private static (Hash256 Root, Dictionary<TreePath, byte[]> Nodes) BuildExpectedTrie(IReadOnlyDictionary<Hash256, byte[]> values)
    {
        CommitRecordingTrieStore store = new();
        PatriciaTree tree = new(new ScopedTrieStore(store, null), LimboLogs.Instance);
        foreach ((Hash256 key, byte[] value) in values) tree.Set(key.Bytes, value);
        tree.Commit();
        return (tree.RootHash, store.TakeCommitted());
    }

    private static Dictionary<TreePath, byte[]> Rlps(TrieNodeLog log, TrieRoot root) =>
        log.EnumerateNodes(root.Pointer).ToDictionary(node => node.Path, node => node.Record.Rlp.ToArray());

    private static IEnumerable<string> InvalidRecords(TrieNodeLog log, TrieRoot root)
    {
        List<string> invalid = [];
        foreach ((TreePath path, _, NodeRecordRef record) in log.EnumerateNodes(root.Pointer))
        {
            byte[] rlp = record.Rlp.ToArray();
            bool parses = NodeRecord.TryMeasure(rlp, out NodeRecordKind kind, out ushort hashMask);
            if (ValueKeccak.Compute(rlp) != record.Hash || !parses || kind != record.Kind || hashMask != record.View.HashMask
                || record.View.NodeKey != NodeRecord.NodeKey(null, path))
            {
                invalid.Add(path.ToString());
            }
        }

        return invalid;
    }

    /// <summary>Keeps the path and RLP of every node committed since the last <see cref="TakeCommitted"/>.</summary>
    private sealed class CommitRecordingTrieStore() : RawTrieStore(new MemDb())
    {
        private Dictionary<TreePath, byte[]> _committed = [];

        public Dictionary<TreePath, byte[]> TakeCommitted()
        {
            Dictionary<TreePath, byte[]> committed = _committed;
            _committed = [];
            return committed;
        }

        public override ICommitter BeginCommit(Hash256 address, TrieNode root, WriteFlags writeFlags) =>
            new RecordingCommitter(_committed, base.BeginCommit(address, root, writeFlags));
    }

    private sealed class RecordingCommitter(Dictionary<TreePath, byte[]> committed, ICommitter committer) : ICommitter
    {
        public TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            committed[path] = node.FullRlp.ToArray();
            return committer.CommitNode(ref path, node);
        }

        public void Dispose() => committer.Dispose();
    }
}

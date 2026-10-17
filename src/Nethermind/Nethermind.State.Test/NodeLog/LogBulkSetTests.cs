// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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
    public void Root_and_written_nodes_match_patricia_tree(List<(Hash256 key, byte[] value)> existingItems, List<(Hash256 key, byte[] value)> items, bool smallGenerations, bool fromSource, bool canParallelize)
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
        WarmedNodes warmed = new(existingNodes.Count);
        DictionaryNodeSource source = new(existingNodes, warmed, null);
        using WrittenNodes existingWritten = new();
        using WrittenNodes written = new();
        TrieRoot existingRoot = BulkSet(existingLog, source, null, TrieRoot.Empty, existingItems, existingWritten, canParallelize);
        // From the source, the existing trie stands for one persisted before this log was created.
        using TrieNodeLog log = fromSource ? new(0, generationBytes) : existingLog;
        using TrieNodeLogRetain retain = log.RetainActive();
        ulong temporaryRootPointer = fromSource ? InsertLinkedRecords(warmed, existingNodes) : 0;
        TrieRoot root = BulkSet(log, source, null, fromSource ? new TrieRoot(expectedExistingRoot.ValueHash256, temporaryRootPointer) : existingRoot, items, written, canParallelize);
        int fetchCount = source.FetchCount;

        Dictionary<Hash256, byte[]> expectedValues = [];
        foreach ((Hash256 key, byte[] value) in existingItems.Concat(items))
        {
            if (value is null || value.Length == 0) expectedValues.Remove(key);
            else expectedValues[key] = value;
        }

        IEnumerable<Hash256> keys = existingItems.Concat(items).Select(item => item.key).Distinct();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(existingRoot.Hash, Is.EqualTo(expectedExistingRoot.ValueHash256), "existing root");
            Assert.That(root.Hash, Is.EqualTo(tree.RootHash.ValueHash256), "root");
            Assert.That(NodeRecord.IsTransient(root.Pointer), Is.False, "returned root");
            Assert.That(LogBulkSet.ResolveRoot(log, null, root.Hash).Pointer, Is.EqualTo(root.Pointer), "indexed root");
            Assert.That(InvalidRecords(existingLog, existingWritten).Concat(InvalidRecords(log, written)), Is.Empty, "written records hash to their hash and parse");
            Assert.That(Rlps(log, written), Is.EquivalentTo(expectedWritten), "written nodes");
            if (!fromSource) Assert.That(fetchCount, Is.Zero, "nodes the first bulk set wrote are read from the log");
            Assert.That(keys.Select(key => LogBulkSet.TryGet(log, source, null, root, key.ValueHash256)), Is.EqualTo(keys.Select(key => expectedValues.GetValueOrDefault(key))), "values");
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
        using WrittenNodes existingWritten = new();
        using WrittenNodes written = new();
        TrieRoot root = BulkSet(log, source, null, TrieRoot.Empty, existingKeys.Select(key => (new Hash256(key), ExistingValue)).ToList(), existingWritten, canParallelize: false);

        TrieRoot after = BulkSet(log, source, null, root, keys.Select(key => (new Hash256(key), existingKeys.Contains(key) ? ExistingValue : null)).ToList(), written, canParallelize: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(after.Hash, Is.EqualTo(root.Hash), "root hash");
            Assert.That(after.Pointer, Is.EqualTo(root.Pointer), "root pointer");
            Assert.That(written.Count, Is.Zero, "written nodes");
        }
    }

    [Test]
    public void No_op_batches_clear_temporary_root_pointers_without_writing([Values] bool emptyBatch)
    {
        Hash256 key = TestItem.KeccakA;
        using TrieNodeLog original = new(0, SmallGenerationBytes);
        using TrieNodeLogRetain originalRetain = original.RetainActive();
        using TrieNodeLog log = new(0, SmallGenerationBytes);
        using TrieNodeLogRetain retain = log.RetainActive();
        using WrittenNodes existingWritten = new();
        using WrittenNodes written = new();
        WarmedNodes warmed = new(1);
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), warmed, null);
        TrieRoot root = BulkSet(original, source, null, TrieRoot.Empty, [(key, ExistingValue)], existingWritten, false);
        byte[] rlp = original.GetRecord(root.Pointer).Rlp.ToArray();
        warmed.TryInsert(null, TreePath.Empty, root.Hash, rlp, out _, out ulong pointer);
        byte[] otherRlp = TrieNodeLogTests.Leaf(1, 8);
        warmed.TryInsert(null, TreePath.Empty, ValueKeccak.Compute(otherRlp), otherRlp, out _);
        TrieRoot temporaryRoot = new(root.Hash, pointer);
        Assert.That(LogBulkSet.TryGet(log, source, null, temporaryRoot, key.ValueHash256), Is.EqualTo(ExistingValue), "point reads use the displaced root's pointer");

        TrieRoot after = BulkSet(log, source, null, temporaryRoot, emptyBatch ? [] : [(key, ExistingValue)], written, false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(after.Hash, Is.EqualTo(root.Hash));
            Assert.That(after.Pointer, Is.Zero);
            Assert.That(written.Count, Is.Zero);
            Assert.That(source.FetchCount, Is.Zero, "root loading resolves the supplied temporary pointer");
        }
    }

    [Test]
    public void Source_pointer_misses_fall_back_to_verified_persistence([Values] bool foreignResource)
    {
        byte[] rlp = TrieNodeLogTests.Leaf(0, 8);
        ValueHash256 hash = ValueKeccak.Compute(rlp);
        WarmedNodes original = new(1);
        original.TryInsert(null, TreePath.Empty, hash, rlp, out _, out ulong pointer);
        WarmedNodes warmed = foreignResource ? new WarmedNodes(1) : original;
        if (!foreignResource) warmed.Reset();
        byte[] otherRlp = TrieNodeLogTests.Leaf(1, 8);
        ValueHash256 otherHash = ValueKeccak.Compute(otherRlp);
        warmed.TryInsert(null, TreePath.Empty, otherHash, otherRlp, out _, out ulong currentPointer);
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]> { [TreePath.Empty] = rlp }, warmed, null);

        bool loaded = source.TryLoad(pointer, TreePath.Empty, hash, out NodeRecordRef record);
        int fetches = source.FetchCount;
        bool loadedDespiteWrongHash = source.TryLoad(currentPointer, TreePath.Empty, hash, out NodeRecordRef correctRecord);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(loaded, Is.True);
            Assert.That(record.Rlp.ToArray(), Is.EqualTo(rlp));
            Assert.That(fetches, Is.EqualTo(1), "foreign and pre-reset pointers fall back to persistence");
            Assert.That(loadedDespiteWrongHash, Is.True);
            Assert.That(correctRecord.Rlp.ToArray(), Is.EqualTo(rlp), "a current pointer with the wrong hash falls back to the path index");
            Assert.That(source.FetchCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void Commit_clears_only_temporary_children_and_remains_readable_after_reset([Values] bool compact)
    {
        Hash256 keyA = new("1000000000000000000000000000000000000000000000000000000000000000");
        Hash256 keyB = new("2000000000000000000000000000000000000000000000000000000000000000");
        Hash256 keyC = new("3000000000000000000000000000000000000000000000000000000000000000");
        List<(Hash256 key, byte[] value)> items = [(keyA, ExistingValue), (keyB, ExistingValue), (keyC, ExistingValue)];
        using TrieNodeLog log = new(1UL << 20, SmallGenerationBytes);
        using TrieNodeLogRetain retain = log.RetainActive();
        using WrittenNodes existingWritten = new();
        using WrittenNodes written = new();
        WarmedNodes warmed = new(16);
        DictionaryNodeSource empty = new(new Dictionary<TreePath, byte[]>(), warmed, null);
        TrieRoot root = BulkSet(log, empty, null, TrieRoot.Empty, items, existingWritten, false);
        Dictionary<TreePath, byte[]> nodes = Rlps(log, existingWritten);
        ulong realChildPointer = log.GetRecord(root.Pointer).View.Child(1).Pointer;
        ValueHash256 unchangedHash = log.GetRecord(root.Pointer).View.Child(2).Data;
        byte[] rootRlp = nodes[TreePath.Empty];
        NodeRecord.TryMeasure(rootRlp, out NodeRecordKind kind, out ushort mask);
        Span<ulong> childPointers = stackalloc ulong[16];
        for (int nibble = 1; nibble <= 3; nibble++)
        {
            TreePath path = TreePath.Empty.Append(nibble);
            byte[] rlp = nodes[path];
            warmed.TryInsert(null, path, ValueKeccak.Compute(rlp), rlp, out _, out ulong pointer);
            childPointers[nibble] = nibble == 1 ? realChildPointer : pointer;
        }
        warmed.TryInsertVerified(null, TreePath.Empty, root.Hash, rootRlp, kind, mask, childPointers, out _, out ulong temporaryRootPointer);
        byte[] displacedRlp = TrieNodeLogTests.Leaf(1, 8);
        warmed.TryInsert(null, TreePath.Empty.Append(3), ValueKeccak.Compute(displacedRlp), displacedRlp, out _);
        DictionaryNodeSource source = new(nodes, warmed, null);

        TrieRoot after = BulkSet(log, source, null, new TrieRoot(root.Hash, temporaryRootPointer), [(keyC, NewValue)], written, false);
        ChildRef realChild = log.GetRecord(after.Pointer).View.Child(1);
        ChildRef transientChild = log.GetRecord(after.Pointer).View.Child(2);
        PatriciaTree expected = new(new RawScopedTrieStore(new MemDb()), LimboLogs.Instance);
        foreach ((Hash256 key, byte[] value) in items) expected.Set(key.Bytes, key == keyC ? NewValue : value);
        expected.UpdateRootHash();
        warmed.Reset();
        if (compact)
        {
            retain.Dispose();
            for (int i = 0; i < TrieNodeLogTests.LiveGenerations + 2; i++) log.Reserve(SmallGenerationBytes, out _, out _);
            Assert.That(() => log.FindBloom(after.Pointer) is null && TrieNodeLogTests.IsLocatable(log, after.Pointer),
                Is.True.After(TrieNodeLogTests.CompactionTimeoutMilliseconds, TrieNodeLogTests.PollingMilliseconds), "normalized output survives compaction");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(after.Hash, Is.EqualTo(expected.RootHash.ValueHash256));
            Assert.That(realChild.Pointer, Is.EqualTo(realChildPointer), "real pointers remain unchanged");
            Assert.That(transientChild.Pointer, Is.Zero, "unchanged temporary child is not promoted");
            Assert.That(transientChild.Data, Is.EqualTo(unchangedHash), "its hash remains unchanged");
            Assert.That(InvalidRecords(log, written), Is.Empty);
            Assert.That(LogBulkSet.ResolveRoot(log, null, after.Hash).Pointer, Is.EqualTo(after.Pointer));
            Assert.That(LogBulkSet.TryGet(log, source, null, after, keyA.ValueHash256), Is.EqualTo(ExistingValue));
            Assert.That(LogBulkSet.TryGet(log, source, null, after, keyB.ValueHash256), Is.EqualTo(ExistingValue));
            Assert.That(LogBulkSet.TryGet(log, source, null, after, keyC.ValueHash256), Is.EqualTo(NewValue));
            Assert.That(source.FetchCount, Is.EqualTo(1), "only the cleared temporary pointer falls back after reset");
        }
    }

    [Test]
    public void WarmUp_fetches_only_nodes_outside_the_log_and_bulk_set_then_reads_them_without_fetching()
    {
        Hash256 address = TestItem.KeccakA;
        List<(Hash256 key, byte[] value)> items = PatriciaTreeBulkSetterTests.GenRandomOfLength(1000, seed: 4);
        using TrieNodeLog log = new(0, SmallGenerationBytes);
        using TrieNodeLogRetain retain = log.RetainActive();
        using WrittenNodes written = new();
        TrieRoot root = BulkSet(log, new DictionaryNodeSource(new Dictionary<TreePath, byte[]>(), new WarmedNodes(1), address), address, TrieRoot.Empty, items, written, canParallelize: false);

        // A log without the trie stands for one written before the trie was persisted.
        using TrieNodeLog persistedLog = new(0, SmallGenerationBytes);
        TrieRoot persistedRoot = LogBulkSet.ResolveRoot(persistedLog, address, root.Hash);
        DictionaryNodeSource source = new(Rlps(log, written), new WarmedNodes(written.Count), address);
        int fromLog = WarmUp(log, root, items[0].key);
        int fromSource = WarmUp(persistedLog, persistedRoot, items[0].key);
        int again = WarmUp(persistedLog, persistedRoot, items[0].key);
        foreach ((Hash256 key, _) in items) WarmUp(persistedLog, persistedRoot, key);
        int fetchesBeforeBulkSet = source.FetchCount;
        using WrittenNodes newWritten = new();
        TrieRoot newRoot = BulkSet(persistedLog, source, address, persistedRoot, [(items[0].key, NewValue)], newWritten, canParallelize: false);

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
            source.WarmUp(walkedLog, walkedRoot, key.ValueHash256);
            return source.FetchCount - before;
        }
    }

    [Test]
    public void Warm_up_publishes_a_linked_verified_prefix_only_after_descent([Values(0, 1, 2)] int missingLeaf)
    {
        Hash256 keyA = new("1000000000000000000000000000000000000000000000000000000000000001");
        Hash256 keyB = new("1000000000000000000000000000000000000000000000000000000000000002");
        Hash256 keyC = new("2000000000000000000000000000000000000000000000000000000000000000");
        using TrieNodeLog original = new(0, SmallGenerationBytes);
        using TrieNodeLogRetain retain = original.RetainActive();
        using WrittenNodes written = new();
        TrieRoot root = BulkSet(original, new DictionaryNodeSource(new Dictionary<TreePath, byte[]>(), new WarmedNodes(1), null),
            null, TrieRoot.Empty, [(keyA, ExistingValue), (keyB, ExistingValue), (keyC, ExistingValue)], written, false);
        Dictionary<TreePath, byte[]> nodes = Rlps(original, written);
        TreePath leafPath = TreePath.FromPath(keyA.Bytes);
        if (missingLeaf == 1) nodes.Remove(leafPath);
        if (missingLeaf == 2) nodes[leafPath] = TrieNodeLogTests.Leaf(9, 8);
        WarmedNodes warmed = new(16);
        int reads = 0;
        byte[] Load(in TreePath path, in ValueHash256 hash)
        {
            Assert.That(warmed.Count, Is.Zero, "descent must not publish zero-linked ancestors");
            reads++;
            return nodes.GetValueOrDefault(path);
        }
        using TrieNodeLog log = new(0, SmallGenerationBytes);

        LogBulkSet.WarmUp(log, warmed, Load, null, new TrieRoot(root.Hash, 0), keyA.ValueHash256);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reads, Is.EqualTo(4));
            Assert.That(warmed.Count, Is.EqualTo(missingLeaf == 0 ? 4 : 3));
        }
        TreePath currentPath = TreePath.Empty;
        ValueHash256 currentHash = root.Hash;
        int linked = 0;
        Span<byte> extensionKey = stackalloc byte[64];
        while (warmed.TryGet(null, currentPath, currentHash, out NodeRecordRef record, out ulong pointer))
        {
            linked++;
            Assert.That(ValueKeccak.Compute(record.Rlp), Is.EqualTo(record.Hash));
            Assert.That(NodeRecord.IsTransient(pointer), Is.True);
            if (record.Kind == NodeRecordKind.Leaf) break;
            int slot = record.Kind == NodeRecordKind.Extension ? 0 : leafPath[currentPath.Length];
            ChildRef child = record.View.Child(slot);
            if (currentPath.Length == 0) Assert.That(record.View.Child(2).Pointer, Is.Zero, "off-path subtrees remain unknown");
            if (child.Pointer == 0)
            {
                Assert.That(missingLeaf, Is.Not.Zero, "a complete warmed path must link every hashed child");
                break;
            }
            Assert.That(warmed.TryGet(child.Pointer, child.Data, out _), Is.True);
            Assert.That(child.Pointer, Is.LessThan(pointer), "the child's bytes were published first");
            if (record.Kind == NodeRecordKind.Extension)
            {
                record.View.CopyKey(0, extensionKey);
                currentPath.AppendMut(extensionKey[..record.View.KeyLength(0)]);
            }
            else currentPath.AppendMut(slot);
            currentHash = child.Data;
        }
        Assert.That(linked, Is.EqualTo(warmed.Count));
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
        using WrittenNodes written = new();
        TrieRoot root = BulkSet(original, new DictionaryNodeSource(new Dictionary<TreePath, byte[]>(), new WarmedNodes(1), null), null, TrieRoot.Empty, items, written, false);
        Dictionary<TreePath, byte[]> persisted = Rlps(original, written);
        WarmedNodes warmed = new(1024);
        DictionaryNodeSource source = new(persisted, warmed, null);
        using TrieNodeLog log = new(0, SmallGenerationBytes);
        using TrieNodeLogRetain newRetain = log.RetainActive();
        TrieRoot persistedRoot = new(root.Hash, 0);

        source.WarmUp(log, persistedRoot, keyA.ValueHash256, forDelete);
        TreePath branchPath = TreePath.FromPath(keyA.Bytes);
        branchPath.TruncateOne();
        Assert.That(warmed.TryGet(null, branchPath, ValueKeccak.Compute(persisted[branchPath]), out NodeRecordRef warmedBranch), Is.True);
        ChildRef warmedSibling = warmedBranch.View.Child(2);
        Assert.That(warmedSibling.Pointer, forDelete && !threeChildren ? Is.Not.Zero : Is.Zero, "only a required collapse sibling is linked");
        if (warmedSibling.Pointer != 0) Assert.That(warmed.TryGet(warmedSibling.Pointer, warmedSibling.Data, out _), Is.True);
        int before = source.FetchCount;
        using WrittenNodes changed = new();
        TrieRoot updated = BulkSet(log, source, null, persistedRoot, [(keyA, null)], changed, false);
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
        using WrittenNodes written = new();
        TrieRoot root = BulkSet(original, new DictionaryNodeSource(new Dictionary<TreePath, byte[]>(), new WarmedNodes(1), null), null, TrieRoot.Empty,
            [(keyA, ExistingValue), (keyB, ExistingValue)], written, false);
        Dictionary<TreePath, byte[]> nodes = Rlps(original, written);
        DictionaryNodeSource pathSource = new(nodes, new WarmedNodes(1024), null);
        DictionaryNodeSource deleteSource = new(nodes, new WarmedNodes(1024), null);
        using TrieNodeLog log = new(0, SmallGenerationBytes);
        TrieRoot persistedRoot = new(root.Hash, 0);

        pathSource.WarmUp(log, persistedRoot, missing.ValueHash256);
        deleteSource.WarmUp(log, persistedRoot, missing.ValueHash256, forDelete: true);

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
        using WrittenNodes written = new();
        using WrittenNodes rewriteWritten = new();
        using WrittenNodes fillerWritten = new();
        TrieRoot root = BulkSet(log, source, null, TrieRoot.Empty, items, written, canParallelize: false);
        (TreePath Path, ulong Pointer)[] nodes = written.AsSpan().ToArray();
        ulong newestTrieGeneration = nodes.Max(node => TrieNodeLogTests.GenerationOf(node.Pointer));
        FillUntil(newestTrieGeneration + 1);
        TrieRoot newRoot = BulkSet(log, source, null, root, [(items[0].key, NewValue)], rewriteWritten, canParallelize: false);
        // Few large filler leaves per generation keep false positives out of the blooms the dropped trie is compacted against.
        FillUntil(newestTrieGeneration + TrieNodeLogTests.LiveGenerations);
        Assert.That(() => TrieNodeLogTests.IsLocatable(log, root.Pointer), Is.False.After(TrieNodeLogTests.CompactionTimeoutMilliseconds, TrieNodeLogTests.PollingMilliseconds),
            "the generations of the trie are compacted");

        HashSet<TreePath> rewrittenPaths = [.. rewriteWritten.AsSpan().ToArray().Select(node => node.Path)];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(TrieNodeLogTests.GenerationOf(newRoot.Pointer), Is.GreaterThan(newestTrieGeneration), "generation of the rewrite");
            Assert.That(nodes.Select(node => TrieNodeLogTests.IsLocatable(log, node.Pointer)), Is.EqualTo(nodes.Select(node => !rewrittenPaths.Contains(node.Path))), "records resolve unless their path was rewritten");
            Assert.That(items.Select(item => LogBulkSet.TryGet(log, source, null, newRoot, item.key.ValueHash256)), Is.EqualTo(items.Select((item, index) => index == 0 ? NewValue : item.value)), "values");
            Assert.That(source.FetchCount, Is.Zero, "nodes read from the log");
        }

        void FillUntil(ulong generation)
        {
            while (fillerWritten.Count == 0 || TrieNodeLogTests.GenerationOf(fillerWritten.AsSpan()[^1].Pointer) < generation)
            {
                List<(Hash256 key, byte[] value)> filler = [.. Enumerable.Range(fillerWritten.Count, fillerBatch).Select(seed => (Keccak.Compute(BitConverter.GetBytes(seed)), FillerValue))];
                BulkSet(log, source, TestItem.KeccakA, TrieRoot.Empty, filler, fillerWritten, canParallelize: false);
            }
        }
    }

    [Test]
    public void Bulk_set_keeps_pending_native_values_alive_until_commit_when_their_generation_is_evicted()
    {
        const int generationBytes = 4096;
        using TrieNodeLog log = new(1800, generationBytes);
        using WrittenNodes written = new();
        using WrittenNodes changed = new();
        DictionaryNodeSource empty = new(new Dictionary<TreePath, byte[]>(), new WarmedNodes(1), null);
        Hash256 earlyKey = new("1000000000000000000000000000000000000000000000000000000000000000");
        Hash256 addedKey = new("1100000000000000000000000000000000000000000000000000000000000000");
        Hash256 lateKey = new("f000000000000000000000000000000000000000000000000000000000000000");
        TrieRoot root;
        Dictionary<TreePath, byte[]> persisted;
        log.Reserve(0, out LogBuffer original, out _);
        using (log.RetainActive())
        {
            root = BulkSet(log, empty, null, TrieRoot.Empty, [(earlyKey, FillerValue), (lateKey, FillerValue)], written, canParallelize: false);
            persisted = Rlps(log, written);
        }

        log.Reserve(generationBytes, out _, out _);
        using TrieNodeLogRetain laterRetain = log.RetainActive();
        TreePath latePath = TreePath.Empty.Append(15);
        byte[] lateRlp = persisted[latePath];
        log.AppendRecordFromRlp(null, latePath, ValueKeccak.Compute(lateRlp), lateRlp);
        while (TrieNodeLogTests.GenerationOf(log.Reserve(generationBytes, out _, out _)) < TrieNodeLogTests.LiveGenerations + 3) { }
        Assert.That(() => original.IsDisposed, Is.True.After(TrieNodeLogTests.CompactionTimeoutMilliseconds, TrieNodeLogTests.PollingMilliseconds));

        ulong earlyPointer = written.AsSpan().ToArray().Single(node => node.Path == TreePath.Empty.Append(1)).Pointer;
        NodeRecordRef borrowed;
        using (log.BeginRead()) Assert.That(log.TryLocate(earlyPointer, out borrowed), Is.True);
        DictionaryNodeSource persistedSource = new(persisted, new WarmedNodes(written.Count), null);
        CallbackNodeSource source = new(persistedSource, () =>
        {
            // The early leaf's value is pending when loading the later, rewritten leaf falls back to persistence.
            laterRetain.Dispose();
            Assert.That(() => TrieNodeLogTests.IsLocatable(log, root.Pointer), Is.False.After(TrieNodeLogTests.CompactionTimeoutMilliseconds, TrieNodeLogTests.PollingMilliseconds));
            Assert.That(borrowed.LeafValue.Span.ToArray(), Is.EqualTo(FillerValue));
        });
        using TrieNodeLogRetain current = log.RetainActive();
        TrieRoot updated = BulkSet(log, source, null, root, [(addedKey, NewValue), (lateKey, NewValue)], changed, canParallelize: false);
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

    private static ulong InsertLinkedRecords(WarmedNodes warmed, Dictionary<TreePath, byte[]> nodes)
    {
        Dictionary<TreePath, ulong> pointers = [];
        Span<byte> extensionKey = stackalloc byte[64];
        Span<ulong> childPointers = stackalloc ulong[16];
        foreach ((TreePath path, byte[] rlp) in nodes.OrderByDescending(node => node.Key.Length))
        {
            NodeRecord.TryMeasure(rlp, out NodeRecordKind kind, out ushort hashMask);
            byte[] bytes = new byte[NodeRecord.Length(BitOperations.PopCount(hashMask), rlp.Length)];
            NodeRecord.WriteFromRlp(rlp, kind, hashMask, ValueKeccak.Compute(rlp), NodeRecord.NodeKey(null, path), bytes);
            RecordView view = new(bytes);
            childPointers.Clear();
            for (int slot = 0; slot < 16; slot++)
            {
                if ((hashMask & (1 << slot)) == 0) continue;
                TreePath childPath;
                if (kind == NodeRecordKind.Extension)
                {
                    view.CopyKey(0, extensionKey);
                    childPath = path.Append(extensionKey[..view.KeyLength(0)]);
                }
                else childPath = path.Append(slot);

                childPointers[slot] = pointers[childPath];
            }

            warmed.TryInsertVerified(null, path, ValueKeccak.Compute(rlp), rlp, kind, hashMask, childPointers, out _, out ulong pointer);
            pointers[path] = pointer;
        }

        return pointers.GetValueOrDefault(TreePath.Empty);
    }

    private static TrieRoot BulkSet(TrieNodeLog log, INodeRecordSource source, Hash256 address, TrieRoot root, List<(Hash256 key, byte[] value)> items, WrittenNodes written, bool canParallelize)
    {
        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = Entries(items);
        return LogBulkSet.BulkSet(log, source, address, root, entries, written, canParallelize);
    }

    private static ArrayPoolListRef<PatriciaTree.BulkSetEntry> Entries(List<(Hash256 key, byte[] value)> items)
    {
        ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(items.Count);
        foreach ((Hash256 key, byte[] value) in items) entries.Add(new PatriciaTree.BulkSetEntry(key.ValueHash256, value));
        return entries;
    }

    private static Dictionary<TreePath, byte[]> Rlps(TrieNodeLog log, WrittenNodes written) =>
        written.AsSpan().ToArray().ToDictionary(node => node.Path, node => log.GetRecord(node.Pointer).Rlp.ToArray());

    private static IEnumerable<string> InvalidRecords(TrieNodeLog log, WrittenNodes written)
    {
        List<string> invalid = [];
        foreach ((TreePath path, ulong pointer) in written.AsSpan())
        {
            NodeRecordRef record = log.GetRecord(pointer);
            byte[] rlp = record.Rlp.ToArray();
            bool parses = NodeRecord.TryMeasure(rlp, out NodeRecordKind kind, out ushort hashMask);
            bool transientPointer = NodeRecord.IsTransient(pointer);
            for (int i = 0; i < BitOperations.PopCount(hashMask); i++)
            {
                transientPointer |= NodeRecord.IsTransient(BinaryPrimitives.ReadUInt64LittleEndian(record.Span[(NodeRecord.HeaderLength + i * NodeRecord.PointerLength)..]));
            }

            if (ValueKeccak.Compute(rlp) != record.Hash || !parses || kind != record.Kind || hashMask != record.View.HashMask
                || record.View.NodeKey != NodeRecord.NodeKey(null, path) || transientPointer)
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

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.State.Flat.Test;
using Nethermind.State.Flat.Test.NodeBuffer;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Store.Test.NodeBuffer;

internal class NodeRecordBulkSetTests
{
    private const int SmallGenerationBytes = 64 * 1024;

    private static readonly byte[] ExistingValue = Bytes.FromHexString("0x7777777777777777777777777777777777777777777777777777777777777777");
    private static readonly byte[] NewValue = Bytes.FromHexString("0x010203");

    private readonly List<WarmedNodeBuffer> _warmedNodeLogs = [];

    [TearDown]
    public void DisposeWarmedNodeBuffers()
    {
        foreach (WarmedNodeBuffer log in _warmedNodeLogs) log.Dispose();
        _warmedNodeLogs.Clear();
    }

    private WarmedNodeBuffer NewWarmedNodeBuffer(int capacity)
    {
        WarmedNodeBuffer log = new(capacity);
        _warmedNodeLogs.Add(log);
        return log;
    }

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

        int generationBytes = smallGenerations ? SmallGenerationBytes : NodeRecordBuffer.DefaultGenerationBytes;
        using NodeRecordBuffer existingBuffer = new(generationBytes);
        using NodeRecordBufferPin existingRetain = existingBuffer.PinHead();
        WarmedNodeBuffer warmed = NewWarmedNodeBuffer(existingNodes.Count);
        DictionaryNodeSource source = new(existingNodes, warmed, null);
        (ValueHash256 Hash, ulong Pointer) existingRoot = BulkSet(existingBuffer, source, null, (ValueKeccak.EmptyTreeHash, 0), existingItems, canParallelize);
        // From the source, the existing trie stands for one persisted before this buffer was created.
        using NodeRecordBuffer buffer = fromSource ? new(generationBytes) : existingBuffer;
        using NodeRecordBufferPin retain = buffer.PinHead();
        (ValueHash256 Hash, ulong Pointer) root = BulkSet(buffer, source, null, fromSource ? (expectedExistingRoot.ValueHash256, 0) : existingRoot, items, canParallelize);
        int fetchCount = source.FetchCount;

        Dictionary<Hash256, byte[]> expectedValues = [];
        foreach ((Hash256 key, byte[] value) in existingItems.Concat(items))
        {
            if (value is null || value.Length == 0) expectedValues.Remove(key);
            else expectedValues[key] = value;
        }

        (_, Dictionary<TreePath, byte[]> expectedReachable) = BuildExpectedTrie(expectedValues);
        Dictionary<TreePath, byte[]> expectedBufferNodes = fromSource
            ? expectedWritten.Where(node => expectedReachable.TryGetValue(node.Key, out byte[] rlp) && node.Value.AsSpan().SequenceEqual(rlp)).ToDictionary()
            : expectedReachable;
        IEnumerable<Hash256> keys = existingItems.Concat(items).Select(item => item.key).Distinct();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(existingRoot.Hash, Is.EqualTo(expectedExistingRoot.ValueHash256), "existing root");
            Assert.That(root.Hash, Is.EqualTo(tree.RootHash.ValueHash256), "root");
            Assert.That(NodeRecordCodec.IsTransient(root.Pointer), Is.False, "returned root");
            Assert.That(InvalidRecords(existingBuffer, existingRoot).Concat(InvalidRecords(buffer, root)), Is.Empty, "reachable records hash to their hash and parse");
            Assert.That(Rlps(buffer, root).OrderBy(node => node.Key), Is.EqualTo(expectedBufferNodes.OrderBy(node => node.Key)), "reachable nodes");
            if (!fromSource) Assert.That(fetchCount, Is.Zero, "nodes the first bulk set wrote are read from the buffer");
            Assert.That(ReadValues(buffer, source, root, keys), Is.EqualTo(keys.Select(key => expectedValues.GetValueOrDefault(key))), "values");
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
        using NodeRecordBuffer buffer = new(SmallGenerationBytes);
        using NodeRecordBufferPin retain = buffer.PinHead();
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), NewWarmedNodeBuffer(1), null);
        Dictionary<Hash256, byte[]> values = [];
        (ValueHash256 Hash, ulong Pointer) root = (ValueKeccak.EmptyTreeHash, 0);
        foreach (List<(Hash256 key, byte[] value)> batch in batches)
        {
            root = BulkSet(buffer, source, null, root, batch, canParallelize);
            foreach ((Hash256 key, byte[] value) in batch)
            {
                if (value is null) values.Remove(key);
                else values[key] = value;
            }
            (Hash256 expectedRoot, Dictionary<TreePath, byte[]> expectedNodes) = BuildExpectedTrie(values);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(root.Hash, Is.EqualTo(expectedRoot.ValueHash256));
                Assert.That(Rlps(buffer, root).OrderBy(node => node.Key), Is.EqualTo(expectedNodes.OrderBy(node => node.Key)));
                Assert.That(InvalidRecords(buffer, root), Is.Empty);
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
        using NodeRecordBuffer buffer = new(SmallGenerationBytes);
        using NodeRecordBufferPin retain = buffer.PinHead();
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), NewWarmedNodeBuffer(1), null);
        (ValueHash256 Hash, ulong Pointer) root = BulkSet(buffer, source, null, (ValueKeccak.EmptyTreeHash, 0), existingKeys.Select(key => (new Hash256(key), ExistingValue)).ToList(), canParallelize: false);

        ulong before = Frontier(buffer);
        (ValueHash256 Hash, ulong Pointer) after = BulkSet(buffer, source, null, root, keys.Select(key => (new Hash256(key), existingKeys.Contains(key) ? ExistingValue : null)).ToList(), canParallelize: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(after.Hash, Is.EqualTo(root.Hash), "root hash");
            Assert.That(after.Pointer, Is.EqualTo(root.Pointer), "root pointer");
            Assert.That(Frontier(buffer), Is.EqualTo(before), "no buffer bytes reserved");
        }
    }

    [Test]
    public void No_op_batches_clear_temporary_root_pointers_without_writing([Values] bool emptyBatch)
    {
        Hash256 key = TestItem.KeccakA;
        using NodeRecordBuffer original = new(SmallGenerationBytes);
        using NodeRecordBufferPin originalRetain = original.PinHead();
        using NodeRecordBuffer buffer = new(SmallGenerationBytes);
        using NodeRecordBufferPin retain = buffer.PinHead();
        WarmedNodeBuffer warmed = NewWarmedNodeBuffer(1);
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), warmed, null);
        (ValueHash256 Hash, ulong Pointer) root = BulkSet(original, source, null, (ValueKeccak.EmptyTreeHash, 0), [(key, ExistingValue)], false);
        Assert.That(original.TryLocate(root.Pointer, out NodeRecord rootRecord), Is.True);
        byte[] rlp = rootRecord.Rlp.ToArray();
        // Indexed off the root's position, so only the supplied pointer finds it.
        ulong pointer = FlatTestHelpers.Warm(warmed, null, TreePath.FromHexString("1"), rlp);
        (ValueHash256 Hash, ulong Pointer) temporaryRoot = (root.Hash, pointer);

        ulong before = Frontier(buffer);
        (ValueHash256 Hash, ulong Pointer) after = BulkSet(buffer, source, null, temporaryRoot, emptyBatch ? [] : [(key, ExistingValue)], false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(after.Hash, Is.EqualTo(root.Hash));
            Assert.That(after.Pointer, Is.Zero);
            Assert.That(Frontier(buffer), Is.EqualTo(before), "no buffer bytes reserved");
            Assert.That(source.FetchCount, Is.Zero, "root loading resolves the supplied temporary pointer");
        }
    }

    [Test]
    public void WarmUp_fetches_only_nodes_outside_the_buffer_and_bulk_set_then_reads_them_without_fetching()
    {
        Hash256 address = TestItem.KeccakA;
        List<(Hash256 key, byte[] value)> items = PatriciaTreeBulkSetterTests.GenRandomOfLength(1000, seed: 4);
        using NodeRecordBuffer buffer = new(SmallGenerationBytes);
        using NodeRecordBufferPin retain = buffer.PinHead();
        (ValueHash256 Hash, ulong Pointer) root = BulkSet(buffer, new DictionaryNodeSource(new Dictionary<TreePath, byte[]>(), NewWarmedNodeBuffer(1), address), address, (ValueKeccak.EmptyTreeHash, 0), items, canParallelize: false);

        // A buffer without the trie stands for one written before the trie was persisted.
        using NodeRecordBuffer persistedBuffer = new(SmallGenerationBytes);
        (ValueHash256 Hash, ulong Pointer) persistedRoot = (root.Hash, 0);
        Dictionary<TreePath, byte[]> persisted = Rlps(buffer, root);
        WarmedNodeBuffer warmed = NewWarmedNodeBuffer(persisted.Count);
        DictionaryNodeSource source = new(persisted, warmed, address);
        int fromBuffer = WarmUp(buffer, root, items[0].key);
        int fromSource = WarmUp(persistedBuffer, persistedRoot, items[0].key);
        int again = WarmUp(persistedBuffer, persistedRoot, items[0].key);
        foreach ((Hash256 key, _) in items) WarmUp(persistedBuffer, persistedRoot, key);
        int fetchesBeforeBulkSet = source.FetchCount;
        (ValueHash256 Hash, ulong Pointer) newRoot = BulkSet(persistedBuffer, source, address, persistedRoot, [(items[0].key, NewValue)], canParallelize: false);

        PatriciaTree tree = new(new RawScopedTrieStore(new MemDb()), LimboLogs.Instance);
        foreach ((Hash256 key, byte[] value) in items) tree.Set(key.Bytes, value);
        tree.Set(items[0].key.Bytes, NewValue);
        tree.UpdateRootHash();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fromBuffer, Is.Zero, "warm-up through the buffer");
            Assert.That(fromSource, Is.GreaterThan(1), "warm-up through the source");
            Assert.That(again, Is.Zero, "warm-up of warmed nodes");
            Assert.That(source.FetchCount, Is.EqualTo(fetchesBeforeBulkSet), "bulk set over warmed nodes");
            Assert.That(newRoot.Hash, Is.EqualTo(tree.RootHash.ValueHash256), "root");
        }

        int WarmUp(INodeRecordBuffer walkedBuffer, (ValueHash256 Hash, ulong Pointer) walkedRoot, Hash256 key)
        {
            int before = source.FetchCount;
            source.WarmUp(walkedBuffer, walkedRoot, key.ValueHash256, forDelete: false);
            return source.FetchCount - before;
        }
    }

    [Test]
    public void Warm_up_publishes_a_linked_prefix_only_after_descent([Values] bool missingLeaf)
    {
        Hash256 keyA = new("1000000000000000000000000000000000000000000000000000000000000001");
        Hash256 keyB = new("1000000000000000000000000000000000000000000000000000000000000002");
        Hash256 keyC = new("2000000000000000000000000000000000000000000000000000000000000000");
        using NodeRecordBuffer original = new(SmallGenerationBytes);
        using NodeRecordBufferPin retain = original.PinHead();
        (ValueHash256 Hash, ulong Pointer) root = BulkSet(original, new DictionaryNodeSource(new Dictionary<TreePath, byte[]>(), NewWarmedNodeBuffer(1), null),
            null, (ValueKeccak.EmptyTreeHash, 0), [(keyA, ExistingValue), (keyB, ExistingValue), (keyC, ExistingValue)], false);
        Dictionary<TreePath, byte[]> nodes = Rlps(original, root);
        TreePath leafPath = TreePath.FromPath(keyA.Bytes);
        if (missingLeaf) nodes.Remove(leafPath);
        WarmedNodeBuffer warmed = NewWarmedNodeBuffer(16);
        DictionaryNodeSource source = new(nodes, warmed, null);
        using NodeRecordBuffer buffer = new(SmallGenerationBytes);

        source.WarmUp(buffer, (root.Hash, 0), keyA.ValueHash256, forDelete: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.FetchCount, Is.EqualTo(4));
            Assert.That(warmed.Count, Is.EqualTo(missingLeaf ? 3 : 4));
        }
        TreePath currentPath = TreePath.Empty;
        ValueHash256 currentHash = root.Hash;
        int linked = 0;
        Span<byte> extensionKey = stackalloc byte[64];
        while (warmed.TryGet(null, currentPath, out NodeRecord record, out ulong pointer))
        {
            linked++;
            Assert.That(record.Hash, Is.EqualTo(currentHash));
            Assert.That(ValueKeccak.Compute(record.Rlp), Is.EqualTo(record.Hash));
            Assert.That(NodeRecordCodec.IsTransient(pointer), Is.True);
            if (record.Kind == NodeRecordKind.Leaf) break;
            int slot = record.Kind == NodeRecordKind.Extension ? 0 : leafPath[currentPath.Length];
            ChildRef child = record.View.Child(slot);
            if (currentPath.Length == 0) Assert.That(record.View.Child(2).Pointer, Is.Zero, "off-path subtrees remain unknown");
            if (child.Pointer == 0)
            {
                Assert.That(missingLeaf, Is.True, "a complete warmed path must link every hashed child");
                break;
            }
            Assert.That(warmed.TryLocate(child.Pointer, out NodeRecord linkedChild) && linkedChild.Hash == child.Data, Is.True);
            Assert.That(child.Pointer, Is.EqualTo(pointer + (ulong)NodeRecordCodec.AlignedLength(record.Span.Length)), "the path is written in one batch, in descent order");
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
    public void Warm_up_keeps_borrowed_ancestors_alive_at_every_key_depth()
    {
        List<(Hash256 key, byte[] value)> items = [(new Hash256(new byte[32]), ExistingValue)];
        for (int depth = 0; depth < 64; depth++)
        {
            byte[] key = new byte[32];
            key[depth / 2] = depth % 2 == 0 ? (byte)0x10 : (byte)0x01;
            items.Add((new Hash256(key), ExistingValue));
        }
        using NodeRecordBuffer original = new(SmallGenerationBytes);
        using NodeRecordBufferPin retain = original.PinHead();
        (ValueHash256 Hash, ulong Pointer) root = BulkSet(original, new DictionaryNodeSource(new Dictionary<TreePath, byte[]>(), NewWarmedNodeBuffer(1), null),
            null, (ValueKeccak.EmptyTreeHash, 0), items, false);
        Dictionary<TreePath, byte[]> nodes = Rlps(original, root);
        WarmedNodeBuffer warmed = NewWarmedNodeBuffer(1024);
        using NodeRecordBuffer buffer = new(SmallGenerationBytes);

        new DictionaryNodeSource(nodes, warmed, null).WarmUp(buffer, (root.Hash, 0), ValueKeccak.Zero, forDelete: false);

        TreePath path = TreePath.Empty;
        Assert.That(warmed.TryGet(null, path, out NodeRecord record, out _), Is.True);
        for (int depth = 0; depth <= 64; depth++)
        {
            Assert.That(record.Rlp.ToArray(), Is.EqualTo(nodes[path]));
            if (depth == 64) break;
            ChildRef child = record.View.Child(0);
            Assert.That(warmed.TryLocate(child.Pointer, out record) && record.Hash == child.Data, Is.True);
            path.AppendMut(0);
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
        using NodeRecordBuffer original = new(SmallGenerationBytes);
        using NodeRecordBufferPin retain = original.PinHead();
        (ValueHash256 Hash, ulong Pointer) root = BulkSet(original, new DictionaryNodeSource(new Dictionary<TreePath, byte[]>(), NewWarmedNodeBuffer(1), null), null, (ValueKeccak.EmptyTreeHash, 0), items, false);
        Dictionary<TreePath, byte[]> persisted = Rlps(original, root);
        WarmedNodeBuffer warmed = NewWarmedNodeBuffer(1024);
        DictionaryNodeSource source = new(persisted, warmed, null);
        using NodeRecordBuffer buffer = new(SmallGenerationBytes);
        using NodeRecordBufferPin newRetain = buffer.PinHead();
        (ValueHash256 Hash, ulong Pointer) persistedRoot = (root.Hash, 0);

        source.WarmUp(buffer, persistedRoot, keyA.ValueHash256, forDelete);
        TreePath branchPath = TreePath.FromPath(keyA.Bytes);
        branchPath.TruncateOne();
        Assert.That(warmed.TryGet(null, branchPath, out NodeRecord warmedBranch, out _), Is.True);
        ChildRef warmedSibling = warmedBranch.View.Child(2);
        Assert.That(warmedSibling.Pointer, forDelete && !threeChildren ? Is.Not.Zero : Is.Zero, "only a required collapse sibling is linked");
        if (warmedSibling.Pointer != 0) Assert.That(warmed.TryLocate(warmedSibling.Pointer, out NodeRecord sibling) && sibling.Hash == warmedSibling.Data, Is.True);
        int before = source.FetchCount;
        (ValueHash256 Hash, ulong Pointer) updated = BulkSet(buffer, source, null, persistedRoot, [(keyA, null)], false);
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
        using NodeRecordBuffer original = new(SmallGenerationBytes);
        using NodeRecordBufferPin retain = original.PinHead();
        (ValueHash256 Hash, ulong Pointer) root = BulkSet(original, new DictionaryNodeSource(new Dictionary<TreePath, byte[]>(), NewWarmedNodeBuffer(1), null), null, (ValueKeccak.EmptyTreeHash, 0),
            [(keyA, ExistingValue), (keyB, ExistingValue)], false);
        Dictionary<TreePath, byte[]> nodes = Rlps(original, root);
        DictionaryNodeSource pathSource = new(nodes, NewWarmedNodeBuffer(1024), null);
        DictionaryNodeSource deleteSource = new(nodes, NewWarmedNodeBuffer(1024), null);
        using NodeRecordBuffer buffer = new(SmallGenerationBytes);
        (ValueHash256 Hash, ulong Pointer) persistedRoot = (root.Hash, 0);

        pathSource.WarmUp(buffer, persistedRoot, missing.ValueHash256, forDelete: false);
        deleteSource.WarmUp(buffer, persistedRoot, missing.ValueHash256, forDelete: true);

        Assert.That(deleteSource.FetchCount, Is.EqualTo(pathSource.FetchCount));
    }

    [Test]
    public void Traversal_finds_exact_node_paths_and_enumerates_in_depth_first_order([Values] bool inlineLeaves)
    {
        using NodeRecordBuffer buffer = new(SmallGenerationBytes);
        using NodeRecordBufferPin retain = buffer.PinHead();
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), NewWarmedNodeBuffer(1), null);
        byte[] value = inlineLeaves ? [1] : ExistingValue;
        (ValueHash256 Hash, ulong Pointer) root = BulkSet(buffer, source, null, (ValueKeccak.EmptyTreeHash, 0),
        [
            (new Hash256("1000000000000000000000000000000000000000000000000000000000000001"), value),
            (new Hash256("1000000000000000000000000000000000000000000000000000000000000002"), ExistingValue),
            (new Hash256("2000000000000000000000000000000000000000000000000000000000000000"), ExistingValue),
        ], false);
        string branchPath = "1" + new string('0', 62);
        string[] expectedPaths = inlineLeaves
            ? ["", "1", branchPath, branchPath + "2", "2"]
            : ["", "1", branchPath, branchPath + "1", branchPath + "2", "2"];
        (TreePath Path, ulong Pointer)[] actual = buffer.EnumerateNodes(root.Pointer, 0).Select(node => (node.Path, node.Pointer)).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.Select(node => node.Path), Is.EqualTo(expectedPaths.Select(TreePath.FromHexString)));
            foreach ((TreePath path, ulong pointer) in actual)
            {
                Assert.That(TryGetNode(buffer, root.Pointer, path, out TrieNode node), Is.True, path.ToString());
                Assert.That(node?.FullRlp.ToArray(), Is.EqualTo(buffer.TryLocate(pointer, out NodeRecord record) ? record.Rlp.ToArray() : null), path.ToString());
            }
            foreach (string path in new[] { "3", "10", "11", "20" })
            {
                Assert.That(TryGetNode(buffer, root.Pointer, TreePath.FromHexString(path), out _), Is.False, path);
            }
            TreePath leafPath = TreePath.FromHexString("1000000000000000000000000000000000000000000000000000000000000001");
            Assert.That(TryGetNode(buffer, root.Pointer, leafPath, out _), Is.EqualTo(!inlineLeaves), "inline leaves have no separate buffer record");
            Assert.That(source.FetchCount, Is.Zero);
        }
    }

    [Test]
    public void Traversal_preserves_sparse_child_order_with_inline_and_unknown_children()
    {
        using NodeRecordBuffer buffer = new(SmallGenerationBytes);
        using NodeRecordBufferPin retain = buffer.PinHead();
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), NewWarmedNodeBuffer(1), null);
        string prefix = new('0', 63);
        (ValueHash256 Hash, ulong Pointer) root = BulkSet(buffer, source, null, (ValueKeccak.EmptyTreeHash, 0),
        [
            (new Hash256(prefix + "1"), new byte[] { 1 }),
            (new Hash256(prefix + "5"), ExistingValue),
            (new Hash256(prefix + "9"), ExistingValue),
            (new Hash256(prefix + "f"), ExistingValue),
        ], false);
        Assert.That(buffer.TryLocate(root.Pointer, out NodeRecord extension), Is.True);
        ulong branchPointer = extension.View.Child(0).Pointer;
        Assert.That(buffer.TryLocate(branchPointer, out NodeRecord branch), Is.True);
        ulong firstChild = branch.View.Child(5).Pointer;
        ulong lastChild = branch.View.Child(15).Pointer;
        ulong unlinkedBranch = CopyWithPointer(branch, 1, 0);
        ulong unlinkedRoot = CopyWithPointer(extension, 0, unlinkedBranch);

        (TreePath Path, ulong Pointer)[] actual = buffer.EnumerateNodes(unlinkedRoot, 0).Select(node => (node.Path, node.Pointer)).ToArray();
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

        ulong CopyWithPointer(NodeRecord original, int ordinal, ulong pointer)
        {
            using INodeRecordWriteBatch batch = ((INodeRecordBuffer)buffer).StartWriteBatch(NodeRecordCodec.AlignedLength(original.Span.Length));
            original.Span.CopyTo(batch.Bytes);
            BinaryPrimitives.WriteUInt64LittleEndian(batch.Bytes[(NodeRecordCodec.HeaderLength + ordinal * NodeRecordCodec.PointerLength)..], pointer);
            return batch.BasePointer;
        }
    }

    [Test]
    public void Traversal_prunes_inherited_subtrees_at_an_inclusive_same_generation_boundary([Values] bool canParallelize)
    {
        using NodeRecordBuffer buffer = new(SmallGenerationBytes);
        using NodeRecordBufferPin originalRetain = buffer.PinHead();
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), NewWarmedNodeBuffer(1), null);
        Hash256 inheritedKey = new("1000000000000000000000000000000000000000000000000000000000000000");
        Hash256 updatedKey = new("f000000000000000000000000000000000000000000000000000000000000000");
        (ValueHash256 Hash, ulong Pointer) original = BulkSet(buffer, source, null, (ValueKeccak.EmptyTreeHash, 0), [(inheritedKey, ExistingValue), (updatedKey, ExistingValue)], canParallelize);
        ulong inherited = NodePointer(buffer, original.Pointer, TreePath.Empty.Append(1));
        using NodeRecordBufferPin updateRetain = buffer.PinHead();
        (ValueHash256 Hash, ulong Pointer) updated = BulkSet(buffer, source, null, original, [(updatedKey, NewValue)], canParallelize);
        (TreePath Path, ulong Pointer)[] delta = buffer.EnumerateNodes(updated.Pointer, updateRetain.WriteBoundary).Select(node => (node.Path, node.Pointer)).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated.Pointer >> 32, Is.EqualTo(original.Pointer >> 32));
            Assert.That(delta.Select(node => node.Path), Is.EqualTo(new[] { TreePath.Empty, TreePath.Empty.Append(15) }));
            Assert.That(delta.Min(node => node.Pointer), Is.EqualTo(updateRetain.WriteBoundary), "the first reservation belongs to this range");
            Assert.That(buffer.EnumerateNodes(original.Pointer, updateRetain.WriteBoundary), Is.Empty);
            ulong found = NodePointer(buffer, updated.Pointer, TreePath.Empty.Append(1));
            Assert.That(found, Is.EqualTo(inherited), "point reads still follow inherited records");
            Assert.That(buffer.TryLocate(found, out NodeRecord foundRecord) && !foundRecord.Rlp.IsEmpty, Is.True);
            Assert.That(source.FetchCount, Is.Zero);
        }
    }

    [Test]
    public void Traversal_stops_at_unknown_child_pointers_without_loading_the_source()
    {
        using NodeRecordBuffer buffer = new(SmallGenerationBytes);
        using NodeRecordBufferPin retain = buffer.PinHead();
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), NewWarmedNodeBuffer(1), null);
        (ValueHash256 Hash, ulong Pointer) root = BulkSet(buffer, source, null, (ValueKeccak.EmptyTreeHash, 0),
        [
            (new Hash256("1000000000000000000000000000000000000000000000000000000000000000"), ExistingValue),
            (new Hash256("2000000000000000000000000000000000000000000000000000000000000000"), ExistingValue),
        ], false);
        Assert.That(buffer.TryLocate(root.Pointer, out NodeRecord rootRecord), Is.True);
        ulong unlinked = buffer.AppendRecordFromRlp(null, TreePath.Empty, rootRecord.Hash, rootRecord.Rlp, []);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.EnumerateNodes(unlinked, 0).Select(node => (node.Path, node.Pointer)).ToArray(), Is.EqualTo(new[] { (TreePath.Empty, unlinked) }));
            Assert.That(TryGetNode(buffer, unlinked, TreePath.Empty, out _), Is.True);
            Assert.That(TryGetNode(buffer, unlinked, TreePath.FromHexString("1"), out _), Is.False);
            Assert.That(TryGetNode(buffer, unlinked, TreePath.FromHexString("2"), out _), Is.False);
            Assert.That(buffer.EnumerateNodes(0, 0), Is.Empty);
            Assert.That(TryGetNode(buffer, 0, TreePath.Empty, out _), Is.False);
            Assert.That(source.FetchCount, Is.Zero);
        }
    }

    [Test]
    public void Traversal_skips_expired_inherited_children_but_keeps_the_new_root()
    {
        using NodeRecordBuffer buffer = new(SmallGenerationBytes);
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), NewWarmedNodeBuffer(1), null);
        Hash256 first = new("1000000000000000000000000000000000000000000000000000000000000000");
        Hash256 second = new("2000000000000000000000000000000000000000000000000000000000000000");
        (ValueHash256 Hash, ulong Pointer) root;
        NodeRecordBufferPin rootRetain = buffer.PinHead();
        root = BulkSet(buffer, source, null, (ValueKeccak.EmptyTreeHash, 0), [(first, ExistingValue), (second, ExistingValue)], false);
        FlatTestHelpers.StartNewGeneration(buffer);
        using NodeRecordBufferPin retain = buffer.PinHead();
        (ValueHash256 Hash, ulong Pointer) updated = BulkSet(buffer, source, null, root, [(first, NewValue)], false);
        rootRetain.Dispose();
        while (NodeRecordBufferTests.GenerationOf(Frontier(buffer)) < NodeRecordBufferTests.LiveGenerations + 1) FlatTestHelpers.StartNewGeneration(buffer);
        Assert.That(NodeRecordBufferTests.IsLocatable(buffer, root.Pointer), Is.False);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(TryGetNode(buffer, updated.Pointer, TreePath.Empty, out _), Is.True);
            Assert.That(TryGetNode(buffer, updated.Pointer, TreePath.FromHexString("1"), out _), Is.True);
            Assert.That(TryGetNode(buffer, updated.Pointer, TreePath.FromHexString("2"), out _), Is.False);
            Assert.That(buffer.EnumerateNodes(updated.Pointer, 0).Select(node => node.Path), Is.EqualTo(new[] { TreePath.Empty, TreePath.FromHexString("1") }));
            Assert.That(buffer.EnumerateNodes(root.Pointer, 0), Is.Empty);
            Assert.That(TryGetNode(buffer, root.Pointer, TreePath.Empty, out _), Is.False);
            Assert.That(source.FetchCount, Is.Zero);
        }
    }

    [Test]
    public void Traversal_handles_branches_at_every_key_depth()
    {
        using NodeRecordBuffer buffer = new(SmallGenerationBytes);
        using NodeRecordBufferPin retain = buffer.PinHead();
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), NewWarmedNodeBuffer(1), null);
        List<(Hash256 key, byte[] value)> items = [(Keccak.Zero, ExistingValue)];
        for (int depth = 0; depth < 64; depth++)
        {
            byte[] key = new byte[32];
            key[depth / 2] = (byte)((depth & 1) == 0 ? 0x10 : 0x01);
            items.Add((new Hash256(key), ExistingValue));
        }
        (ValueHash256 Hash, ulong Pointer) root = BulkSet(buffer, source, null, (ValueKeccak.EmptyTreeHash, 0), items, false);
        byte[] expectedLeafRlp = new byte[NodeRlp.LeafLength([], ExistingValue)];
        NodeRlp.WriteLeaf([], ExistingValue, expectedLeafRlp);
        using (Assert.EnterMultipleScope())
        {
            IEnumerable<TreePath> expected = Enumerable.Range(0, 64).Select(depth => TreePath.FromHexString(new string('0', depth)))
                .Concat(Enumerable.Range(0, 64).Select(depth => TreePath.FromHexString(new string('0', depth) + "1")))
                .Append(TreePath.FromHexString(new string('0', 64)));
            Assert.That(buffer.EnumerateNodes(root.Pointer, 0).Select(node => node.Path), Is.EquivalentTo(expected));
            Assert.That(TryGetNode(buffer, root.Pointer, TreePath.FromHexString(new string('0', 64)), out TrieNode leaf), Is.True);
            Assert.That(leaf?.FullRlp.ToArray(), Is.EqualTo(expectedLeafRlp));
        }
    }

    [Test]
    public void Traversal_node_lookup_returns_owned_rlp()
    {
        using NodeRecordBuffer buffer = new(SmallGenerationBytes);
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), NewWarmedNodeBuffer(1), null);
        (ValueHash256 Hash, ulong Pointer) root;
        using (buffer.PinHead()) root = BulkSet(buffer, source, null, (ValueKeccak.EmptyTreeHash, 0), [(TestItem.KeccakA, ExistingValue)], false);
        Assert.That(TryGetNode(buffer, root.Pointer, TreePath.Empty, out TrieNode node), Is.True);
        byte[] expected = node.FullRlp.ToArray();
        buffer.Dispose();
        Assert.That(node.FullRlp.ToArray(), Is.EqualTo(expected));
    }

    [Test]
    public void Traversal_records_can_be_buffered_under_a_pin()
    {
        using NodeRecordBuffer buffer = new(SmallGenerationBytes);
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), NewWarmedNodeBuffer(1), null);
        (ValueHash256 Hash, ulong Pointer) root;
        using (buffer.PinHead()) root = BulkSet(buffer, source, null, (ValueKeccak.EmptyTreeHash, 0), [(TestItem.KeccakA, ExistingValue)], false);
        NodeRecord borrowed;
        byte[] expected;
        using (buffer.PinAll())
        {
            NodeRecord[] buffered = buffer.EnumerateNodes(root.Pointer, 0).Select(node => node.Record).ToArray();
            borrowed = buffered[0];
            expected = borrowed.Rlp.ToArray();
            buffer.Dispose();
            Assert.That(borrowed.Rlp.ToArray(), Is.EqualTo(expected), "the pin covers records after enumerator disposal");
        }
        Assert.That(() => borrowed.Rlp.ToArray(), Throws.InstanceOf<ObjectDisposedException>());
    }

    /// <summary>The buffered node at exactly <paramref name="path"/> below <paramref name="rootPointer"/>, reached by record pointers.</summary>
    private static bool TryGetNode(INodeRecordBuffer buffer, ulong rootPointer, TreePath path, [NotNullWhen(true)] out TrieNode node)
    {
        (TreePath Path, ulong Pointer, NodeRecord Record) match = buffer.EnumerateNodes(rootPointer, 0).FirstOrDefault(found => found.Path == path);
        node = match.Pointer == 0 ? null : match.Record.ToTrieNode();
        return node is not null;
    }

    /// <summary>The buffer's allocation frontier, which every reservation advances.</summary>
    private static ulong Frontier(INodeRecordBuffer buffer)
    {
        using INodeRecordBufferPin pin = buffer.PinHead();
        return pin.WriteBoundary;
    }

    private static (ValueHash256 Hash, ulong Pointer) BulkSet(INodeRecordBuffer buffer, DictionaryNodeSource source, Hash256 address, (ValueHash256 Hash, ulong Pointer) root, List<(Hash256 key, byte[] value)> items, bool canParallelize)
    {
        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = Entries(items);
        return NodeRecordBulkSet.BulkSet(source.Over(buffer), address, root, entries, canParallelize);
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

    private static Dictionary<TreePath, byte[]> Rlps(INodeRecordBuffer buffer, (ValueHash256 Hash, ulong Pointer) root) =>
        buffer.EnumerateNodes(root.Pointer, 0).ToDictionary(node => node.Path, node => node.Record.Rlp.ToArray());

    private static ulong NodePointer(INodeRecordBuffer buffer, ulong rootPointer, TreePath path) =>
        buffer.EnumerateNodes(rootPointer, 0).First(node => node.Path == path).Pointer;

    /// <summary>The values at <paramref name="keys"/>, read by a <see cref="PatriciaTree"/> from the records linked from <paramref name="root"/>, falling back to <paramref name="source"/>.</summary>
    private static byte[][] ReadValues(INodeRecordBuffer buffer, DictionaryNodeSource source, (ValueHash256 Hash, ulong Pointer) root, IEnumerable<Hash256> keys)
    {
        PatriciaTree tree = new(new LinkedRecordResolver(Rlps(buffer, root), source), LimboLogs.Instance) { RootHash = new(root.Hash) };
        return keys.Select(key => tree.Get(key.Bytes) is { IsEmpty: false } value ? value.ToArray() : null).ToArray();
    }

    private sealed class LinkedRecordResolver(Dictionary<TreePath, byte[]> linked, DictionaryNodeSource source) : IScopedTrieStore
    {
        public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => new(NodeType.Unknown, hash);
        public byte[] TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
        {
            TreePath nodePath = path;
            return linked.TryGetValue(path, out byte[] rlp) && ValueKeccak.Compute(rlp) == hash.ValueHash256 ? rlp
                : FlatTestHelpers.LoadRlp((action, nodes) => source.TryLoad(nodePath, hash.ValueHash256, action, nodes));
        }
        public byte[] LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            TryLoadRlp(path, hash, flags) ?? throw new MissingTrieNodeException($"Trie node missing hash={hash} path={path}", null, path, hash);
        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256 address) => this;
        public INodeStorage.KeyScheme Scheme => INodeStorage.KeyScheme.HalfPath;
        public ICommitter BeginCommit(TrieNode root, WriteFlags flags = WriteFlags.None) => throw new NotSupportedException();
    }

    private static IEnumerable<string> InvalidRecords(INodeRecordBuffer buffer, (ValueHash256 Hash, ulong Pointer) root)
    {
        List<string> invalid = [];
        foreach ((TreePath path, ulong pointer, NodeRecord record) in buffer.EnumerateNodes(root.Pointer, 0))
        {
            byte[] rlp = record.Rlp.ToArray();
            bool parses = NodeRecordCodec.TryDecode(rlp, out NodeRecordKind kind, out ushort hashMask);
            bool transientPointer = NodeRecordCodec.IsTransient(pointer);
            for (int i = 0; i < BitOperations.PopCount(hashMask); i++)
            {
                transientPointer |= NodeRecordCodec.IsTransient(BinaryPrimitives.ReadUInt64LittleEndian(record.Span[(NodeRecordCodec.HeaderLength + i * NodeRecordCodec.PointerLength)..]));
            }

            if (ValueKeccak.Compute(rlp) != record.Hash || !parses || kind != record.Kind || hashMask != record.View.HashMask
                || BinaryPrimitives.ReadUInt64LittleEndian(record.Span[NodeRecordCodec.NodeKeyHashOffset..]) != NodeRecordCodec.NodeKeyHash(null, path) || transientPointer)
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

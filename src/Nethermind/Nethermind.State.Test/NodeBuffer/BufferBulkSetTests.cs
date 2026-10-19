// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
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
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Store.Test.NodeBuffer;

internal class BufferBulkSetTests
{
    private const int SmallGenerationBytes = 64 * 1024;

    private static readonly byte[] ExistingValue = Bytes.FromHexString("0x7777777777777777777777777777777777777777777777777777777777777777");
    private static readonly byte[] NewValue = Bytes.FromHexString("0x010203");
    private static readonly byte[] FillerValue = Enumerable.Repeat((byte)0x77, 1000).ToArray();

    private readonly List<WarmedNodeLog> _warmedNodeLogs = [];

    [TearDown]
    public void DisposeWarmedNodeLogs()
    {
        foreach (WarmedNodeLog log in _warmedNodeLogs) log.Dispose();
        _warmedNodeLogs.Clear();
    }

    private WarmedNodeLog NewWarmedNodeLog(int capacity = 1)
    {
        WarmedNodeLog log = new(capacity);
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
        using NodeRecordBuffer existingBuffer = new(0, generationBytes);
        using NodeRecordBufferPin existingRetain = existingBuffer.Pin();
        WarmedNodeLog warmed = NewWarmedNodeLog(existingNodes.Count);
        DictionaryNodeSource source = new(existingNodes, warmed, null);
        TrieRoot existingRoot = BulkSet(existingBuffer, source, null, TrieRoot.Empty, existingItems, canParallelize);
        // From the source, the existing trie stands for one persisted before this buffer was created.
        using NodeRecordBuffer buffer = fromSource ? new(0, generationBytes) : existingBuffer;
        using NodeRecordBufferPin retain = buffer.Pin();
        ulong temporaryRootPointer = fromSource ? InsertLinkedRecords(warmed, existingNodes) : 0;
        TrieRoot root = BulkSet(new WarmedNodeRecordBuffer(buffer, warmed), source, null, fromSource ? new TrieRoot(expectedExistingRoot.ValueHash256, temporaryRootPointer) : existingRoot, items, canParallelize);
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
            Assert.That(Rlps(buffer, root), Is.EquivalentTo(expectedBufferNodes), "reachable nodes");
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
        using NodeRecordBuffer buffer = new(0, SmallGenerationBytes);
        using NodeRecordBufferPin retain = buffer.Pin();
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), NewWarmedNodeLog(), null);
        Dictionary<Hash256, byte[]> values = [];
        TrieRoot root = TrieRoot.Empty;
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
                Assert.That(Rlps(buffer, root), Is.EquivalentTo(expectedNodes));
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
        using NodeRecordBuffer buffer = new(0, SmallGenerationBytes);
        using NodeRecordBufferPin retain = buffer.Pin();
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), NewWarmedNodeLog(), null);
        TrieRoot root = BulkSet(buffer, source, null, TrieRoot.Empty, existingKeys.Select(key => (new Hash256(key), ExistingValue)).ToList(), canParallelize: false);

        ulong before = buffer.Reserve(0, out _, out _);
        TrieRoot after = BulkSet(buffer, source, null, root, keys.Select(key => (new Hash256(key), existingKeys.Contains(key) ? ExistingValue : null)).ToList(), canParallelize: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(after.Hash, Is.EqualTo(root.Hash), "root hash");
            Assert.That(after.Pointer, Is.EqualTo(root.Pointer), "root pointer");
            Assert.That(buffer.Reserve(0, out _, out _), Is.EqualTo(before), "no buffer bytes reserved");
        }
    }

    [Test]
    public void No_op_batches_clear_temporary_root_pointers_without_writing([Values] bool emptyBatch)
    {
        Hash256 key = TestItem.KeccakA;
        using NodeRecordBuffer original = new(0, SmallGenerationBytes);
        using NodeRecordBufferPin originalRetain = original.Pin();
        using NodeRecordBuffer buffer = new(0, SmallGenerationBytes);
        using NodeRecordBufferPin retain = buffer.Pin();
        WarmedNodeLog warmed = NewWarmedNodeLog(1);
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), warmed, null);
        TrieRoot root = BulkSet(original, source, null, TrieRoot.Empty, [(key, ExistingValue)], false);
        byte[] rlp = original.GetRecord(root.Pointer).Rlp.ToArray();
        warmed.TryAppend(null, TreePath.Empty, root.Hash, rlp, out _, out ulong pointer);
        byte[] otherRlp = NodeRecordBufferTests.Leaf(1, 8);
        warmed.TryAppend(null, TreePath.Empty, ValueKeccak.Compute(otherRlp), otherRlp, out _, out _);
        TrieRoot temporaryRoot = new(root.Hash, pointer);

        ulong before = buffer.Reserve(0, out _, out _);
        TrieRoot after = BulkSet(new WarmedNodeRecordBuffer(buffer, warmed), source, null, temporaryRoot, emptyBatch ? [] : [(key, ExistingValue)], false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(after.Hash, Is.EqualTo(root.Hash));
            Assert.That(after.Pointer, Is.Zero);
            Assert.That(buffer.Reserve(0, out _, out _), Is.EqualTo(before), "no buffer bytes reserved");
            Assert.That(source.FetchCount, Is.Zero, "root loading resolves the supplied temporary pointer");
        }
    }

    [Test]
    public void Commit_clears_only_temporary_children_and_remains_readable_after_reset([Values] bool compact)
    {
        Hash256 keyA = new("1000000000000000000000000000000000000000000000000000000000000000");
        Hash256 keyB = new("2000000000000000000000000000000000000000000000000000000000000000");
        Hash256 keyC = new("3000000000000000000000000000000000000000000000000000000000000000");
        List<(Hash256 key, byte[] value)> items = [(keyA, ExistingValue), (keyB, ExistingValue), (keyC, ExistingValue)];
        using NodeRecordBuffer buffer = new(1UL << 20, SmallGenerationBytes);
        using NodeRecordBufferPin retain = buffer.Pin();
        WarmedNodeLog warmed = NewWarmedNodeLog(16);
        DictionaryNodeSource empty = new(new Dictionary<TreePath, byte[]>(), warmed, null);
        TrieRoot root = BulkSet(buffer, empty, null, TrieRoot.Empty, items, false);
        Dictionary<TreePath, byte[]> nodes = Rlps(buffer, root);
        ulong realChildPointer = buffer.GetRecord(root.Pointer).View.Child(1).Pointer;
        ValueHash256 unchangedHash = buffer.GetRecord(root.Pointer).View.Child(2).Data;
        byte[] rootRlp = nodes[TreePath.Empty];
        NodeRecordCodec.TryMeasure(rootRlp, out NodeRecordKind kind, out ushort mask);
        Span<ulong> childPointers = stackalloc ulong[16];
        for (int nibble = 1; nibble <= 3; nibble++)
        {
            TreePath path = TreePath.Empty.Append(nibble);
            byte[] rlp = nodes[path];
            warmed.TryAppend(null, path, ValueKeccak.Compute(rlp), rlp, out _, out ulong pointer);
            childPointers[nibble] = nibble == 1 ? realChildPointer : pointer;
        }
        warmed.AppendVerified(null, TreePath.Empty, root.Hash, rootRlp, kind, mask, childPointers, out _, out ulong temporaryRootPointer);
        byte[] displacedRlp = NodeRecordBufferTests.Leaf(1, 8);
        warmed.TryAppend(null, TreePath.Empty.Append(3), ValueKeccak.Compute(displacedRlp), displacedRlp, out _, out _);
        DictionaryNodeSource source = new(nodes, warmed, null);

        TrieRoot after = BulkSet(new WarmedNodeRecordBuffer(buffer, warmed), source, null, new TrieRoot(root.Hash, temporaryRootPointer), [(keyC, NewValue)], false);
        ChildRef realChild = buffer.GetRecord(after.Pointer).View.Child(1);
        ChildRef transientChild = buffer.GetRecord(after.Pointer).View.Child(2);
        PatriciaTree expected = new(new RawScopedTrieStore(new MemDb()), LimboLogs.Instance);
        foreach ((Hash256 key, byte[] value) in items) expected.Set(key.Bytes, key == keyC ? NewValue : value);
        expected.UpdateRootHash();
        warmed.Reset();
        if (compact)
        {
            retain.Dispose();
            for (int i = 0; i < NodeRecordBufferTests.LiveGenerations + 2; i++) buffer.Reserve(SmallGenerationBytes, out _, out _);
            Assert.That(() => buffer.FindBloom(after.Pointer) is null && NodeRecordBufferTests.IsLocatable(buffer, after.Pointer),
                Is.True.After(NodeRecordBufferTests.CompactionTimeoutMilliseconds, NodeRecordBufferTests.PollingMilliseconds), "normalized output survives compaction");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(after.Hash, Is.EqualTo(expected.RootHash.ValueHash256));
            Assert.That(realChild.Pointer, Is.EqualTo(realChildPointer), "real pointers remain unchanged");
            Assert.That(transientChild.Pointer, Is.Zero, "unchanged temporary child is not promoted");
            Assert.That(transientChild.Data, Is.EqualTo(unchangedHash), "its hash remains unchanged");
            Assert.That(InvalidRecords(buffer, after), Is.Empty);
            Assert.That(ReadValues(buffer, source, after, [keyA, keyB, keyC]), Is.EqualTo(new[] { ExistingValue, ExistingValue, NewValue }));
            Assert.That(source.FetchCount, Is.EqualTo(1), "only the cleared temporary pointer falls back after reset");
        }
    }

    [Test]
    public void WarmUp_fetches_only_nodes_outside_the_buffer_and_bulk_set_then_reads_them_without_fetching()
    {
        Hash256 address = TestItem.KeccakA;
        List<(Hash256 key, byte[] value)> items = PatriciaTreeBulkSetterTests.GenRandomOfLength(1000, seed: 4);
        using NodeRecordBuffer buffer = new(0, SmallGenerationBytes);
        using NodeRecordBufferPin retain = buffer.Pin();
        TrieRoot root = BulkSet(buffer, new DictionaryNodeSource(new Dictionary<TreePath, byte[]>(), NewWarmedNodeLog(), address), address, TrieRoot.Empty, items, canParallelize: false);

        // A buffer without the trie stands for one written before the trie was persisted.
        using NodeRecordBuffer persistedBuffer = new(0, SmallGenerationBytes);
        TrieRoot persistedRoot = new(root.Hash, 0);
        Dictionary<TreePath, byte[]> persisted = Rlps(buffer, root);
        WarmedNodeLog warmed = NewWarmedNodeLog(persisted.Count);
        DictionaryNodeSource source = new(persisted, warmed, address);
        int fromBuffer = WarmUp(buffer, root, items[0].key);
        int fromSource = WarmUp(persistedBuffer, persistedRoot, items[0].key);
        int again = WarmUp(persistedBuffer, persistedRoot, items[0].key);
        foreach ((Hash256 key, _) in items) WarmUp(persistedBuffer, persistedRoot, key);
        int fetchesBeforeBulkSet = source.FetchCount;
        TrieRoot newRoot = BulkSet(new WarmedNodeRecordBuffer(persistedBuffer, warmed), source, address, persistedRoot, [(items[0].key, NewValue)], canParallelize: false);

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

        int WarmUp(INodeRecordBuffer walkedBuffer, TrieRoot walkedRoot, Hash256 key)
        {
            int before = source.FetchCount;
            source.WarmUp(walkedBuffer, walkedRoot, key.ValueHash256);
            return source.FetchCount - before;
        }
    }

    [Test]
    public void Warm_up_publishes_a_linked_verified_prefix_only_after_descent([Values(0, 1, 2)] int missingLeaf)
    {
        Hash256 keyA = new("1000000000000000000000000000000000000000000000000000000000000001");
        Hash256 keyB = new("1000000000000000000000000000000000000000000000000000000000000002");
        Hash256 keyC = new("2000000000000000000000000000000000000000000000000000000000000000");
        using NodeRecordBuffer original = new(0, SmallGenerationBytes);
        using NodeRecordBufferPin retain = original.Pin();
        TrieRoot root = BulkSet(original, new DictionaryNodeSource(new Dictionary<TreePath, byte[]>(), NewWarmedNodeLog(), null),
            null, TrieRoot.Empty, [(keyA, ExistingValue), (keyB, ExistingValue), (keyC, ExistingValue)], false);
        Dictionary<TreePath, byte[]> nodes = Rlps(original, root);
        TreePath leafPath = TreePath.FromPath(keyA.Bytes);
        if (missingLeaf == 1) nodes.Remove(leafPath);
        if (missingLeaf == 2) nodes[leafPath] = NodeRecordBufferTests.Leaf(9, 8);
        WarmedNodeLog warmed = NewWarmedNodeLog(16);
        int reads = 0;
        void Load(in TreePath path, in ValueHash256 hash, ReadOnlySpanAction<byte, BufferWarmUp.WarmUpRead> action, BufferWarmUp.WarmUpRead read)
        {
            Assert.That(warmed.Count, Is.Zero, "descent must not publish zero-linked ancestors");
            reads++;
            if (!nodes.TryGetValue(path, out byte[] rlp)) return;
            Span<byte> buffer = stackalloc byte[rlp.Length];
            rlp.CopyTo(buffer);
            action(buffer, read);
            buffer.Fill(0xFF);
        }
        using NodeRecordBuffer buffer = new(0, SmallGenerationBytes);

        BufferWarmUp.WarmUp(new WarmedNodeRecordBuffer(buffer, warmed), warmed, Load, null, new TrieRoot(root.Hash, 0), keyA.ValueHash256);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reads, Is.EqualTo(4));
            Assert.That(warmed.Count, Is.EqualTo(missingLeaf == 0 ? 4 : 3));
        }
        TreePath currentPath = TreePath.Empty;
        ValueHash256 currentHash = root.Hash;
        int linked = 0;
        Span<byte> extensionKey = stackalloc byte[64];
        while (warmed.TryGet(null, currentPath, currentHash, out NodeRecord record, out ulong pointer))
        {
            linked++;
            Assert.That(ValueKeccak.Compute(record.Rlp), Is.EqualTo(record.Hash));
            Assert.That(NodeRecordCodec.IsTransient(pointer), Is.True);
            if (record.Kind == NodeRecordKind.Leaf) break;
            int slot = record.Kind == NodeRecordKind.Extension ? 0 : leafPath[currentPath.Length];
            ChildRef child = record.View.Child(slot);
            if (currentPath.Length == 0) Assert.That(record.View.Child(2).Pointer, Is.Zero, "off-path subtrees remain unknown");
            if (child.Pointer == 0)
            {
                Assert.That(missingLeaf, Is.Not.Zero, "a complete warmed path must link every hashed child");
                break;
            }
            Assert.That(warmed.TryLocate(child.Pointer, out NodeRecord linkedChild) && linkedChild.Hash == child.Data, Is.True);
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
    public void Warm_up_keeps_borrowed_ancestors_alive_at_every_key_depth()
    {
        List<(Hash256 key, byte[] value)> items = [(new Hash256(new byte[32]), ExistingValue)];
        for (int depth = 0; depth < 64; depth++)
        {
            byte[] key = new byte[32];
            key[depth / 2] = depth % 2 == 0 ? (byte)0x10 : (byte)0x01;
            items.Add((new Hash256(key), ExistingValue));
        }
        using NodeRecordBuffer original = new(0, SmallGenerationBytes);
        using NodeRecordBufferPin retain = original.Pin();
        TrieRoot root = BulkSet(original, new DictionaryNodeSource(new Dictionary<TreePath, byte[]>(), NewWarmedNodeLog(), null),
            null, TrieRoot.Empty, items, false);
        Dictionary<TreePath, byte[]> nodes = Rlps(original, root);
        WarmedNodeLog warmed = NewWarmedNodeLog(1024);
        using NodeRecordBuffer buffer = new(0, SmallGenerationBytes);

        BufferWarmUp.WarmUp(new WarmedNodeRecordBuffer(buffer, warmed), warmed, Load, null, new TrieRoot(root.Hash, 0), ValueKeccak.Zero);

        TreePath path = TreePath.Empty;
        Assert.That(warmed.TryGet(null, path, root.Hash, out NodeRecord record, out _), Is.True);
        for (int depth = 0; depth <= 64; depth++)
        {
            Assert.That(record.Rlp.ToArray(), Is.EqualTo(nodes[path]));
            if (depth == 64) break;
            ChildRef child = record.View.Child(0);
            Assert.That(warmed.TryLocate(child.Pointer, out record) && record.Hash == child.Data, Is.True);
            path.AppendMut(0);
        }

        void Load(in TreePath path, in ValueHash256 hash, ReadOnlySpanAction<byte, BufferWarmUp.WarmUpRead> action, BufferWarmUp.WarmUpRead read)
        {
            byte[] rlp = nodes[path];
            Span<byte> buffer = stackalloc byte[rlp.Length];
            rlp.CopyTo(buffer);
            action(buffer, read);
            buffer.Fill(0xFF);
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
        using NodeRecordBuffer original = new(0, SmallGenerationBytes);
        using NodeRecordBufferPin retain = original.Pin();
        TrieRoot root = BulkSet(original, new DictionaryNodeSource(new Dictionary<TreePath, byte[]>(), NewWarmedNodeLog(), null), null, TrieRoot.Empty, items, false);
        Dictionary<TreePath, byte[]> persisted = Rlps(original, root);
        WarmedNodeLog warmed = NewWarmedNodeLog(1024);
        DictionaryNodeSource source = new(persisted, warmed, null);
        using NodeRecordBuffer buffer = new(0, SmallGenerationBytes);
        using NodeRecordBufferPin newRetain = buffer.Pin();
        TrieRoot persistedRoot = new(root.Hash, 0);

        source.WarmUp(buffer, persistedRoot, keyA.ValueHash256, forDelete);
        TreePath branchPath = TreePath.FromPath(keyA.Bytes);
        branchPath.TruncateOne();
        Assert.That(warmed.TryGet(null, branchPath, ValueKeccak.Compute(persisted[branchPath]), out NodeRecord warmedBranch, out _), Is.True);
        ChildRef warmedSibling = warmedBranch.View.Child(2);
        Assert.That(warmedSibling.Pointer, forDelete && !threeChildren ? Is.Not.Zero : Is.Zero, "only a required collapse sibling is linked");
        if (warmedSibling.Pointer != 0) Assert.That(warmed.TryLocate(warmedSibling.Pointer, out NodeRecord sibling) && sibling.Hash == warmedSibling.Data, Is.True);
        int before = source.FetchCount;
        TrieRoot updated = BulkSet(new WarmedNodeRecordBuffer(buffer, warmed), source, null, persistedRoot, [(keyA, null)], false);
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
        using NodeRecordBuffer original = new(0, SmallGenerationBytes);
        using NodeRecordBufferPin retain = original.Pin();
        TrieRoot root = BulkSet(original, new DictionaryNodeSource(new Dictionary<TreePath, byte[]>(), NewWarmedNodeLog(), null), null, TrieRoot.Empty,
            [(keyA, ExistingValue), (keyB, ExistingValue)], false);
        Dictionary<TreePath, byte[]> nodes = Rlps(original, root);
        DictionaryNodeSource pathSource = new(nodes, NewWarmedNodeLog(1024), null);
        DictionaryNodeSource deleteSource = new(nodes, NewWarmedNodeLog(1024), null);
        using NodeRecordBuffer buffer = new(0, SmallGenerationBytes);
        TrieRoot persistedRoot = new(root.Hash, 0);

        pathSource.WarmUp(buffer, persistedRoot, missing.ValueHash256);
        deleteSource.WarmUp(buffer, persistedRoot, missing.ValueHash256, forDelete: true);

        Assert.That(deleteSource.FetchCount, Is.EqualTo(pathSource.FetchCount));
    }

    [Test]
    public void Compaction_drops_rewritten_paths_and_keeps_unchanged_subtrees_reachable_by_pointer()
    {
        const ulong compressedBudget = 1UL << 20;
        const int fillerBatch = 16;
        using NodeRecordBuffer buffer = new(compressedBudget, SmallGenerationBytes);
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), NewWarmedNodeLog(), null);
        List<(Hash256 key, byte[] value)> items = PatriciaTreeBulkSetterTests.GenRandomOfLength(100, seed: 5);
        TrieRoot fillerRoot = TrieRoot.Empty;
        int fillerSeed = 0;
        TrieRoot root = BulkSet(buffer, source, null, TrieRoot.Empty, items, canParallelize: false);
        (TreePath Path, ulong Pointer)[] nodes = buffer.EnumerateNodes(root.Pointer).Select(node => (node.Path, node.Pointer)).ToArray();
        ulong newestTrieGeneration = nodes.Max(node => NodeRecordBufferTests.GenerationOf(node.Pointer));
        FillUntil(newestTrieGeneration + 1);
        TrieRoot newRoot = BulkSet(buffer, source, null, root, [(items[0].key, NewValue)], canParallelize: false);
        HashSet<TreePath> rewrittenPaths = [.. nodes.Where(node => !buffer.TryGetNodePointer(newRoot.Pointer, node.Path, out ulong pointer) || pointer != node.Pointer).Select(node => node.Path)];
        // Few large filler leaves per generation keep false positives out of the blooms the dropped trie is compacted against.
        FillUntil(newestTrieGeneration + NodeRecordBufferTests.LiveGenerations);
        Assert.That(() => NodeRecordBufferTests.IsLocatable(buffer, root.Pointer), Is.False.After(NodeRecordBufferTests.CompactionTimeoutMilliseconds, NodeRecordBufferTests.PollingMilliseconds),
            "the generations of the trie are compacted");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(NodeRecordBufferTests.GenerationOf(newRoot.Pointer), Is.GreaterThan(newestTrieGeneration), "generation of the rewrite");
            Assert.That(nodes.Select(node => NodeRecordBufferTests.IsLocatable(buffer, node.Pointer)), Is.EqualTo(nodes.Select(node => !rewrittenPaths.Contains(node.Path))), "records resolve unless their path was rewritten");
            Assert.That(ReadValues(buffer, source, newRoot, items.Select(item => item.key)), Is.EqualTo(items.Select((item, index) => index == 0 ? NewValue : item.value)), "values");
            Assert.That(source.FetchCount, Is.Zero, "nodes read from the buffer");
        }

        void FillUntil(ulong generation)
        {
            while (fillerRoot.Pointer == 0 || NodeRecordBufferTests.GenerationOf(fillerRoot.Pointer) < generation)
            {
                List<(Hash256 key, byte[] value)> filler = [.. Enumerable.Range(fillerSeed, fillerBatch).Select(seed => (Keccak.Compute(BitConverter.GetBytes(seed)), FillerValue))];
                fillerRoot = BulkSet(buffer, source, TestItem.KeccakA, TrieRoot.Empty, filler, canParallelize: false);
                fillerSeed += fillerBatch;
            }
        }
    }

    [Test]
    public void Traversal_finds_exact_node_paths_and_enumerates_in_depth_first_order([Values] bool inlineLeaves)
    {
        using NodeRecordBuffer buffer = new(0, SmallGenerationBytes);
        using NodeRecordBufferPin retain = buffer.Pin();
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), NewWarmedNodeLog(), null);
        byte[] value = inlineLeaves ? [1] : ExistingValue;
        TrieRoot root = BulkSet(buffer, source, null, TrieRoot.Empty,
        [
            (new Hash256("1000000000000000000000000000000000000000000000000000000000000001"), value),
            (new Hash256("1000000000000000000000000000000000000000000000000000000000000002"), ExistingValue),
            (new Hash256("2000000000000000000000000000000000000000000000000000000000000000"), ExistingValue),
        ], false);
        string branchPath = "1" + new string('0', 62);
        string[] expectedPaths = inlineLeaves
            ? ["", "1", branchPath, branchPath + "2", "2"]
            : ["", "1", branchPath, branchPath + "1", branchPath + "2", "2"];
        (TreePath Path, ulong Pointer)[] actual = buffer.EnumerateNodes(root.Pointer).Select(node => (node.Path, node.Pointer)).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.Select(node => node.Path), Is.EqualTo(expectedPaths.Select(TreePath.FromHexString)));
            foreach ((TreePath path, ulong pointer) in actual)
            {
                Assert.That(buffer.TryGetNodePointer(root.Pointer, path, out ulong found), Is.True, path.ToString());
                Assert.That(found, Is.EqualTo(pointer), path.ToString());
                Assert.That(buffer.TryGetNode(root.Pointer, path, out TrieNode node), Is.True, path.ToString());
                Assert.That(node?.FullRlp.ToArray(), Is.EqualTo(buffer.GetRecord(pointer).Rlp.ToArray()), path.ToString());
            }
            foreach (string path in new[] { "3", "10", "11", "20" })
            {
                Assert.That(buffer.TryGetNodePointer(root.Pointer, TreePath.FromHexString(path), out _), Is.False, path);
            }
            TreePath leafPath = TreePath.FromHexString("1000000000000000000000000000000000000000000000000000000000000001");
            Assert.That(buffer.TryGetNode(root.Pointer, leafPath, out _), Is.EqualTo(!inlineLeaves), "inline leaves have no separate buffer record");
            Assert.That(source.FetchCount, Is.Zero);
        }
    }

    [Test]
    public void Traversal_preserves_sparse_child_order_with_inline_and_unknown_children()
    {
        using NodeRecordBuffer buffer = new(0, SmallGenerationBytes);
        using NodeRecordBufferPin retain = buffer.Pin();
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), NewWarmedNodeLog(), null);
        string prefix = new('0', 63);
        TrieRoot root = BulkSet(buffer, source, null, TrieRoot.Empty,
        [
            (new Hash256(prefix + "1"), new byte[] { 1 }),
            (new Hash256(prefix + "5"), ExistingValue),
            (new Hash256(prefix + "9"), ExistingValue),
            (new Hash256(prefix + "f"), ExistingValue),
        ], false);
        NodeRecord extension = buffer.GetRecord(root.Pointer);
        ulong branchPointer = extension.View.Child(0).Pointer;
        NodeRecord branch = buffer.GetRecord(branchPointer);
        ulong firstChild = branch.View.Child(5).Pointer;
        ulong lastChild = branch.View.Child(15).Pointer;
        ulong unlinkedBranch = CopyWithPointer(branch, 1, 0);
        ulong unlinkedRoot = CopyWithPointer(extension, 0, unlinkedBranch);

        (TreePath Path, ulong Pointer)[] actual = buffer.EnumerateNodes(unlinkedRoot).Select(node => (node.Path, node.Pointer)).ToArray();
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
            ulong copy = buffer.Reserve(NodeRecordCodec.AlignedLength(original.Span.Length), out NativeNodeBuffer storage, out int offset);
            original.Span.CopyTo(storage.AsSpan(offset));
            BinaryPrimitives.WriteUInt64LittleEndian(storage.AsSpan(offset + NodeRecordCodec.HeaderLength + ordinal * NodeRecordCodec.PointerLength), pointer);
            return copy;
        }
    }

    [Test]
    public void Traversal_prunes_inherited_subtrees_at_an_inclusive_same_generation_boundary([Values] bool canParallelize)
    {
        using NodeRecordBuffer buffer = new(0, SmallGenerationBytes);
        using NodeRecordBufferPin originalRetain = buffer.Pin();
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), NewWarmedNodeLog(), null);
        Hash256 inheritedKey = new("1000000000000000000000000000000000000000000000000000000000000000");
        Hash256 updatedKey = new("f000000000000000000000000000000000000000000000000000000000000000");
        TrieRoot original = BulkSet(buffer, source, null, TrieRoot.Empty, [(inheritedKey, ExistingValue), (updatedKey, ExistingValue)], canParallelize);
        Assert.That(buffer.TryGetNodePointer(original.Pointer, TreePath.Empty.Append(1), out ulong inherited), Is.True);
        using NodeRecordBufferPin updateRetain = buffer.Pin();
        TrieRoot updated = BulkSet(buffer, source, null, original, [(updatedKey, NewValue)], canParallelize);
        (TreePath Path, ulong Pointer)[] delta = buffer.EnumerateNodes(updated.Pointer, updateRetain.WriteBoundary).Select(node => (node.Path, node.Pointer)).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated.Pointer >> 32, Is.EqualTo(original.Pointer >> 32));
            Assert.That(delta.Select(node => node.Path), Is.EqualTo(new[] { TreePath.Empty, TreePath.Empty.Append(15) }));
            Assert.That(delta.Min(node => node.Pointer), Is.EqualTo(updateRetain.WriteBoundary), "the first reservation belongs to this range");
            Assert.That(buffer.EnumerateNodes(original.Pointer, updateRetain.WriteBoundary), Is.Empty);
            Assert.That(buffer.TryGetNodePointer(updated.Pointer, TreePath.Empty.Append(1), out ulong found), Is.True);
            Assert.That(found, Is.EqualTo(inherited), "point reads still follow inherited records");
            Assert.That(buffer.GetRecord(found).Rlp.ToArray(), Is.Not.Empty);
            Assert.That(source.FetchCount, Is.Zero);
        }
    }

    [Test]
    public void Traversal_stops_at_unknown_child_pointers_without_loading_the_source()
    {
        using NodeRecordBuffer buffer = new(0, SmallGenerationBytes);
        using NodeRecordBufferPin retain = buffer.Pin();
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), NewWarmedNodeLog(), null);
        TrieRoot root = BulkSet(buffer, source, null, TrieRoot.Empty,
        [
            (new Hash256("1000000000000000000000000000000000000000000000000000000000000000"), ExistingValue),
            (new Hash256("2000000000000000000000000000000000000000000000000000000000000000"), ExistingValue),
        ], false);
        NodeRecord rootRecord = buffer.GetRecord(root.Pointer);
        ulong unlinked = buffer.AppendRecordFromRlp(null, TreePath.Empty, rootRecord.Hash, rootRecord.Rlp);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.EnumerateNodes(unlinked).Select(node => (node.Path, node.Pointer)).ToArray(), Is.EqualTo(new[] { (TreePath.Empty, unlinked) }));
            Assert.That(buffer.TryGetNodePointer(unlinked, TreePath.Empty, out ulong found), Is.True);
            Assert.That(found, Is.EqualTo(unlinked));
            Assert.That(buffer.TryGetNodePointer(unlinked, TreePath.FromHexString("1"), out _), Is.False);
            Assert.That(buffer.TryGetNode(unlinked, TreePath.FromHexString("2"), out _), Is.False);
            Assert.That(buffer.EnumerateNodes(0), Is.Empty);
            Assert.That(buffer.TryGetNodePointer(0, TreePath.Empty, out _), Is.False);
            Assert.That(buffer.TryGetNode(0, TreePath.Empty, out _), Is.False);
            Assert.That(source.FetchCount, Is.Zero);
        }
    }

    [Test]
    public void Traversal_skips_expired_inherited_children_but_keeps_the_new_root()
    {
        using NodeRecordBuffer buffer = new(0, SmallGenerationBytes);
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), NewWarmedNodeLog(), null);
        Hash256 first = new("1000000000000000000000000000000000000000000000000000000000000000");
        Hash256 second = new("2000000000000000000000000000000000000000000000000000000000000000");
        buffer.Reserve(0, out NativeNodeBuffer original, out _);
        TrieRoot root;
        using (buffer.Pin()) root = BulkSet(buffer, source, null, TrieRoot.Empty, [(first, ExistingValue), (second, ExistingValue)], false);
        buffer.Reserve(SmallGenerationBytes, out _, out _);
        using NodeRecordBufferPin retain = buffer.Pin();
        TrieRoot updated = BulkSet(buffer, source, null, root, [(first, NewValue)], false);
        while (NodeRecordBufferTests.GenerationOf(buffer.Reserve(SmallGenerationBytes, out _, out _)) < NodeRecordBufferTests.LiveGenerations + 1) { }
        Assert.That(() => original.IsDisposed, Is.True.After(NodeRecordBufferTests.CompactionTimeoutMilliseconds, NodeRecordBufferTests.PollingMilliseconds));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.TryGetNode(updated.Pointer, TreePath.Empty, out _), Is.True);
            Assert.That(buffer.TryGetNode(updated.Pointer, TreePath.FromHexString("1"), out _), Is.True);
            Assert.That(buffer.TryGetNode(updated.Pointer, TreePath.FromHexString("2"), out _), Is.False);
            Assert.That(buffer.EnumerateNodes(updated.Pointer).Select(node => node.Path), Is.EqualTo(new[] { TreePath.Empty, TreePath.FromHexString("1") }));
            Assert.That(buffer.EnumerateNodes(root.Pointer), Is.Empty);
            Assert.That(buffer.TryGetNode(root.Pointer, TreePath.Empty, out _), Is.False);
            Assert.That(source.FetchCount, Is.Zero);
        }
    }

    [Test]
    public void Traversal_handles_branches_at_every_key_depth()
    {
        using NodeRecordBuffer buffer = new(0, SmallGenerationBytes);
        using NodeRecordBufferPin retain = buffer.Pin();
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), NewWarmedNodeLog(), null);
        List<(Hash256 key, byte[] value)> items = [(Keccak.Zero, ExistingValue)];
        for (int depth = 0; depth < 64; depth++)
        {
            byte[] key = new byte[32];
            key[depth / 2] = (byte)((depth & 1) == 0 ? 0x10 : 0x01);
            items.Add((new Hash256(key), ExistingValue));
        }
        TrieRoot root = BulkSet(buffer, source, null, TrieRoot.Empty, items, false);
        byte[] expectedLeafRlp = new byte[NodeRlp.LeafLength([], ExistingValue)];
        NodeRlp.WriteLeaf([], ExistingValue, expectedLeafRlp);
        using (Assert.EnterMultipleScope())
        {
            IEnumerable<TreePath> expected = Enumerable.Range(0, 64).Select(depth => TreePath.FromHexString(new string('0', depth)))
                .Concat(Enumerable.Range(0, 64).Select(depth => TreePath.FromHexString(new string('0', depth) + "1")))
                .Append(TreePath.FromHexString(new string('0', 64)));
            Assert.That(buffer.EnumerateNodes(root.Pointer).Select(node => node.Path), Is.EquivalentTo(expected));
            Assert.That(buffer.TryGetNode(root.Pointer, TreePath.FromHexString(new string('0', 64)), out TrieNode leaf), Is.True);
            Assert.That(leaf?.FullRlp.ToArray(), Is.EqualTo(expectedLeafRlp));
        }
    }

    [Test]
    public void Traversal_node_lookup_returns_owned_rlp()
    {
        using NodeRecordBuffer buffer = new(0, SmallGenerationBytes);
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), NewWarmedNodeLog(), null);
        TrieRoot root;
        using (buffer.Pin()) root = BulkSet(buffer, source, null, TrieRoot.Empty, [(TestItem.KeccakA, ExistingValue)], false);
        Assert.That(buffer.TryGetNode(root.Pointer, TreePath.Empty, out TrieNode node), Is.True);
        byte[] expected = node.FullRlp.ToArray();
        buffer.Dispose();
        Assert.That(node.FullRlp.ToArray(), Is.EqualTo(expected));
    }

    [Test]
    public void Traversal_records_can_be_buffered_under_a_pin()
    {
        using NodeRecordBuffer buffer = new(0, SmallGenerationBytes);
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), NewWarmedNodeLog(), null);
        TrieRoot root;
        using (buffer.Pin()) root = BulkSet(buffer, source, null, TrieRoot.Empty, [(TestItem.KeccakA, ExistingValue)], false);
        NodeRecord borrowed;
        byte[] expected;
        using (buffer.PinAll())
        {
            NodeRecord[] buffered = buffer.EnumerateNodes(root.Pointer).Select(node => node.Record).ToArray();
            borrowed = buffered[0];
            expected = borrowed.Rlp.ToArray();
            buffer.Dispose();
            Assert.That(borrowed.Rlp.ToArray(), Is.EqualTo(expected), "the pin covers records after enumerator disposal");
        }
        Assert.That(() => borrowed.Rlp.ToArray(), Throws.InstanceOf<ObjectDisposedException>());
    }

    private static ulong InsertLinkedRecords(WarmedNodeLog warmed, Dictionary<TreePath, byte[]> nodes)
    {
        Dictionary<TreePath, ulong> pointers = [];
        Span<byte> extensionKey = stackalloc byte[64];
        Span<ulong> childPointers = stackalloc ulong[16];
        foreach ((TreePath path, byte[] rlp) in nodes.OrderByDescending(node => node.Key.Length))
        {
            NodeRecordCodec.TryMeasure(rlp, out NodeRecordKind kind, out ushort hashMask);
            byte[] bytes = new byte[NodeRecordCodec.Length(BitOperations.PopCount(hashMask), rlp.Length)];
            NodeRecordCodec.WriteFromRlp(rlp, kind, hashMask, ValueKeccak.Compute(rlp), NodeRecordCodec.NodeKey(null, path), bytes);
            NodeRecordRef view = new(bytes);
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

            warmed.AppendVerified(null, path, ValueKeccak.Compute(rlp), rlp, kind, hashMask, childPointers, out _, out ulong pointer);
            pointers[path] = pointer;
        }

        return pointers.GetValueOrDefault(TreePath.Empty);
    }

    private static TrieRoot BulkSet(INodeRecordBuffer buffer, INodeRecordSource source, Hash256 address, TrieRoot root, List<(Hash256 key, byte[] value)> items, bool canParallelize)
    {
        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = Entries(items);
        return BufferBulkSet.BulkSet(buffer, source, address, root, entries, canParallelize);
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

    private static Dictionary<TreePath, byte[]> Rlps(INodeRecordBuffer buffer, TrieRoot root) =>
        buffer.EnumerateNodes(root.Pointer).ToDictionary(node => node.Path, node => node.Record.Rlp.ToArray());

    /// <summary>The values at <paramref name="keys"/>, read by a <see cref="PatriciaTree"/> from the records linked from <paramref name="root"/>, falling back to <paramref name="source"/>.</summary>
    private static byte[][] ReadValues(INodeRecordBuffer buffer, INodeRecordSource source, TrieRoot root, IEnumerable<Hash256> keys)
    {
        PatriciaTree tree = new(new LinkedRecordResolver(Rlps(buffer, root), source), LimboLogs.Instance) { RootHash = root.ToHash256() };
        return keys.Select(key => tree.Get(key.Bytes) is { IsEmpty: false } value ? value.ToArray() : null).ToArray();
    }

    private sealed class LinkedRecordResolver(Dictionary<TreePath, byte[]> linked, INodeRecordSource source) : IScopedTrieStore
    {
        public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => new(NodeType.Unknown, hash);
        public byte[] TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            linked.TryGetValue(path, out byte[] rlp) && ValueKeccak.Compute(rlp) == hash.ValueHash256 ? rlp
            : source.TryLoad(path, hash.ValueHash256, out NodeRecord record) ? record.Rlp.ToArray() : null;
        public byte[] LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            TryLoadRlp(path, hash, flags) ?? throw new MissingTrieNodeException($"Trie node missing hash={hash} path={path}", null, path, hash);
        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256 address) => this;
        public INodeStorage.KeyScheme Scheme => INodeStorage.KeyScheme.HalfPath;
        public ICommitter BeginCommit(TrieNode root, WriteFlags flags = WriteFlags.None) => throw new NotSupportedException();
    }

    private static IEnumerable<string> InvalidRecords(INodeRecordBuffer buffer, TrieRoot root)
    {
        List<string> invalid = [];
        foreach ((TreePath path, ulong pointer, NodeRecord record) in buffer.EnumerateNodes(root.Pointer))
        {
            byte[] rlp = record.Rlp.ToArray();
            bool parses = NodeRecordCodec.TryMeasure(rlp, out NodeRecordKind kind, out ushort hashMask);
            bool transientPointer = NodeRecordCodec.IsTransient(pointer);
            for (int i = 0; i < BitOperations.PopCount(hashMask); i++)
            {
                transientPointer |= NodeRecordCodec.IsTransient(BinaryPrimitives.ReadUInt64LittleEndian(record.Span[(NodeRecordCodec.HeaderLength + i * NodeRecordCodec.PointerLength)..]));
            }

            if (ValueKeccak.Compute(rlp) != record.Hash || !parses || kind != record.Kind || hashMask != record.View.HashMask
                || record.View.NodeKey != NodeRecordCodec.NodeKey(null, path) || transientPointer)
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

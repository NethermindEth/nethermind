// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Native;
using Nethermind.Trie.Pruning;
using NUnit.Framework;
using NativeMetrics = Nethermind.Trie.Native.Metrics;

namespace Nethermind.Store.Test;

public class NativePatriciaBulkSetTests
{
    public static IEnumerable<TestCaseData> BulkSetTestGen()
    {
        foreach (TestCaseData testCase in PatriciaTreeBulkSetterTests.BulkSetTestGen()) yield return testCase;

        List<(Hash256 key, byte[] value)> existing = PatriciaTreeBulkSetterTests.GenRandomOfLength(10000, seed: 1);
        List<(Hash256 key, byte[] value)> changes =
        [
            .. existing.Take(2500).Select(item => (item.key, (byte[])[.. item.value, 1])),
            .. existing.Skip(2500).Take(2500).Select(item => (item.key, (byte[])null)),
            .. PatriciaTreeBulkSetterTests.GenRandomOfLength(5000, seed: 2),
        ];
        yield return new TestCaseData(existing, changes).SetName("parallel updates, deletes and inserts");
    }

    [TestCaseSource(nameof(BulkSetTestGen))]
    public void BulkSet_root_matches_patricia_tree(List<(Hash256 key, byte[] value)> existingItems, List<(Hash256 key, byte[] value)> items)
    {
        Assume.That(NativePatriciaBulkSet.IsAvailable, "libnethermind_patricia is not built");

        PatriciaTree tree = new(new RawScopedTrieStore(new MemDb()), LimboLogs.Instance);
        foreach ((Hash256 key, byte[] value) in existingItems.Concat(items)) tree.Set(key.Bytes, value);
        tree.UpdateRootHash();

        // Nothing is in the store, so the second bulk set reads the first one's nodes back from the log, or from the
        // pending nodes when the small log dropped them.
        DictionaryNodeStore store = new(new Dictionary<TreePath, byte[]>());
        using NativePendingNodes pending = NativePendingNodes.Rent(existingItems.Count);
        using NativeTrieNodeLog log = new(256 * 1024);
        Hash256 existingRoot = BulkSet(store, pending, log, Keccak.EmptyTreeHash, existingItems);
        Assert.That(BulkSet(store, pending, log, existingRoot, items), Is.EqualTo(tree.RootHash));

        Assert.That(Unsafe.SizeOf<TreePath>(), Is.EqualTo(40));
        Assert.That(Unsafe.SizeOf<NativeWrittenNode>(), Is.EqualTo(88));
        for (int shard = 0; shard < pending.ShardCount; shard++)
        {
            NativePendingNodesView view = pending.GetView(shard);
            Assert.That(Enumerable.Range(0, view.Count).Where(i => Keccak.Compute(view.GetRlp(view[i])).ValueHash256 != view[i].Hash), Is.Empty);
        }
    }

    [Test]
    public void BulkSet_metrics_count_log_hits_node_reads_log_memory_and_time()
    {
        Assume.That(NativePatriciaBulkSet.IsAvailable, "libnethermind_patricia is not built");

        List<(Hash256 key, byte[] value)> items = PatriciaTreeBulkSetterTests.GenRandomOfLength(1000, seed: 3);
        Dictionary<TreePath, byte[]> nodes = [];
        DictionaryNodeStore store = new(nodes);
        using NativePendingNodes pending = NativePendingNodes.Rent(items.Count);
        using NativeTrieNodeLog log = new(64 * 1024 * 1024);
        Hash256 root = BulkSet(store, pending, log, Keccak.EmptyTreeHash, items);
        for (int shard = 0; shard < pending.ShardCount; shard++)
        {
            NativePendingNodesView view = pending.GetView(shard);
            for (int i = 0; i < view.Count; i++) nodes[view[i].Path] = view.GetRlp(view[i]).ToArray();
        }

        // Other tests run bulk sets and logs concurrently, so the counters are only known to have grown by at least this.
        long rootHits = NativeMetrics.NativeTrieNodeLogRootHits;
        long nodesFromLog = NativeMetrics.NativeBulkSetNodesFromLog;
        long nodesFetched = NativeMetrics.NativeBulkSetNodesFetched;
        long bulkSetTime = NativeMetrics.NativeBulkSetTime;
        long callTime = NativeMetrics.NativeBulkSetCallTime;
        long fetchTime = NativeMetrics.NativeBulkSetFetchTime;

        BulkSet(store, pending, log, root, [(items[0].key, [1, 2, 3])]);
        // A new log does not have the trie, so its nodes are fetched from the store.
        using NativePendingNodes freshPending = NativePendingNodes.Rent(1);
        using NativeTrieNodeLog freshLog = new(64 * 1024 * 1024);
        NativePatriciaBulkSet.BulkSet(store, freshPending, freshLog, null, TestItem.KeccakA, root.ValueHash256, [new PatriciaTree.BulkSetEntry(items[1].key.ValueHash256, [4, 5, 6])]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(NativeMetrics.NativeTrieNodeLogRootHits, Is.GreaterThan(rootHits));
            Assert.That(NativeMetrics.NativeBulkSetNodesFromLog, Is.GreaterThan(nodesFromLog));
            Assert.That(NativeMetrics.NativeBulkSetNodesFetched, Is.GreaterThan(nodesFetched));
            Assert.That(NativeMetrics.NativeTrieNodeLogMemory, Is.GreaterThanOrEqualTo(2 * 1024 * 1024));
            Assert.That(NativeMetrics.NativeTrieNodeLogGenerations, Is.GreaterThanOrEqualTo(2));
            Assert.That(NativeMetrics.NativeBulkSetTime, Is.GreaterThan(bulkSetTime));
            Assert.That(NativeMetrics.NativeBulkSetCallTime, Is.GreaterThan(callTime));
            Assert.That(NativeMetrics.NativeBulkSetFetchTime, Is.GreaterThan(fetchTime));
        }
    }

    [Test]
    public void WarmUp_fetches_only_nodes_neither_logged_nor_warmed_which_bulk_sets_then_read_without_fetching()
    {
        Assume.That(NativePatriciaBulkSet.IsAvailable, "libnethermind_patricia is not built");

        List<(Hash256 key, byte[] value)> items = PatriciaTreeBulkSetterTests.GenRandomOfLength(1000, seed: 4);
        Dictionary<TreePath, byte[]> nodes = [];
        using NativePendingNodes pending = NativePendingNodes.Rent(items.Count);
        using NativeTrieNodeLog log = new(64 * 1024 * 1024);
        Hash256 root = BulkSet(new DictionaryNodeStore(nodes), pending, log, Keccak.EmptyTreeHash, items);
        for (int shard = 0; shard < pending.ShardCount; shard++)
        {
            NativePendingNodesView view = pending.GetView(shard);
            for (int i = 0; i < view.Count; i++) nodes[view[i].Path] = view.GetRlp(view[i]).ToArray();
        }

        // A log without the trie stands for one written before the trie was persisted.
        CountingNodeStore store = new(new DictionaryNodeStore(nodes));
        using NativeTrieNodeLog persistedLog = new(64 * 1024 * 1024);
        using NativeWarmedNodes warmed = new();
        int fromLog = WarmUp(log, items[0].key);
        int fromStore = WarmUp(persistedLog, items[0].key);
        int again = WarmUp(persistedLog, items[0].key);
        foreach ((Hash256 key, _) in items) WarmUp(persistedLog, key);

        int fetchesBeforeBulkSet = store.Fetches;
        using NativePendingNodes freshPending = NativePendingNodes.Rent(1);
        Hash256 newRoot = NativePatriciaBulkSet.BulkSet(store, freshPending, persistedLog, warmed, TestItem.KeccakA, root.ValueHash256, [new PatriciaTree.BulkSetEntry(items[0].key.ValueHash256, [1, 2, 3])]);

        PatriciaTree tree = new(new RawScopedTrieStore(new MemDb()), LimboLogs.Instance);
        foreach ((Hash256 key, byte[] value) in items) tree.Set(key.Bytes, value);
        tree.Set(items[0].key.Bytes, [1, 2, 3]);
        tree.UpdateRootHash();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fromLog, Is.Zero);
            Assert.That(fromStore, Is.GreaterThan(1));
            Assert.That(again, Is.Zero);
            Assert.That(store.Fetches, Is.EqualTo(fetchesBeforeBulkSet));
            Assert.That(newRoot, Is.EqualTo(tree.RootHash));
        }

        int WarmUp(NativeTrieNodeLog walkedLog, Hash256 key)
        {
            int before = store.Fetches;
            NativePatriciaBulkSet.WarmUp(store, walkedLog, warmed, TestItem.KeccakA, root.ValueHash256, key.ValueHash256);
            return store.Fetches - before;
        }
    }

    [Test]
    public void PinActiveGeneration_keeps_tries_written_after_it_in_the_log_until_every_copy_is_disposed()
    {
        Assume.That(NativePatriciaBulkSet.IsAvailable, "libnethermind_patricia is not built");

        List<(Hash256 key, byte[] value)> items = PatriciaTreeBulkSetterTests.GenRandomOfLength(1000, seed: 5);
        CountingNodeStore store = new(new DictionaryNodeStore(new Dictionary<TreePath, byte[]>()));
        using NativePendingNodes pending = NativePendingNodes.Rent(items.Count);
        using NativeTrieNodeLog log = new(256 * 1024);
        NativeTrieNodeLogPin pin = log.PinActiveGeneration();
        Hash256 root = BulkSet(store, pending, log, Keccak.EmptyTreeHash, items);
        NativeTrieNodeLogPin copy = pin.Copy();
        pin.Dispose();

        // Many times what the log holds, so without the pin it would have dropped the trie.
        for (int i = 0; i < 20; i++) BulkSet(store, pending, log, Keccak.EmptyTreeHash, PatriciaTreeBulkSetterTests.GenRandomOfLength(1000, seed: 10 + i));
        bool missingWhilePinned = IsMissing();
        // Every node written since the pin reads back from the log at its pointer, as in-memory snapshots read them.
        List<string> unreadable = [];
        for (int shard = 0; shard < pending.ShardCount; shard++)
        {
            NativePendingNodesView view = pending.GetView(shard);
            for (int i = 0; i < view.Count; i++)
            {
                ReadOnlySpan<byte> rlp = log.GetNodeRlp(view[i].LogPointer, out ValueHash256 hash);
                if (hash != view[i].Hash || !rlp.SequenceEqual(view.GetRlp(view[i]))) unreadable.Add(view[i].Path.ToString());
            }
        }
        copy.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(copy.Generation, Is.EqualTo(pin.Generation));
            Assert.That(missingWhilePinned, Is.False);
            Assert.That(pending.Count, Is.GreaterThan(0));
            Assert.That(unreadable, Is.Empty);
        }

        // A node not in the log is fetched to warm it, so a warm-up that fetches nothing read the whole path there.
        bool IsMissing()
        {
            using NativeWarmedNodes warmed = new();
            int before = store.Fetches;
            NativePatriciaBulkSet.WarmUp(store, log, warmed, TestItem.KeccakA, root.ValueHash256, items[0].key.ValueHash256);
            return store.Fetches != before;
        }
    }

    private static Hash256 BulkSet(INativeNodeStore store, NativePendingNodes pending, NativeTrieNodeLog log, Hash256 root, List<(Hash256 key, byte[] value)> items)
    {
        NativeBulkSetEntry[] entries = new NativeBulkSetEntry[items.Count];
        int offset = 0;
        for (int i = 0; i < items.Count; i++)
        {
            int length = items[i].value?.Length ?? 0;
            entries[i] = new NativeBulkSetEntry(items[i].key.ValueHash256, offset, length);
            offset += length;
        }

        return NativePatriciaBulkSet.BulkSet(store, pending, log, null, TestItem.KeccakA, root.ValueHash256, entries, items.SelectMany(item => item.value ?? []).ToArray());
    }

    private sealed class CountingNodeStore(INativeNodeStore inner) : INativeNodeStore
    {
        private int _fetches;

        public int Fetches => Volatile.Read(ref _fetches);

        public bool Fetch(in TreePath path, in ValueHash256 hash, NativeNodeReceiver receiver)
        {
            Interlocked.Increment(ref _fetches);
            return inner.Fetch(in path, in hash, receiver);
        }
    }
}

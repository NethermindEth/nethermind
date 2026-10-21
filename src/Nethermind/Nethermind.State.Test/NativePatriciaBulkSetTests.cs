// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
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

        // Nothing is in the store, so the second bulk set reads the first one's nodes back from the buffer, or from the
        // pending nodes when the small buffer dropped them.
        DictionaryNodeStore store = new(new Dictionary<TreePath, byte[]>());
        using NativePendingNodes pending = NativePendingNodes.Rent(existingItems.Count);
        using NativeNodeBuffer buffer = new(256 * 1024, NativeNodeBuffer.DefaultLiveGenerations);
        Hash256 existingRoot = BulkSet(store, pending, buffer, Keccak.EmptyTreeHash, existingItems);
        Assert.That(BulkSet(store, pending, buffer, existingRoot, items), Is.EqualTo(tree.RootHash));

        Assert.That(Unsafe.SizeOf<TreePath>(), Is.EqualTo(40));
        Assert.That(Unsafe.SizeOf<NativeWrittenNode>(), Is.EqualTo(88));
        for (int shard = 0; shard < pending.ShardCount; shard++)
        {
            NativePendingNodesView view = pending.GetView(shard);
            Assert.That(Enumerable.Range(0, view.Count).Where(i => Keccak.Compute(view.GetRlp(view[i])).ValueHash256 != view[i].Hash), Is.Empty);
        }
    }

    [TestCaseSource(nameof(BulkSetTestGen))]
    public void Prepared_bulk_set_root_matches_patricia_tree(List<(Hash256 key, byte[] value)> existingItems, List<(Hash256 key, byte[] value)> items)
    {
        Assume.That(NativePatriciaBulkSet.IsAvailable, "libnethermind_patricia is not built");

        DictionaryNodeStore store = new(new Dictionary<TreePath, byte[]>());
        using NativePendingNodes pending = NativePendingNodes.Rent(existingItems.Count);
        using NativeNodeBuffer buffer = new(256 * 1024, NativeNodeBuffer.DefaultLiveGenerations);
        using NativeNodeBufferPin pin = buffer.PinActiveGeneration();
        Hash256 existingRoot = BulkSet(store, pending, buffer, Keccak.EmptyTreeHash, existingItems);
        int pendingBefore = pending.Count;
        using NativeBulkSetState trie = NativeBulkSetState.Create(existingRoot.ValueHash256, TestItem.KeccakA);
        foreach ((Hash256 key, byte[] value)[] batch in items.Chunk(31))
        {
            PatriciaTree.BulkSetEntry[] entries = ToEntries(batch);
            NativePatriciaBulkSet.Update(store, trie, buffer, null, entries);
            NativePatriciaBulkSet.Update(store, trie, buffer, null, entries);
        }

        Assert.That(pending.Count, Is.EqualTo(pendingBefore));
        Hash256 root = NativePatriciaBulkSet.Finish(store, trie, pending, buffer, null, []);
        Assert.That(root, Is.EqualTo(ManagedRoot(existingItems.Concat(items))));
    }

    [Test]
    public void Prepared_bulk_set_reconciles_after_cross_thread_handoff_and_buffer_activity()
    {
        Assume.That(NativePatriciaBulkSet.IsAvailable, "libnethermind_patricia is not built");

        List<(Hash256 key, byte[] value)> initial = PatriciaTreeBulkSetterTests.GenRandomOfLength(1000, seed: 31);
        List<(Hash256 key, byte[] value)> hints = initial.Take(500).Select(item => (item.key, (byte[])[7])).ToList();
        List<(Hash256 key, byte[] value)> corrections =
        [
            .. initial.Take(250),
            .. initial.Skip(250).Take(250).Select(item => (item.key, (byte[])null)),
            .. PatriciaTreeBulkSetterTests.GenRandomOfLength(100, seed: 32),
        ];
        DictionaryNodeStore store = new(new Dictionary<TreePath, byte[]>());
        using NativePendingNodes pending = NativePendingNodes.Rent(initial.Count);
        using NativeNodeBuffer buffer = new(256 * 1024, NativeNodeBuffer.DefaultLiveGenerations);
        using NativeNodeBufferPin pin = buffer.PinActiveGeneration();
        Hash256 root = BulkSet(store, pending, buffer, Keccak.EmptyTreeHash, initial);
        pending.Clear();
        using NativeBulkSetState trie = NativeBulkSetState.Create(root.ValueHash256, TestItem.KeccakA);
        Task.Factory.StartNew(() => NativePatriciaBulkSet.Update(store, trie, buffer, null, ToEntries(hints)),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult();

        using NativePendingNodes unrelated = NativePendingNodes.Rent(1000);
        for (int i = 0; i < 20; i++) BulkSet(store, unrelated, buffer, Keccak.EmptyTreeHash, PatriciaTreeBulkSetterTests.GenRandomOfLength(1000, seed: 100 + i));

        Assert.That(pending.Count, Is.Zero);
        Hash256 finished = NativePatriciaBulkSet.Finish(store, trie, pending, buffer, null, ToEntries(corrections));
        Assert.That(finished, Is.EqualTo(ManagedRoot(initial.Concat(hints).Concat(corrections))));
        Assert.That(pending.Count, Is.GreaterThan(0));
    }

    [Test]
    public void Prepared_bulk_set_copies_input_buffers_and_rejects_finished_state()
    {
        Assume.That(NativePatriciaBulkSet.IsAvailable, "libnethermind_patricia is not built");

        DictionaryNodeStore store = new(new Dictionary<TreePath, byte[]>());
        using NativePendingNodes pending = NativePendingNodes.Rent(2);
        using NativeNodeBuffer buffer = new(256 * 1024, NativeNodeBuffer.DefaultLiveGenerations);
        ValueHash256 root = Keccak.EmptyTreeHash.ValueHash256;
        using NativeBulkSetState trie = NativeBulkSetState.Create(root, null);
        root = TestItem.KeccakA.ValueHash256;
        NativeBulkSetEntry[] entries = [new(TestItem.KeccakA.ValueHash256, 0, 2), new(TestItem.KeccakB.ValueHash256, 2, 2)];
        byte[] values = [1, 2, 3, 4];
        NativePatriciaBulkSet.Update(store, trie, buffer, null, entries, values);
        entries.AsSpan().Clear();
        values.AsSpan().Fill(99);
        Hash256 finished = NativePatriciaBulkSet.Finish(store, trie, pending, buffer, null,
            [new NativeBulkSetEntry(TestItem.KeccakA.ValueHash256, 0, 0)], []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(finished, Is.EqualTo(ManagedRoot([(TestItem.KeccakB, [3, 4])])));
            Assert.That(trie.IsClosed, Is.False);
            Assert.That(() => NativePatriciaBulkSet.Update(store, trie, buffer, null, []), Throws.InvalidOperationException.With.Message.Contains("status -2"));
            Assert.That(() => NativePatriciaBulkSet.Finish(store, trie, pending, buffer, null, []), Throws.InvalidOperationException.With.Message.Contains("status -2"));
        }
    }

    [Test]
    public void Prepared_bulk_set_can_be_discarded_before_starting_an_empty_trie()
    {
        Assume.That(NativePatriciaBulkSet.IsAvailable, "libnethermind_patricia is not built");

        DictionaryNodeStore store = new(new Dictionary<TreePath, byte[]>());
        using NativePendingNodes pending = NativePendingNodes.Rent(1);
        using NativeNodeBuffer buffer = new(256 * 1024, NativeNodeBuffer.DefaultLiveGenerations);
        using NativeBulkSetState discarded = NativeBulkSetState.Create(Keccak.EmptyTreeHash.ValueHash256, null);
        NativePatriciaBulkSet.Update(store, discarded, buffer, null, [new PatriciaTree.BulkSetEntry(TestItem.KeccakA.ValueHash256, [1])]);
        discarded.Dispose();
        discarded.Dispose();
        Assert.That(() => NativePatriciaBulkSet.Update(store, discarded, buffer, null, []), Throws.TypeOf<ObjectDisposedException>());
        Assert.That(pending.Count, Is.Zero);

        using NativeBulkSetState cleared = NativeBulkSetState.Create(Keccak.EmptyTreeHash.ValueHash256, null);
        Assert.That(NativePatriciaBulkSet.Finish(store, cleared, pending, buffer, null, []), Is.EqualTo(Keccak.EmptyTreeHash));
    }

    [Test]
    public void Prepared_bulk_set_callback_failure_invalidates_state([Values] bool finish)
    {
        Assume.That(NativePatriciaBulkSet.IsAvailable, "libnethermind_patricia is not built");

        InvalidOperationException cause = new("fetch failed");
        ThrowingNodeStore store = new(cause);
        using NativePendingNodes pending = NativePendingNodes.Rent(1);
        using NativeNodeBuffer buffer = new(256 * 1024, NativeNodeBuffer.DefaultLiveGenerations);
        using NativeBulkSetState trie = NativeBulkSetState.Create(TestItem.KeccakA.ValueHash256, null);
        PatriciaTree.BulkSetEntry[] entries = [new(TestItem.KeccakB.ValueHash256, [1])];
        Assert.That(() => Call(), Throws.InvalidOperationException.With.InnerException.SameAs(cause));
        Assert.That(() => NativePatriciaBulkSet.Update(store, trie, buffer, null, []), Throws.InvalidOperationException.With.Message.Contains("status -2"));

        void Call()
        {
            if (finish) NativePatriciaBulkSet.Finish(store, trie, pending, buffer, null, entries);
            else NativePatriciaBulkSet.Update(store, trie, buffer, null, entries);
        }
    }

    [Test]
    public void Prepared_bulk_set_releases_callback_state([Values] bool finish, [Values] bool fail)
    {
        Assume.That(NativePatriciaBulkSet.IsAvailable, "libnethermind_patricia is not built");

        using NativePendingNodes pending = NativePendingNodes.Rent(1);
        using NativeNodeBuffer buffer = new(256 * 1024, NativeNodeBuffer.DefaultLiveGenerations);
        using NativeBulkSetState trie = NativeBulkSetState.Create(fail ? TestItem.KeccakA.ValueHash256 : Keccak.EmptyTreeHash.ValueHash256, null);
        WeakReference store = CallWithTransientStore(trie, pending, buffer, finish, fail);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.That(store.IsAlive, Is.False);
    }

    [Test]
    public async Task Prepared_bulk_set_keeps_native_handles_alive_during_call([Values] bool finish)
    {
        Assume.That(NativePatriciaBulkSet.IsAvailable, "libnethermind_patricia is not built");

        List<(Hash256 key, byte[] value)> initial = [(TestItem.KeccakA, [1]), (TestItem.KeccakB, [2])];
        Dictionary<TreePath, byte[]> nodes = [];
        using NativePendingNodes pending = NativePendingNodes.Rent(initial.Count);
        using NativeNodeBuffer initialBuffer = new(256 * 1024, NativeNodeBuffer.DefaultLiveGenerations);
        Hash256 root = BulkSet(new DictionaryNodeStore(nodes), pending, initialBuffer, Keccak.EmptyTreeHash, initial);
        for (int shard = 0; shard < pending.ShardCount; shard++)
        {
            NativePendingNodesView view = pending.GetView(shard);
            for (int i = 0; i < view.Count; i++) nodes[view[i].Path] = view.GetRlp(view[i]).ToArray();
        }
        pending.Clear();
        using NativeNodeBuffer buffer = new(256 * 1024, NativeNodeBuffer.DefaultLiveGenerations);
        using NativeWarmedNodes warmed = new();
        using NativeBulkSetState trie = NativeBulkSetState.Create(root.ValueHash256, TestItem.KeccakA);
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        BlockingNodeStore store = new(new DictionaryNodeStore(nodes), entered, release);
        PatriciaTree.BulkSetEntry[] entries = [new(TestItem.KeccakA.ValueHash256, [3])];
        Task<Hash256> call = Task.Run(() =>
        {
            if (finish) return NativePatriciaBulkSet.Finish(store, trie, pending, buffer, warmed, entries);
            NativePatriciaBulkSet.Update(store, trie, buffer, warmed, entries);
            return null;
        });
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
            trie.Dispose();
            pending.Dispose();
            buffer.Dispose();
            warmed.Dispose();
        }
        finally
        {
            release.Set();
        }

        Hash256 result = await call.WaitAsync(TimeSpan.FromSeconds(10));
        using (Assert.EnterMultipleScope())
        {
            if (finish) Assert.That(result, Is.EqualTo(ManagedRoot(initial.Concat([(TestItem.KeccakA, (byte[])[3])]))));
            Assert.That(trie.IsClosed, Is.True);
            Assert.That(pending.IsClosed, Is.True);
            Assert.That(buffer.IsClosed, Is.True);
            Assert.That(warmed.IsClosed, Is.True);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CallWithTransientStore(NativeBulkSetState trie, NativePendingNodes pending, NativeNodeBuffer buffer, bool finish, bool fail)
    {
        INativeNodeStore store = fail ? new ThrowingNodeStore(new InvalidOperationException("fetch failed")) : new DictionaryNodeStore(new Dictionary<TreePath, byte[]>());
        WeakReference reference = new(store);
        PatriciaTree.BulkSetEntry[] entries = [new(TestItem.KeccakB.ValueHash256, [1])];
        void Call()
        {
            if (finish) NativePatriciaBulkSet.Finish(store, trie, pending, buffer, null, entries);
            else NativePatriciaBulkSet.Update(store, trie, buffer, null, entries);
        }
        if (fail) Assert.That(() => Call(), Throws.InvalidOperationException);
        else Call();
        return reference;
    }

    private static PatriciaTree.BulkSetEntry[] ToEntries(IEnumerable<(Hash256 key, byte[] value)> items) =>
        items.Select(item => new PatriciaTree.BulkSetEntry(item.key.ValueHash256, item.value)).ToArray();

    private static Hash256 ManagedRoot(IEnumerable<(Hash256 key, byte[] value)> items)
    {
        using MemDb db = new();
        PatriciaTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);
        foreach ((Hash256 key, byte[] value) in items) tree.Set(key.Bytes, value);
        tree.UpdateRootHash();
        return tree.RootHash;
    }

    private sealed class BlockingNodeStore(INativeNodeStore inner, ManualResetEventSlim entered, ManualResetEventSlim release) : INativeNodeStore
    {
        public bool Fetch(in TreePath path, in ValueHash256 hash, NativeNodeReceiver receiver)
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Native call was not released");
            return inner.Fetch(in path, in hash, receiver);
        }
    }

    private sealed class ThrowingNodeStore(Exception exception) : INativeNodeStore
    {
        public bool Fetch(in TreePath path, in ValueHash256 hash, NativeNodeReceiver receiver) => throw exception;
    }

    [Test]
    public void BulkSet_metrics_count_buffer_hits_node_reads_buffer_memory_and_time()
    {
        Assume.That(NativePatriciaBulkSet.IsAvailable, "libnethermind_patricia is not built");

        List<(Hash256 key, byte[] value)> items = PatriciaTreeBulkSetterTests.GenRandomOfLength(1000, seed: 3);
        Dictionary<TreePath, byte[]> nodes = [];
        DictionaryNodeStore store = new(nodes);
        using NativePendingNodes pending = NativePendingNodes.Rent(items.Count);
        using NativeNodeBuffer buffer = new(64 * 1024 * 1024, NativeNodeBuffer.DefaultLiveGenerations);
        Hash256 root = BulkSet(store, pending, buffer, Keccak.EmptyTreeHash, items);
        for (int shard = 0; shard < pending.ShardCount; shard++)
        {
            NativePendingNodesView view = pending.GetView(shard);
            for (int i = 0; i < view.Count; i++) nodes[view[i].Path] = view.GetRlp(view[i]).ToArray();
        }

        // Other tests run bulk sets and buffers concurrently, so the counters are only known to have grown by at least this.
        long rootHits = NativeMetrics.NodeRecordBufferRootHits;
        long nodesFromBuffer = NativeMetrics.NativeBulkSetNodesFromBuffer;
        long nodesFetched = NativeMetrics.NativeBulkSetNodesFetched;
        long bulkSetTime = NativeMetrics.NativeBulkSetTime;
        long callTime = NativeMetrics.NativeBulkSetCallTime;
        long fetchTime = NativeMetrics.NativeBulkSetFetchTime;

        BulkSet(store, pending, buffer, root, [(items[0].key, [1, 2, 3])]);
        // A new buffer does not have the trie, so its nodes are fetched from the store.
        using NativePendingNodes freshPending = NativePendingNodes.Rent(1);
        using NativeNodeBuffer freshBuffer = new(64 * 1024 * 1024, NativeNodeBuffer.DefaultLiveGenerations);
        NativePatriciaBulkSet.BulkSet(store, freshPending, freshBuffer, null, TestItem.KeccakA, root.ValueHash256, [new PatriciaTree.BulkSetEntry(items[1].key.ValueHash256, [4, 5, 6])]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(NativeMetrics.NodeRecordBufferRootHits, Is.GreaterThan(rootHits));
            Assert.That(NativeMetrics.NativeBulkSetNodesFromBuffer, Is.GreaterThan(nodesFromBuffer));
            Assert.That(NativeMetrics.NativeBulkSetNodesFetched, Is.GreaterThan(nodesFetched));
            Assert.That(NativeMetrics.NodeRecordBufferMemory, Is.GreaterThanOrEqualTo(2 * 1024 * 1024));
            Assert.That(NativeMetrics.NodeRecordBufferGenerations, Is.GreaterThanOrEqualTo(2));
            Assert.That(NativeMetrics.NativeBulkSetTime, Is.GreaterThan(bulkSetTime));
            Assert.That(NativeMetrics.NativeBulkSetCallTime, Is.GreaterThan(callTime));
            Assert.That(NativeMetrics.NativeBulkSetFetchTime, Is.GreaterThan(fetchTime));
        }
    }

    [Test]
    public void WarmUp_fetches_only_nodes_neither_buffered_nor_warmed_which_bulk_sets_then_read_without_fetching()
    {
        Assume.That(NativePatriciaBulkSet.IsAvailable, "libnethermind_patricia is not built");

        List<(Hash256 key, byte[] value)> items = PatriciaTreeBulkSetterTests.GenRandomOfLength(1000, seed: 4);
        Dictionary<TreePath, byte[]> nodes = [];
        using NativePendingNodes pending = NativePendingNodes.Rent(items.Count);
        using NativeNodeBuffer buffer = new(64 * 1024 * 1024, NativeNodeBuffer.DefaultLiveGenerations);
        Hash256 root = BulkSet(new DictionaryNodeStore(nodes), pending, buffer, Keccak.EmptyTreeHash, items);
        for (int shard = 0; shard < pending.ShardCount; shard++)
        {
            NativePendingNodesView view = pending.GetView(shard);
            for (int i = 0; i < view.Count; i++) nodes[view[i].Path] = view.GetRlp(view[i]).ToArray();
        }

        // A buffer without the trie stands for one written before the trie was persisted.
        CountingNodeStore store = new(new DictionaryNodeStore(nodes));
        using NativeNodeBuffer persistedBuffer = new(64 * 1024 * 1024, NativeNodeBuffer.DefaultLiveGenerations);
        using NativeWarmedNodes warmed = new();
        int fromBuffer = WarmUp(buffer, items[0].key);
        int fromStore = WarmUp(persistedBuffer, items[0].key);
        int again = WarmUp(persistedBuffer, items[0].key);
        foreach ((Hash256 key, _) in items) WarmUp(persistedBuffer, key);

        int fetchesBeforeBulkSet = store.Fetches;
        using NativePendingNodes freshPending = NativePendingNodes.Rent(1);
        Hash256 newRoot = NativePatriciaBulkSet.BulkSet(store, freshPending, persistedBuffer, warmed, TestItem.KeccakA, root.ValueHash256, [new PatriciaTree.BulkSetEntry(items[0].key.ValueHash256, [1, 2, 3])]);

        PatriciaTree tree = new(new RawScopedTrieStore(new MemDb()), LimboLogs.Instance);
        foreach ((Hash256 key, byte[] value) in items) tree.Set(key.Bytes, value);
        tree.Set(items[0].key.Bytes, [1, 2, 3]);
        tree.UpdateRootHash();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fromBuffer, Is.Zero);
            Assert.That(fromStore, Is.GreaterThan(1));
            Assert.That(again, Is.Zero);
            Assert.That(store.Fetches, Is.EqualTo(fetchesBeforeBulkSet));
            Assert.That(newRoot, Is.EqualTo(tree.RootHash));
        }

        int WarmUp(NativeNodeBuffer walkedBuffer, Hash256 key)
        {
            int before = store.Fetches;
            NativePatriciaBulkSet.WarmUp(store, walkedBuffer, warmed, TestItem.KeccakA, root.ValueHash256, key.ValueHash256);
            return store.Fetches - before;
        }
    }

    [Test, NonParallelizable]
    public void Buffer_allocation_survives_owner_disposal_until_pins_are_released_and_does_not_root_its_finalizer([Values] bool disposeOwner)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long before = NativeMetrics.NodeRecordBufferMemory;
        WeakReference buffer = CreatePinnedBuffer(disposeOwner, before);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.IsAlive, Is.False);
            Assert.That(NativeMetrics.NodeRecordBufferMemory, Is.EqualTo(before));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static WeakReference CreatePinnedBuffer(bool disposeOwner, long before)
        {
            NativeNodeBuffer buffer = new(256 * 1024, NativeNodeBuffer.DefaultLiveGenerations);
            using NativeNodeBufferPin pin = buffer.PinActiveGeneration();
            using NativeNodeBufferPin copy = pin.Copy();
            long allocated = NativeMetrics.NodeRecordBufferMemory;
            Assert.That(allocated, Is.GreaterThan(before));
            if (disposeOwner) buffer.Dispose();
            pin.Dispose();
            Assert.That(NativeMetrics.NodeRecordBufferMemory, Is.EqualTo(allocated));
            copy.Dispose();
            Assert.That(NativeMetrics.NodeRecordBufferMemory, Is.EqualTo(disposeOwner ? before : allocated));
            return new WeakReference(buffer);
        }
    }

    [Test]
    public void PinActiveGeneration_keeps_tries_written_after_it_in_the_buffer_until_every_copy_is_disposed()
    {
        Assume.That(NativePatriciaBulkSet.IsAvailable, "libnethermind_patricia is not built");

        List<(Hash256 key, byte[] value)> items = PatriciaTreeBulkSetterTests.GenRandomOfLength(1000, seed: 5);
        CountingNodeStore store = new(new DictionaryNodeStore(new Dictionary<TreePath, byte[]>()));
        using NativePendingNodes pending = NativePendingNodes.Rent(items.Count);
        using NativeNodeBuffer buffer = new(256 * 1024, NativeNodeBuffer.DefaultLiveGenerations);
        NativeNodeBufferPin pin = buffer.PinActiveGeneration();
        Hash256 root = BulkSet(store, pending, buffer, Keccak.EmptyTreeHash, items);
        NativeNodeBufferPin copy = pin.Copy();
        pin.Dispose();

        // Many times what the buffer holds, so without the pin it would have dropped the trie.
        for (int i = 0; i < 20; i++) BulkSet(store, pending, buffer, Keccak.EmptyTreeHash, PatriciaTreeBulkSetterTests.GenRandomOfLength(1000, seed: 10 + i));
        bool missingWhilePinned = IsMissing();
        // Every node written since the pin reads back from the buffer at its pointer, as in-memory snapshots read them.
        List<string> unreadable = [];
        for (int shard = 0; shard < pending.ShardCount; shard++)
        {
            NativePendingNodesView view = pending.GetView(shard);
            for (int i = 0; i < view.Count; i++)
            {
                ReadOnlySpan<byte> rlp = buffer.GetNodeRlp(view[i].BufferPointer, out ValueHash256 hash);
                if (hash != view[i].Hash || !rlp.SequenceEqual(view.GetRlp(view[i]))) unreadable.Add(view[i].Path.ToString());
            }
        }
        copy.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(copy.Generation, Is.EqualTo(pin.Generation));
            Assert.That(copy.WriteBoundary, Is.EqualTo(pin.WriteBoundary));
            Assert.That(missingWhilePinned, Is.False);
            Assert.That(pending.Count, Is.GreaterThan(0));
            Assert.That(unreadable, Is.Empty);
        }

        // A node not in the buffer is fetched to warm it, so a warm-up that fetches nothing read the whole path there.
        bool IsMissing()
        {
            using NativeWarmedNodes warmed = new();
            int before = store.Fetches;
            NativePatriciaBulkSet.WarmUp(store, buffer, warmed, TestItem.KeccakA, root.ValueHash256, items[0].key.ValueHash256);
            return store.Fetches != before;
        }
    }

    [Test]
    public void Root_snapshot_enumeration_respects_same_generation_write_boundary([Values] bool storage)
    {
        Assume.That(NativePatriciaBulkSet.IsAvailable, "libnethermind_patricia is not built");
        Hash256 address = storage ? TestItem.KeccakA : null;
        List<(Hash256 key, byte[] value)> initial = PatriciaTreeBulkSetterTests.GenRandomOfLength(48, seed: 71);
        CountingNodeStore store = new(new DictionaryNodeStore(new Dictionary<TreePath, byte[]>()));
        using NativeNodeBuffer buffer = new(256 * 1024, NativeNodeBuffer.DefaultLiveGenerations);
        using NativePendingNodes pending = NativePendingNodes.Rent(initial.Count);
        using NativeNodeBufferPin first = buffer.PinActiveGeneration();
        Hash256 root = NativePatriciaBulkSet.BulkSet(store, pending, buffer, null, address, Keccak.EmptyTreeHash.ValueHash256, ToEntries(initial));
        ulong oldRoot = buffer.ResolveRoot(address, root.ValueHash256);
        pending.Clear();
        using NativeNodeBufferPin second = buffer.PinActiveGeneration();
        using NativeNodeBufferPin copy = second.Copy();
        Hash256 updated = NativePatriciaBulkSet.BulkSet(store, pending, buffer, null, address, root.ValueHash256,
            [new PatriciaTree.BulkSetEntry(initial[0].key.ValueHash256, new byte[40])]);
        ulong pointer = buffer.ResolveRoot(address, updated.ValueHash256);
        HashSet<ulong> pendingPointers = [];
        for (int shard = 0; shard < pending.ShardCount; shard++)
        {
            NativePendingNodesView view = pending.GetView(shard);
            for (int i = 0; i < view.Count; i++) pendingPointers.Add(view[i].BufferPointer);
        }
        (TreePath Path, ulong Pointer)[] writes = buffer.EnumerateNodes(pointer, second.WriteBoundary).ToArray();
        (TreePath Path, ulong Pointer)[] reachable = buffer.EnumerateNodes(pointer, first.WriteBoundary).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.Generation, Is.EqualTo(first.Generation));
            Assert.That(second.WriteBoundary, Is.GreaterThan(first.WriteBoundary));
            Assert.That(copy.WriteBoundary, Is.EqualTo(second.WriteBoundary));
            Assert.That(writes.Select(node => node.Pointer), Is.EquivalentTo(pendingPointers));
            Assert.That(reachable.Length, Is.GreaterThan(writes.Length), "inherited subtrees are omitted from the new write range");
            Assert.That(buffer.EnumerateNodes(oldRoot, second.WriteBoundary), Is.Empty);
            Assert.That(store.Fetches, Is.Zero);
            foreach ((TreePath path, ulong record) in reachable)
            {
                Assert.That(buffer.TryGetNode(pointer, path, out TrieNode node), Is.True, $"node at {path}");
                Assert.That(node!.FullRlp.ToArray(), Is.EqualTo(buffer.GetNodeRlp(record, out _).ToArray()));
            }
        }
    }

    [Test]
    public void Root_lookup_copies_large_rlp_and_returns_miss_for_unavailable_path()
    {
        Assume.That(NativePatriciaBulkSet.IsAvailable, "libnethermind_patricia is not built");
        DictionaryNodeStore store = new(new Dictionary<TreePath, byte[]>());
        using NativeNodeBuffer buffer = new(256 * 1024, NativeNodeBuffer.DefaultLiveGenerations);
        using NativeNodeBufferPin pin = buffer.PinActiveGeneration();
        using NativePendingNodes pending = NativePendingNodes.Rent(1);
        Hash256 root = BulkSet(store, pending, buffer, Keccak.EmptyTreeHash, [(TestItem.KeccakA, new byte[3000])]);
        ulong pointer = buffer.ResolveRoot(TestItem.KeccakA, root.ValueHash256);
        Assert.That(buffer.TryGetNode(pointer, TreePath.Empty, out TrieNode node), Is.True);
        byte[] expected = buffer.GetNodeRlp(pointer, out _).ToArray();
        Assert.That(buffer.TryGetNode(pointer, TreePath.FromHexString("1"), out _), Is.False);
        Assert.That(buffer.TryGetNode(0, TreePath.Empty, out _), Is.False);
        pin.Dispose();
        buffer.Dispose();
        Assert.That(node!.FullRlp.ToArray(), Is.EqualTo(expected), "managed node owns its RLP copy");
    }

    [Test]
    public void Root_snapshot_cursor_keeps_buffer_alive_after_original_pin_and_owner_are_disposed()
    {
        Assume.That(NativePatriciaBulkSet.IsAvailable, "libnethermind_patricia is not built");
        using NativeNodeBuffer buffer = new(256 * 1024, NativeNodeBuffer.DefaultLiveGenerations);
        using NativeNodeBufferPin pin = buffer.PinActiveGeneration();
        using NativePendingNodes pending = NativePendingNodes.Rent(48);
        Hash256 root = BulkSet(new DictionaryNodeStore(new Dictionary<TreePath, byte[]>()), pending, buffer, Keccak.EmptyTreeHash,
            PatriciaTreeBulkSetterTests.GenRandomOfLength(48, seed: 72));
        ulong pointer = buffer.ResolveRoot(TestItem.KeccakA, root.ValueHash256);
        int expected = buffer.EnumerateNodes(pointer, pin.WriteBoundary).Count();
        using IEnumerator<(TreePath Path, ulong Pointer)> cursor = buffer.EnumerateNodes(pointer, pin.WriteBoundary).GetEnumerator();
        Assert.That(cursor.MoveNext(), Is.True);
        pin.Dispose();
        buffer.Dispose();
        int found = 1;
        while (cursor.MoveNext()) found++;
        Assert.That(found, Is.EqualTo(expected));
    }

    [Test]
    public void Warmed_records_are_local_to_their_resource_and_reset()
    {
        Assume.That(NativePatriciaBulkSet.IsAvailable, "libnethermind_patricia is not built");
        List<(Hash256 key, byte[] value)> initial = PatriciaTreeBulkSetterTests.GenRandomOfLength(48, seed: 73);
        Dictionary<TreePath, byte[]> nodes = [];
        using NativeNodeBuffer writer = new(256 * 1024, NativeNodeBuffer.DefaultLiveGenerations);
        using NativePendingNodes pending = NativePendingNodes.Rent(initial.Count);
        Hash256 root = BulkSet(new DictionaryNodeStore(nodes), pending, writer, Keccak.EmptyTreeHash, initial);
        for (int shard = 0; shard < pending.ShardCount; shard++)
        {
            NativePendingNodesView view = pending.GetView(shard);
            for (int i = 0; i < view.Count; i++) nodes[view[i].Path] = view.GetRlp(view[i]).ToArray();
        }
        using NativeNodeBuffer buffer = new(256 * 1024, NativeNodeBuffer.DefaultLiveGenerations);
        CountingNodeStore store = new(new DictionaryNodeStore(nodes));
        using NativeWarmedNodes first = new();
        using NativeWarmedNodes second = new();
        ValueHash256 key = initial[0].key.ValueHash256;
        NativePatriciaBulkSet.WarmUp(store, buffer, first, TestItem.KeccakA, root.ValueHash256, key);
        Assert.That(store.Fetches, Is.Positive);
        int fetches = store.Fetches;
        NativePatriciaBulkSet.WarmUp(store, buffer, first, TestItem.KeccakA, root.ValueHash256, key);
        Assert.That(store.Fetches, Is.EqualTo(fetches), "a resource reuses its warmed path");
        NativePatriciaBulkSet.WarmUp(store, buffer, second, TestItem.KeccakA, root.ValueHash256, key);
        Assert.That(store.Fetches, Is.GreaterThan(fetches), "a separate resource fetches its own records");
        fetches = store.Fetches;
        first.Reset();
        NativePatriciaBulkSet.WarmUp(store, buffer, second, TestItem.KeccakA, root.ValueHash256, key);
        Assert.That(store.Fetches, Is.EqualTo(fetches), "reset does not invalidate another resource's warmed path");
        NativePatriciaBulkSet.WarmUp(store, buffer, first, TestItem.KeccakA, root.ValueHash256, key);
        Assert.That(store.Fetches, Is.GreaterThan(fetches), "reset discards warmed records instead of promoting them");
    }

    private static Hash256 BulkSet(INativeNodeStore store, NativePendingNodes pending, NativeNodeBuffer buffer, Hash256 root, List<(Hash256 key, byte[] value)> items)
    {
        NativeBulkSetEntry[] entries = new NativeBulkSetEntry[items.Count];
        int offset = 0;
        for (int i = 0; i < items.Count; i++)
        {
            int length = items[i].value?.Length ?? 0;
            entries[i] = new NativeBulkSetEntry(items[i].key.ValueHash256, offset, length);
            offset += length;
        }

        return NativePatriciaBulkSet.BulkSet(store, pending, buffer, null, TestItem.KeccakA, root.ValueHash256, entries, items.SelectMany(item => item.value ?? []).ToArray());
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

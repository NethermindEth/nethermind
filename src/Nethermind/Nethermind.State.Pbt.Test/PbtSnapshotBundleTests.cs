// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtSnapshotBundleTests
{
    [Test]
    public void LocalCanonicalWrites_OverrideSharedAndPersistedLeaves()
    {
        PbtFullKey key = new([1]);
        ValueHash256 persisted = new([1]);
        ValueHash256 shared = new([2]);
        ValueHash256 local = new([3]);
        PbtResourcePool pool = new(new PbtConfig());
        PbtSnapshotContent sharedContent = new();
        sharedContent.SetLeaf(key, shared);
        PbtSnapshotPooledList sharedSnapshots = new(1)
        {
            new PbtSnapshot(StateId.PreGenesis, new StateId(1, default), default, sharedContent, pool, PbtResourcePool.Usage.MainBlockProcessing)
        };
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(sharedSnapshots, new Reader(key, persisted)), pool, PbtResourcePool.Usage.MainBlockProcessing);
        Assert.That(bundle.GetLeaf(key), Is.EqualTo(shared));
        bundle.SetLeaf(key, local);
        Assert.That(bundle.GetLeaf(key), Is.EqualTo(local));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SnapshotStore_updater_failure_leaves_leaf_and_node_deltas_unchanged(bool hashMismatch)
    {
        PbtResourcePool pool = new(new PbtConfig());
        PbtFullKey originalLeafKey = new([1]);
        ValueHash256 originalLeafValue = new([2]);
        PbtNodePath originalNodePath = new([0x80], 1);
        byte[] originalNode = [0x7F];
        Reader reader = new(new PbtFullKey([0]), null);
        using PbtSnapshotBundle bundle = new(
            new PbtSnapshotPooledList(0),
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), reader),
            pool,
            PbtResourcePool.Usage.MainBlockProcessing);
        bundle.SetLeaf(originalLeafKey, originalLeafValue);
        bundle.ApplyTreeMutations([], [new PbtNodeMutation(originalNodePath, originalNode)]);

        PbtFullKey updateKey = new([3]);
        PbtWriteBatch initial = new();
        initial.Set(updateKey, new ValueHash256([4]));
        using PbtNodeGroupStore validStore = new();
        ValueHash256 validRoot = TrieUpdater.UpdateRoot(validStore, default, initial);
        PbtWriteBatch wrongNodeBatch = new();
        wrongNodeBatch.Set(new PbtFullKey([7]), new ValueHash256([8]));
        using PbtNodeGroupStore wrongNodeStore = new();
        TrieUpdater.UpdateRoot(wrongNodeStore, default, wrongNodeBatch);
        reader.Node = hashMismatch ? wrongNodeStore.GetNode(new PbtNodePath([], 0)) : null;
        PbtWriteBatch changes = new();
        changes.Set(new PbtFullKey([5]), new ValueHash256([6]));

        Assert.Throws<InvalidDataException>(() => TrieUpdater.UpdateRoot(new PbtSnapshotStore(bundle), validRoot, changes));
        using PbtSnapshot snapshot = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), default);

        bool foundNode = snapshot.Content.TryGetNode(originalNodePath, out byte[]? node);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.Content.Leaves, Has.Count.EqualTo(1));
            Assert.That(snapshot.Content.TryGetLeaf(originalLeafKey, out ValueHash256? leaf) && leaf == originalLeafValue, Is.True);
            Assert.That(snapshot.Content.Nodes, Has.Count.EqualTo(1));
            Assert.That(foundNode, Is.True);
            Assert.That(node, Is.EqualTo(originalNode));
            Assert.That(snapshot.Content.TryGetLeaf(updateKey, out _), Is.False);
        }
    }

    [Test]
    public void Node_group_read_rejects_non_boundary_key_before_empty_persistence_lookup()
    {
        Reader reader = new(new PbtFullKey([0]), null);
        using PbtReadOnlySnapshotBundle bundle = new(new PbtSnapshotPooledList(0), reader);

        Assert.Throws<ArgumentException>(() => bundle.GetNodeGroup(new PbtNodePath([0], 1)));
        Assert.That(reader.GroupReadCount, Is.Zero);
    }

    [Test]
    public void Node_group_composition_reads_persistence_once_and_applies_layers_oldest_to_newest()
    {
        PbtNodePath groupKey = new([], 0);
        PbtNodePath persistedPath = PbtFourLevelGroupGeometry.PathOf(groupKey, 14);
        PbtNodePath sharedPath = PbtFourLevelGroupGeometry.PathOf(groupKey, 29);
        PbtNodePath localPath = PbtFourLevelGroupGeometry.PathOf(groupKey, 13);
        PbtNodePath writeBufferPath = PbtFourLevelGroupGeometry.PathOf(groupKey, 12);
        byte[] persisted = BranchEncoding(1);
        byte[] sharedOldest = BranchEncoding(2);
        byte[] sharedNewest = BranchEncoding(3);
        byte[] localOldest = BranchEncoding(4);
        byte[] localNewest = BranchEncoding(5);
        byte[] writeBuffer = BranchEncoding(6);
        Reader reader = new(new PbtFullKey([0]), null)
        {
            GroupKey = groupKey,
            GroupPayload = EncodeGroup(groupKey,
            [
                new PbtNodeRecord(persistedPath, persisted),
                new PbtNodeRecord(sharedPath, BranchEncoding(7)),
                new PbtNodeRecord(localPath, BranchEncoding(8)),
                new PbtNodeRecord(writeBufferPath, BranchEncoding(9)),
            ]),
        };
        PbtResourcePool pool = new(new PbtConfig());
        PbtSnapshotPooledList sharedSnapshots = Snapshots(pool,
            Content((sharedPath, sharedOldest), (localPath, sharedOldest)),
            Content((sharedPath, sharedNewest), (localPath, sharedNewest)));
        PbtSnapshotPooledList localSnapshots = Snapshots(pool,
            Content((localPath, localOldest)),
            Content((localPath, localNewest)));
        using PbtSnapshotBundle bundle = new(localSnapshots, new PbtReadOnlySnapshotBundle(sharedSnapshots, reader), pool, PbtResourcePool.Usage.MainBlockProcessing);
        bundle.ApplyTreeMutations([], [new PbtNodeMutation(writeBufferPath, writeBuffer)]);

        using PbtNodeGroupPayload payload = bundle.GetNodeGroup(groupKey)!;
        PbtNodeGroupReader group = new(groupKey, payload.Span);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.GroupReadCount, Is.EqualTo(1));
            Assert.That(group.GetNode(14).ToArray(), Is.EqualTo(persisted));
            Assert.That(group.GetNode(29).ToArray(), Is.EqualTo(sharedNewest));
            Assert.That(group.GetNode(13).ToArray(), Is.EqualTo(localNewest));
            Assert.That(group.GetNode(12).ToArray(), Is.EqualTo(writeBuffer));
        }
    }

    [Test]
    public void Node_group_tombstones_remove_positions_and_deleting_final_position_returns_null()
    {
        PbtNodePath groupKey = new([], 0);
        PbtNodePath retainedPath = PbtFourLevelGroupGeometry.PathOf(groupKey, 14);
        PbtNodePath deletedPath = PbtFourLevelGroupGeometry.PathOf(groupKey, 29);
        Reader reader = new(new PbtFullKey([0]), null)
        {
            GroupKey = groupKey,
            GroupPayload = EncodeGroup(groupKey,
            [
                new PbtNodeRecord(retainedPath, BranchEncoding(1)),
                new PbtNodeRecord(deletedPath, BranchEncoding(2)),
            ]),
        };
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), reader), pool, PbtResourcePool.Usage.MainBlockProcessing);
        bundle.ApplyTreeMutations([], [new PbtNodeMutation(deletedPath, null)]);

        using (PbtNodeGroupPayload payload = bundle.GetNodeGroup(groupKey)!)
        {
            PbtNodeGroupReader group = new(groupKey, payload.Span);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(group.TryGetNode(PbtFourLevelGroupGeometry.PositionOf(retainedPath), out _), Is.True);
                Assert.That(group.TryGetNode(PbtFourLevelGroupGeometry.PositionOf(deletedPath), out _), Is.False);
            }
        }

        bundle.ApplyTreeMutations([], [new PbtNodeMutation(retainedPath, null)]);
        Assert.That(bundle.GetNodeGroup(groupKey), Is.Null);
    }

    [Test]
    public void Malformed_persisted_group_is_rejected_and_lease_is_released_without_mutating_snapshot()
    {
        TrackingMemoryProvider memoryProvider = new();
        Reader reader = new(new PbtFullKey([0]), null)
        {
            GroupKey = new PbtNodePath([], 0),
            GroupPayload = [1],
            MemoryProvider = memoryProvider,
        };
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), reader), pool, PbtResourcePool.Usage.MainBlockProcessing);
        PbtFullKey originalLeafKey = new([1]);
        PbtNodePath originalNodePath = new([0], 1);
        byte[] originalNode = BranchEncoding(1);
        bundle.SetLeaf(originalLeafKey, new ValueHash256([2]));
        bundle.ApplyTreeMutations([], [new PbtNodeMutation(originalNodePath, originalNode)]);

        Assert.Throws<InvalidDataException>(() => bundle.GetNodeGroup(reader.GroupKey));
        AssertSnapshotUnchanged(bundle, originalLeafKey, originalNodePath, originalNode);
        Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
    }

    [Test]
    public void Malformed_non_null_delta_is_rejected_and_persisted_lease_is_released_without_mutating_snapshot()
    {
        TrackingMemoryProvider memoryProvider = new();
        PbtNodePath groupKey = new([], 0);
        PbtNodePath originalNodePath = PbtFourLevelGroupGeometry.PathOf(groupKey, 14);
        byte[] originalNode = BranchEncoding(1);
        Reader reader = new(new PbtFullKey([0]), null)
        {
            GroupKey = groupKey,
            GroupPayload = EncodeGroup(groupKey, [new PbtNodeRecord(originalNodePath, originalNode)]),
            MemoryProvider = memoryProvider,
        };
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), reader), pool, PbtResourcePool.Usage.MainBlockProcessing);
        PbtFullKey originalLeafKey = new([1]);
        bundle.SetLeaf(originalLeafKey, new ValueHash256([2]));
        bundle.ApplyTreeMutations([], [new PbtNodeMutation(originalNodePath, [0x7F])]);

        Assert.Throws<InvalidDataException>(() => bundle.GetNodeGroup(groupKey));
        AssertSnapshotUnchanged(bundle, originalLeafKey, originalNodePath, [0x7F]);
        Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
    }

    [Test]
    public void Updater_group_read_failure_preserves_prior_deltas_and_does_not_apply()
    {
        PbtResourcePool pool = new(new PbtConfig());
        PbtFullKey originalLeafKey = new([1]);
        ValueHash256 originalLeafValue = new([2]);
        PbtNodePath originalNodePath = new([0x80], 1);
        byte[] originalNode = [0x7F];
        Reader reader = new(new PbtFullKey([0]), null) { GroupReadException = new InvalidDataException("Configured group read failure.") };
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), reader), pool, PbtResourcePool.Usage.MainBlockProcessing);
        bundle.SetLeaf(originalLeafKey, originalLeafValue);
        bundle.ApplyTreeMutations([], [new PbtNodeMutation(originalNodePath, originalNode)]);
        PbtWriteBatch changes = new();
        changes.Set(new PbtFullKey([3]), new ValueHash256([4]));

        CountingStore store = new(bundle);
        Assert.Throws<InvalidDataException>(() => TrieUpdater.UpdateRoot(store, new ValueHash256([5]), changes));

        using PbtSnapshot snapshot = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), default);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.GroupReadCount, Is.EqualTo(1));
            Assert.That(store.ApplyCount, Is.Zero);
            Assert.That(snapshot.Content.Leaves, Has.Count.EqualTo(1));
            Assert.That(snapshot.Content.TryGetLeaf(originalLeafKey, out ValueHash256? leaf) && leaf == originalLeafValue, Is.True);
            Assert.That(snapshot.Content.Nodes, Has.Count.EqualTo(1));
            Assert.That(snapshot.Content.TryGetNode(originalNodePath, out byte[]? node) && node!.AsSpan().SequenceEqual(originalNode), Is.True);
        }
    }

    [Test]
    public void CollectedSnapshot_ContainsCanonicalWritesAndRoot()
    {
        PbtFullKey key = new([1]);
        ValueHash256 value = new([2]);
        ValueHash256 root = new([3]);
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(new PbtFullKey([0]), null)), pool, PbtResourcePool.Usage.MainBlockProcessing);
        bundle.SetLeaf(key, value);
        using PbtSnapshot snapshot = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), root);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.TreeRoot, Is.EqualTo(root));
            Assert.That(snapshot.Content.TryGetLeaf(key, out ValueHash256? actual) && actual == value, Is.True);
        }
    }

    private static PbtSnapshotPooledList Snapshots(PbtResourcePool pool, params PbtSnapshotContent[] contents)
    {
        PbtSnapshotPooledList snapshots = new(contents.Length);
        for (int index = 0; index < contents.Length; index++)
            snapshots.Add(new PbtSnapshot(StateId.PreGenesis, new StateId((ulong)index, default), default, contents[index], pool, PbtResourcePool.Usage.MainBlockProcessing));
        return snapshots;
    }

    private static PbtSnapshotContent Content(params (PbtNodePath Path, byte[] Encoding)[] nodes)
    {
        PbtSnapshotContent content = new();
        foreach ((PbtNodePath path, byte[] encoding) in nodes) content.SetNode(path, encoding);
        return content;
    }

    private static byte[] BranchEncoding(byte marker)
    {
        ValueHash256 left = new([marker]);
        ValueHash256 right = new([(byte)(marker + 32)]);
        return PbtNodeCodec.Encode(new PbtBranchNode(new PbtBitPrefix([], 0), left, right));
    }

    private static byte[] EncodeGroup(PbtNodePath groupKey, IReadOnlyList<PbtNodeRecord> records)
    {
        int length = PbtNodeGroupCodec.TrailerLength;
        foreach (PbtNodeRecord record in records) length += record.Encoding.Length;
        byte[] payload = new byte[length];
        BufferWriter writer = new(payload);
        PbtNodeGroupCodec.Encode(ref writer, groupKey, records);
        return writer.WrittenSpan.ToArray();
    }

    private static void AssertSnapshotUnchanged(PbtSnapshotBundle bundle, PbtFullKey leafKey, PbtNodePath nodePath, byte[] node)
    {
        using PbtSnapshot snapshot = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), default);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.Content.Leaves, Has.Count.EqualTo(1));
            Assert.That(snapshot.Content.TryGetLeaf(leafKey, out _), Is.True);
            Assert.That(snapshot.Content.Nodes, Has.Count.EqualTo(1));
            Assert.That(snapshot.Content.TryGetNode(nodePath, out byte[]? actual) && actual!.AsSpan().SequenceEqual(node), Is.True);
        }
    }

    private sealed class Reader(PbtFullKey key, ValueHash256? value) : IPbtPersistence.IReader
    {
        public byte[]? Node { get; set; }
        public PbtNodePath GroupKey { get; set; } = new([], 0);
        public byte[]? GroupPayload { get; set; }
        public IRefCountingMemoryProvider MemoryProvider { get; set; } = PooledRefCountingMemoryProvider.Instance;
        public Exception? GroupReadException { get; set; }
        public int GroupReadCount { get; private set; }
        public StateId CurrentState => StateId.PreGenesis;
        public ValueHash256 CurrentRoot => default;
        public ValueHash256? GetLeaf(PbtFullKey requested) => requested == key ? value : null;
        public IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeaves() => [];
        public IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeaves(PbtFullKey prefix) => [];
        public byte[]? GetNode(PbtNodePath path) => Node;
        public PbtNodeGroupPayload? GetNodeGroup(PbtNodePath groupKey)
        {
            GroupReadCount++;
            if (GroupReadException is not null) throw GroupReadException;
            if (!groupKey.Equals(GroupKey) || GroupPayload is null) return null;
            RefCountingMemory memory = MemoryProvider.Rent(GroupPayload.Length);
            GroupPayload.CopyTo(memory.GetSpan());
            return PbtNodeGroupPayload.FromLease(memory);
        }
        public IEnumerable<KeyValuePair<PbtNodePath, byte[]>> EnumerateNodes() => [];
        public ulong GetCodeReference(in ValueHash256 codeHash) => 0;
        public void Dispose() { }
    }

    private sealed class CountingStore(PbtSnapshotBundle bundle) : IPbtStore
    {
        private readonly List<PbtLeafMutation> _leaves = [];

        public int ApplyCount { get; private set; }
        public byte[]? GetNode(PbtNodePath path) => bundle.GetNode(path);
        public PbtNodeGroupPayload? GetNodeGroup(PbtNodePath groupKey) => bundle.GetNodeGroup(groupKey);
        public void SetLeaf(PbtFullKey key, ValueHash256? value) => _leaves.Add(new(key, value));
        public void Apply(in ValueHash256 newRoot, IReadOnlyList<PbtNodeMutation> nodes)
        {
            ApplyCount++;
            bundle.ApplyTreeMutations(_leaves, nodes);
        }
    }
}

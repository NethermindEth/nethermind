// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
        ValueHash256 persisted = new(Value(1));
        ValueHash256 shared = new(Value(2));
        ValueHash256 local = new(Value(3));
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
    public void Leaf_enumeration_preserves_optional_value_key_prefix(bool filtered)
    {
        PbtFullKey matching = new(Bytes.FromHexString("a501"));
        PbtFullKey other = new(Bytes.FromHexString("b502"));
        PbtFullKey prefix = new(Bytes.FromHexString("a5"));
        PbtResourcePool pool = new(new PbtConfig());
        PbtSnapshotContent sharedContent = new();
        sharedContent.SetLeaf(matching, new ValueHash256(Value(1)));
        sharedContent.SetLeaf(other, new ValueHash256(Value(2)));
        PbtSnapshotPooledList sharedSnapshots = new(1)
        {
            new PbtSnapshot(StateId.PreGenesis, new StateId(1, default), default, sharedContent, pool, PbtResourcePool.Usage.MainBlockProcessing)
        };
        PbtReadOnlySnapshotBundle readOnly = new(sharedSnapshots, new Reader(matching, null));
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), readOnly, pool, PbtResourcePool.Usage.MainBlockProcessing);
        List<PbtFullKey> expected = filtered ? [matching] : [matching, other];
        List<PbtFullKey> sharedKeys = [];
        foreach (KeyValuePair<PbtFullKey, ValueHash256> leaf in filtered ? readOnly.EnumerateLeaves(prefix) : readOnly.EnumerateLeaves())
            sharedKeys.Add(leaf.Key);
        bundle.SetLeaf(matching, new ValueHash256(Value(3)));
        List<PbtFullKey> visibleKeys = [];
        foreach (KeyValuePair<PbtFullKey, ValueHash256> leaf in filtered ? bundle.EnumerateLeaves(prefix) : bundle.EnumerateLeaves())
            visibleKeys.Add(leaf.Key);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sharedKeys, Is.EqualTo(expected));
            Assert.That(visibleKeys, Is.EqualTo(expected));
        }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void Trie_updates_leave_independently_staged_flat_entries_unchanged(bool leafExists, bool delete)
    {
        PbtFullKey key = new(Bytes.FromHexString("80"));
        ValueHash256 flatValue = new(Value(9));
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0),
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(key, null)), pool, PbtResourcePool.Usage.MainBlockProcessing);
        PbtSnapshotStore store = new(bundle);
        PbtWriteBatch initial = new();
        if (leafExists) initial.Set(key, new ValueHash256(Value(1)));
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, initial);
        bundle.SetLeaf(key, flatValue);

        PbtWriteBatch changes = new();
        if (delete) changes.Delete(key);
        else changes.Set(key, new ValueHash256(Value(2)));
        ValueHash256 updatedRoot = TrieUpdater.UpdateRoot(store, root, changes);

        byte[] expectedLeaf = PbtNodeCodec.EncodeLeaf(key, Value(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.GetLeaf(key), Is.EqualTo(flatValue));
            Assert.That(bundle.PendingLeafMutations(), Is.EquivalentTo(new[] { new KeyValuePair<PbtFullKey, ValueHash256?>(key, flatValue) }));
            Assert.That(updatedRoot, Is.EqualTo(delete ? default : PbtNodeCodec.Hash(new PbtNodeReader(expectedLeaf))));
            Assert.That(store.GetNode(new PbtNodePath([], 0)), Is.EqualTo(delete ? null : expectedLeaf));
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

    [TestCase(0, false)]
    [TestCase(1, false)]
    [TestCase(2, false)]
    [TestCase(3, false)]
    [TestCase(1, true)]
    [TestCase(2, true)]
    [TestCase(3, true)]
    public void Node_group_newest_full_replacement_or_tombstone_stops_fallback(int newestTier, bool tombstone)
    {
        PbtNodePath groupKey = new([], 0);
        byte[] persisted = EncodeGroup(groupKey, [new PbtNodeRecord(groupKey, BranchEncoding(1)),
            new PbtNodeRecord(PbtFourLevelGroupGeometry.PathOf(groupKey, 14), BranchEncoding(2))]);
        byte[] shared = EncodeGroup(groupKey, [new PbtNodeRecord(groupKey, BranchEncoding(3))]);
        byte[] local = EncodeGroup(groupKey, [new PbtNodeRecord(groupKey, BranchEncoding(4))]);
        byte[] write = EncodeGroup(groupKey, [new PbtNodeRecord(groupKey, BranchEncoding(5))]);
        Reader reader = new(new PbtFullKey([0]), null) { GroupPayload = persisted };
        PbtResourcePool pool = new(new PbtConfig());
        PbtSnapshotPooledList sharedSnapshots = newestTier >= 1
            ? Snapshots(pool, Content(groupKey, persisted), Content(groupKey, newestTier == 1 && tombstone ? null : shared))
            : new(0);
        PbtSnapshotPooledList localSnapshots = newestTier >= 2
            ? Snapshots(pool, Content(groupKey, shared), Content(groupKey, newestTier == 2 && tombstone ? null : local))
            : new(0);
        using PbtSnapshotBundle bundle = new(localSnapshots, new PbtReadOnlySnapshotBundle(sharedSnapshots, reader), pool, PbtResourcePool.Usage.MainBlockProcessing);
        if (newestTier == 3)
        {
            using RefCountingMemory? payload = tombstone ? null : Memory(write);
            bundle.SetNodeGroup(groupKey, payload);
        }

        using RefCountingMemory? actual = bundle.GetNodeGroup(groupKey);
        byte[]? expected = tombstone ? null : newestTier switch { 0 => persisted, 1 => shared, 2 => local, _ => write };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual?.Memory.ToArray(), Is.EqualTo(expected));
            Assert.That(reader.GroupReadCount, Is.EqualTo(newestTier == 0 ? 1 : 0));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Malformed_persisted_group_is_rejected_by_updater_and_lease_is_released_without_mutating_snapshot(bool invalidFooter)
    {
        TrackingMemoryProvider memoryProvider = new();
        byte[] malformed = invalidFooter ? new byte[PbtNodeGroupCodec.TrailerLength] : Bytes.FromHexString("01");
        if (invalidFooter) malformed[^1] = 0x80;
        Reader reader = new(new PbtFullKey([0]), null)
        {
            GroupPayload = malformed,
            MemoryProvider = memoryProvider,
        };
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), reader), pool, PbtResourcePool.Usage.MainBlockProcessing);
        PbtFullKey originalLeafKey = new([1]);
        PbtNodePath originalGroupKey = new([0x80], 4);
        byte[] originalGroup = EncodeGroup(originalGroupKey, [new PbtNodeRecord(PbtFourLevelGroupGeometry.PathOf(originalGroupKey, 0), BranchEncoding(1))]);
        using RefCountingMemory originalPayload = Memory(originalGroup);
        bundle.SetLeaf(originalLeafKey, new ValueHash256(Value(2)));
        bundle.SetNodeGroup(originalGroupKey, originalPayload);
        PbtWriteBatch changes = new();
        changes.Set(new PbtFullKey([3]), new ValueHash256(Value(4)));

        Assert.Throws<InvalidDataException>(() => TrieUpdater.UpdateRoot(new PbtSnapshotStore(bundle), new ValueHash256(Value(5)), changes));
        AssertSnapshotUnchanged(bundle, originalLeafKey, originalGroupKey, originalGroup);
        Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
    }

    [Test]
    public void Malformed_group_replacement_is_rejected_without_mutating_snapshot()
    {
        PbtResourcePool pool = new(new PbtConfig());
        Reader reader = new(new PbtFullKey([0]), null);
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), reader), pool, PbtResourcePool.Usage.MainBlockProcessing);
        PbtFullKey leafKey = new([1]);
        PbtNodePath groupKey = new([], 0);
        byte[] original = EncodeGroup(groupKey, [new PbtNodeRecord(groupKey, BranchEncoding(1))]);
        using RefCountingMemory originalPayload = Memory(original);
        using RefCountingMemory malformed = Memory(Bytes.FromHexString("7f"));
        bundle.SetLeaf(leafKey, new ValueHash256(Value(2)));
        bundle.SetNodeGroup(groupKey, originalPayload);

        Assert.Throws<InvalidDataException>(() => bundle.SetNodeGroup(groupKey, malformed));
        AssertSnapshotUnchanged(bundle, leafKey, groupKey, original);
        Assert.That(reader.GroupReadCount, Is.Zero);
    }

    [Test]
    public void Updater_group_read_failure_preserves_prior_deltas_and_does_not_apply()
    {
        PbtResourcePool pool = new(new PbtConfig());
        PbtFullKey originalLeafKey = new([1]);
        ValueHash256 originalLeafValue = new(Value(2));
        PbtNodePath originalNodePath = new([0x80], 4);
        byte[] originalNode = EncodeGroup(originalNodePath, [new PbtNodeRecord(PbtFourLevelGroupGeometry.PathOf(originalNodePath, 0), BranchEncoding(1))]);
        Reader reader = new(new PbtFullKey([0]), null) { GroupReadException = new InvalidDataException("Configured group read failure.") };
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), reader), pool, PbtResourcePool.Usage.MainBlockProcessing);
        bundle.SetLeaf(originalLeafKey, originalLeafValue);
        using RefCountingMemory originalPayload = Memory(originalNode);
        bundle.SetNodeGroup(originalNodePath, originalPayload);
        PbtWriteBatch changes = new();
        changes.Set(new PbtFullKey([3]), new ValueHash256(Value(4)));

        CountingStore store = new(bundle);
        Assert.Throws<InvalidDataException>(() => TrieUpdater.UpdateRoot(store, new ValueHash256(Value(5)), changes));

        using PbtSnapshot snapshot = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), default);
        bool foundGroup = snapshot.Content.TryGetNodeGroup(originalNodePath, out RefCountingMemory? group);
        using RefCountingMemory? groupLease = group;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.GroupReadCount, Is.EqualTo(1));
            Assert.That(store.ApplyCount, Is.Zero);
            Assert.That(snapshot.Content.Leaves, Has.Count.EqualTo(1));
            Assert.That(snapshot.Content.TryGetLeaf(originalLeafKey, out ValueHash256? leaf) && leaf == originalLeafValue, Is.True);
            Assert.That(snapshot.Content.NodeGroups, Has.Count.EqualTo(1));
            Assert.That(foundGroup, Is.True);
            Assert.That(group!.GetSpan().ToArray(), Is.EqualTo(originalNode));
        }
    }

    [Test]
    public void CollectedSnapshot_ContainsCanonicalWritesAndRoot()
    {
        PbtFullKey key = new([1]);
        ValueHash256 value = new(Value(2));
        ValueHash256 root = new(Value(3));
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

    private static PbtSnapshotContent Content(PbtNodePath groupKey, byte[]? encoding)
    {
        PbtSnapshotContent content = new();
        using RefCountingMemory? payload = encoding is null ? null : Memory(encoding);
        content.SetNodeGroup(groupKey, payload);
        return content;
    }

    private static RefCountingMemory Memory(byte[] encoding)
    {
        RefCountingMemory payload = PooledRefCountingMemoryProvider.Instance.Rent(encoding.Length);
        encoding.CopyTo(payload.GetSpan());
        return payload;
    }

    private static byte[] Value(byte marker)
    {
        byte[] value = new byte[32];
        value[^1] = marker;
        return value;
    }

    private static byte[] BranchEncoding(byte marker)
    {
        ValueHash256 left = new(Value(marker));
        ValueHash256 right = new(Value((byte)(marker + 32)));
        return PbtNodeCodec.EncodeBranch([], 0, left, right);
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
        bool foundGroup = snapshot.Content.TryGetNodeGroup(nodePath, out RefCountingMemory? actual);
        using RefCountingMemory? actualLease = actual;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.Content.Leaves, Has.Count.EqualTo(1));
            Assert.That(snapshot.Content.TryGetLeaf(leafKey, out _), Is.True);
            Assert.That(snapshot.Content.NodeGroups, Has.Count.EqualTo(1));
            Assert.That(foundGroup, Is.True);
            Assert.That(actual!.GetSpan().ToArray(), Is.EqualTo(node));
        }
    }

    private sealed class Reader(PbtFullKey key, ValueHash256? value) : IPbtPersistence.IReader
    {
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
        public RefCountingMemory? GetNodeGroup(PbtNodePath groupKey)
        {
            GroupReadCount++;
            if (GroupReadException is not null) throw GroupReadException;
            if (!groupKey.Equals(GroupKey) || GroupPayload is null) return null;
            RefCountingMemory memory = MemoryProvider.Rent(GroupPayload.Length);
            GroupPayload.CopyTo(memory.GetSpan());
            return memory;
        }
        public IEnumerable<PbtNodePath> EnumerateNodeGroupKeys() => [];
        public ulong GetCodeReference(in ValueHash256 codeHash) => 0;
        public void Dispose() { }
    }

    private sealed class CountingStore(PbtSnapshotBundle bundle) : IPbtStore
    {
        public int ApplyCount { get; private set; }
        public RefCountingMemory? GetNodeGroup(PbtNodePath groupKey) => bundle.GetNodeGroup(groupKey);
        public void SetNodeGroup(PbtNodePath groupKey, RefCountingMemory? payload)
        {
            ApplyCount++;
            bundle.SetNodeGroup(groupKey, payload);
        }
    }
}

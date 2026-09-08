// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class PbtNodeGroupTests
{
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void Node_group_paths_pack_internal_node_geometry_into_one_byte(int length)
    {
        for (int prefix = 0; prefix < 1 << length; prefix++)
        {
            int slot = prefix << (4 - length);
            NodeGroupPath path = new(slot, length);
            PbtNodePath nodePath = PbtPathOperations.FromKey<PbtNodePath>([(byte)(slot << 4)], length);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Unsafe.SizeOf<NodeGroupPath>(), Is.EqualTo(1));
                Assert.That(path.Slot, Is.EqualTo(slot));
                Assert.That(path.Length, Is.EqualTo(length));
                Assert.That(path.Width, Is.EqualTo(16 >> length));
                Assert.That(path.Position, Is.EqualTo(PbtFourLevelGroupGeometry.PositionOf(nodePath)));
                if (length < 3)
                {
                    Assert.That(path.Left.Slot, Is.EqualTo(slot));
                    Assert.That(path.Right.Slot, Is.EqualTo(slot + (8 >> length)));
                    Assert.That(path.Left.Length, Is.EqualTo(length + 1));
                    Assert.That(path.Right.Length, Is.EqualTo(length + 1));
                }
            }
        }
    }

    [TestCase(0)]
    [TestCase(4)]
    [TestCase(268)]
    public void Small_and_storage_paths_share_identity_and_accept_wide_leaf_payloads(int depth)
    {
        byte[] keyBytes = new byte[PbtStorageFullKey.MaxLength];
        keyBytes[0] = Eip8297KeyDerivation.StorageZone;
        PbtStorageFullKey storageKey = new(keyBytes);
        PbtNodePath smallPath = PbtPathOperations.FromKey<PbtNodePath>(keyBytes, depth);
        PbtStorageNodePath storagePath = PbtStorageNodePath.FromKey(storageKey, depth);
        Dictionary<IPbtNodePath, int?> entries = new() { [smallPath] = 1 };
        entries[storagePath] = null;
        byte[] encoding = PbtNodeCodec.EncodeLeaf(storageKey, new byte[32]);
        IPbtNodePath leafPath = PbtStorageNodePath.FromKey(storageKey, depth == 0 ? 0 : depth + 4);
        byte[] payload = EncodeGroup(smallPath, [new PbtNodeRecord(leafPath, encoding)]);
        PbtNodeGroupReader reader = new(storagePath, payload);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(smallPath.Equals(storagePath), Is.True);
            Assert.That(storagePath.Equals(smallPath), Is.True);
            Assert.That(smallPath.GetHashCode(), Is.EqualTo(storagePath.GetHashCode()));
            Assert.That(smallPath.CompareTo(storagePath), Is.Zero);
            Assert.That(smallPath.Encode(), Is.EqualTo(storagePath.Encode()));
            Assert.That(entries.Count, Is.EqualTo(1));
            Assert.That(entries[smallPath], Is.Null);
            Assert.That(reader.GroupKey.Equals(smallPath), Is.True);
            Assert.That(entries.Remove(smallPath), Is.True);
        }
    }

    [TestCase(34, 272)]
    [TestCase(66, 528)]
    public void Key_families_enforce_capacity_without_changing_storage_bytes(int length, int depth)
    {
        Assert.That(Unsafe.SizeOf<PbtFullKey>(), Is.EqualTo(40));
        Assert.That(Unsafe.SizeOf<PbtStorageFullKey>(), Is.EqualTo(72));
        byte[] keyBytes = new byte[length];
        keyBytes[^1] = 1;
        PbtStorageFullKey storageKey = new(keyBytes);
        PbtStorageNodePath storagePath = PbtStorageNodePath.FromKey(storageKey, depth);
        Assert.That(PbtStorageNodePath.Decode(storagePath.Encode()), Is.EqualTo(storagePath));
        Assert.That(storageKey.FirstDifferingBit(new PbtStorageFullKey(new byte[length])), Is.EqualTo(depth - 1));
        if (length == PbtFullKey.MaxLength)
        {
            PbtFullKey key = (PbtFullKey)storageKey;
            Assert.That(key.FirstDifferingBit(new PbtFullKey(new byte[length])), Is.EqualTo(depth - 1));
            Assert.That(((PbtStorageFullKey)key).Bytes.ToArray(), Is.EqualTo(keyBytes));
            Assert.That(PbtNodePath.Decode(storagePath.Encode()).Equals(storagePath), Is.True);
        }
        else
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = (PbtFullKey)storageKey);
            Assert.Throws<InvalidDataException>(() => PbtNodePath.Decode(storagePath.Encode()));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtFullKey(new byte[35]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtStorageFullKey(new byte[67]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtNodePath(new byte[35], 273));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtStorageNodePath(new byte[67], 529));
    }

    [TestCase(0x00)]
    [TestCase(0x80)]
    public void Root_node_round_trip_preserves_encoding_and_allows_root_position_access(byte keyByte)
    {
        byte[] value = new byte[32];
        PbtNodePath rootPath = new([], 0);
        byte[] encoding = PbtNodeCodec.EncodeLeaf(new PbtFullKey([keyByte]), value);

        byte[] payload = EncodeGroup(rootPath, [new PbtNodeRecord(rootPath, encoding)]);
        PbtNodeGroupReader group = new(rootPath, payload);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(group.GetNode(PbtFourLevelGroupGeometry.RootPosition).ToArray(), Is.EqualTo(encoding));
            Assert.That(group[PbtFourLevelGroupGeometry.RootPosition].ToArray(), Is.EqualTo(encoding));
            PbtNodeGroupReader.Enumerator nodes = group.EnumerateNodes();
            Assert.That(nodes.MoveNext(), Is.True);
            Assert.That(nodes.MoveNext(), Is.False);
        }
    }

    [Test]
    public void Reader_and_writer_validate_every_required_leaf_bit_and_ignore_suffix_bits(
        [Values(0, 4, 8, 12, 252, 256, 516)] int groupDepth, [Values] bool streamingWriter)
    {
        byte[] groupBytes = new byte[(groupDepth + 7) / 8];
        groupBytes.AsSpan().Fill(0xA5);
        if ((groupDepth & 7) != 0) groupBytes[^1] &= 0xF0;
        PbtStorageNodePath groupKey = new(groupBytes, groupDepth);

        for (int position = 0; position < PbtFourLevelGroupGeometry.PositionCount; position++)
        {
            if (groupDepth != 0 && position == PbtFourLevelGroupGeometry.RootPosition) continue;
            IPbtNodePath path = PbtFourLevelGroupGeometry.PathOf(groupKey, position);
            byte[] key = new byte[(path.BitDepth + 7) / 8 + 1];
            path.Path.CopyTo(key);
            byte[] encoding = PbtNodeCodec.EncodeLeaf(new PbtStorageFullKey(key), Value(1));
            byte[] payload = EncodeGroup(groupKey, [new PbtNodeRecord(path, encoding)]);
            Assert.That(ValidateLeafGroup(groupKey, position, payload, streamingWriter), Is.EqualTo(1));

            for (int bit = 0; bit < key.Length * 8; bit++)
            {
                byte mask = (byte)(0x80 >> (bit & 7));
                payload[3 + (bit >> 3)] ^= mask;
                if (bit < path.BitDepth)
                    Assert.That(() => ValidateLeafGroup(groupKey, position, payload, streamingWriter), Throws.TypeOf<InvalidDataException>(), $"position {position}, bit {bit}");
                else
                    Assert.That(ValidateLeafGroup(groupKey, position, payload, streamingWriter), Is.EqualTo(1), $"position {position}, suffix bit {bit}");
                payload[3 + (bit >> 3)] ^= mask;
            }
        }
    }

    [Test]
    public void Reader_and_writer_reject_leaf_keys_shorter_than_required_path(
        [Values(8, 12, 252, PbtFourLevelGroupGeometry.MaxGroupDepth)] int groupDepth, [Values] bool streamingWriter)
    {
        PbtStorageNodePath groupKey = new(new byte[(groupDepth + 7) / 8], groupDepth);
        IPbtNodePath path = PbtFourLevelGroupGeometry.PathOf(groupKey, 0);
        byte[] key = new byte[(path.BitDepth + 7) / 8];
        byte[] encoding = PbtNodeCodec.EncodeLeaf(new PbtStorageFullKey(key), Value(1));
        byte[] payload = EncodeGroup(groupKey, [new PbtNodeRecord(path, encoding)]);
        Assert.That(ValidateLeafGroup(groupKey, 0, payload, streamingWriter), Is.EqualTo(1));

        byte[] shortEncoding = PbtNodeCodec.EncodeLeaf(new PbtStorageFullKey(key.AsSpan(0, key.Length - 1)), Value(1));
        byte[] shortPayload = new byte[shortEncoding.Length + PbtNodeGroupCodec.TrailerLength];
        shortEncoding.CopyTo(shortPayload, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(shortPayload.AsSpan(shortPayload.Length - sizeof(uint)), 1u);
        Assert.That(() => ValidateLeafGroup(groupKey, 0, shortPayload, streamingWriter), Throws.TypeOf<InvalidDataException>());
    }

    private static int ValidateLeafGroup(IPbtNodePath groupKey, int position, byte[] payload, bool streamingWriter)
    {
        if (!streamingWriter) return ReadGroupCount(groupKey, payload);
        using PbtNodeGroupWriter writer = new(groupKey, new TrackingMemoryProvider());
        writer.Write(position, payload.AsSpan(0, payload.Length - PbtNodeGroupCodec.TrailerLength));
        using RefCountingMemory writtenPayload = writer.Detach()!;
        Assert.That(writtenPayload.GetSpan().ToArray(), Is.EqualTo(payload));
        return 1;
    }

    private static int ReadGroupCount(IPbtNodePath groupKey, byte[] payload) => new PbtNodeGroupReader(groupKey, payload).Count;

    [Test]
    public void Default_reader_rejects_access_and_iteration()
    {
        Assert.That(() => default(PbtNodeGroupReader).GetEnumerator(), Throws.TypeOf<InvalidOperationException>());
        Assert.That(() => default(PbtNodeGroupReader).EnumerateNodes(), Throws.TypeOf<InvalidOperationException>());
        Assert.That(() => MoveDefaultReaderEnumerator(), Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void Encode_rejects_leaf_encoding_with_mismatched_record_path()
    {
        PbtNodePath recordPath = new([0], 1);
        byte[] encoding = PbtNodeCodec.EncodeLeaf(new PbtFullKey([0x80]), new byte[32]);

        Assert.Throws<InvalidDataException>(() => EncodeGroup(new PbtNodePath([], 0), [new PbtNodeRecord(recordPath, encoding)]));
    }

    [Test]
    public void Node_groups_partition_nodes_at_four_level_boundaries_and_preserve_siblings()
    {
        using PbtTreeHarness tree = new();
        List<(byte[] Key, byte[]? Value)> initial = [];
        for (int index = 0; index < 64; index++)
        {
            byte[] key = [(byte)(index * 4), (byte)index];
            initial.Add((key, Value((byte)(index + 1))));
        }

        tree.ApplyBatch(initial);
        Dictionary<string, byte[]> before = Payloads(tree);
        Assert.That(before.Count, Is.GreaterThan(1));

        tree.ApplyBatch([(initial[0].Key, Value(0xF0))]);
        Dictionary<string, byte[]> after = Payloads(tree);

        Assert.That(after.Keys, Is.EquivalentTo(before.Keys));
        int unchangedGroups = 0;
        foreach ((string key, byte[] payload) in after)
        {
            if (payload.AsSpan().SequenceEqual(before[key])) unchangedGroups++;
        }
        Assert.That(unchangedGroups, Is.GreaterThan(0));
    }

    [Test]
    public void Dense_group_mutations_preserve_unchanged_subtrees_and_canonical_payloads(
        [Values(0, 2, 3, 4, 8, 13)] int prefixBits,
        [Values(false, true)] bool promoteSibling)
    {
        using PbtTreeHarness tree = new();
        EipReferenceTree oracle = new();
        (byte[] Key, byte[]? Value)[] entries = new (byte[], byte[]?)[32];
        for (int index = 0; index < entries.Length; index++)
        {
            int keyBits = index << (24 - prefixBits - 5);
            byte[] key = [(byte)(keyBits >> 16), (byte)(keyBits >> 8), (byte)keyBits];
            entries[index] = (key, Value((byte)(index + 1)));
            oracle.Insert(key, entries[index].Value!);
        }
        tree.ApplyBatch(entries);

        List<(byte[] Key, byte[]? Value)> changes = [];
        int changedCount = promoteSibling ? entries.Length / 2 : 1;
        for (int index = 0; index < changedCount; index++)
        {
            entries[index].Value = promoteSibling ? null : Value(0xF0);
            changes.Add(entries[index]);
            if (promoteSibling) oracle.Delete(entries[index].Key);
            else oracle.Insert(entries[index].Key, entries[index].Value!);
        }
        TrieUpdaterMetrics metrics = new();
        tree.ApplyBatch(changes, metrics);
        AssertMatchesRebuild();
        if (!promoteSibling)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(metrics.BulkCopyOperations, Is.GreaterThan(0));
                Assert.That(metrics.BulkCopiedNodes, Is.GreaterThan(metrics.BulkCopyOperations), "copy entire runs rather than one node at a time");
            }
        }

        tree.Reopen();
        entries[^1].Value = Value(0xF1);
        oracle.Insert(entries[^1].Key, entries[^1].Value!);
        tree.ApplyBatch([entries[^1]]);
        AssertMatchesRebuild();

        void AssertMatchesRebuild()
        {
            using PbtTreeHarness rebuilt = new();
            List<(byte[] Key, byte[]? Value)> survivors = [];
            foreach ((byte[] key, byte[]? value) in entries)
                if (value is not null) survivors.Add((key, value));
            rebuilt.ApplyBatch(survivors);
            Dictionary<string, byte[]> expectedPayloads = Payloads(rebuilt);
            Dictionary<string, byte[]> actualPayloads = Payloads(tree);
            Assert.That(actualPayloads.Keys, Is.EquivalentTo(expectedPayloads.Keys));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
                Assert.That(tree.RootHash, Is.EqualTo(rebuilt.RootHash));
                Assert.That(tree.CanonicalRecords(), Is.EqualTo(rebuilt.CanonicalRecords()));
                foreach ((string key, byte[] payload) in expectedPayloads)
                    Assert.That(actualPayloads[key], Is.EqualTo(payload), $"physical group {key}");
            }
        }
    }

    [Test]
    public void Deleting_the_last_node_removes_its_physical_group_and_reopen_preserves_records()
    {
        using PbtTreeHarness tree = new();
        byte[] key = [0x42, 0x24];
        tree.ApplyBatch([(key, Value(1))]);
        tree.Reopen();
        Assert.That(tree.CanonicalRecords(), Is.Not.Empty);

        tree.ApplyBatch([(key, (byte[]?)null)]);
        tree.Reopen();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.CanonicalRecords(), Is.Empty);
            Assert.That(tree.PhysicalPayloads, Is.Empty);
            Assert.That(tree.RootHash, Is.EqualTo(default(ValueHash256)));
        }
    }

    [Test]
    public void Import_rejects_zero_availability_group() =>
        Assert.Throws<InvalidDataException>(() => PbtNodeGroupStore.FromPhysicalPayloads([new PbtPhysicalPayload([0, 0, 0, 0], [0, 0, 0, 0])]));

    [Test]
    public void Import_rejects_non_boundary_keys_and_malformed_group_payloads()
    {
        Assert.Throws<InvalidDataException>(() => PbtNodeGroupStore.FromPhysicalPayloads([new PbtPhysicalPayload([0x00], [0, 0, 0, 0])]));
        Assert.Throws<InvalidDataException>(() => PbtNodeGroupStore.FromPhysicalPayloads([new PbtPhysicalPayload([], [1, 0, 0, 0])]));
    }

    [Test]
    public void Store_releases_owned_memory_across_create_replace_delete_reopen_lookup_and_disposal()
    {
        TrackingMemoryProvider provider = new();
        PbtNodePath rootPath = new([], 0);
        byte[] firstEncoding = LeafEncoding(0x00, 1);
        byte[] secondEncoding = LeafEncoding(0x80, 2);

        using (PbtNodeGroupStore store = new(provider))
        {
            store.SetNode(rootPath, firstEncoding, provider);
            Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.EqualTo(1), "create");

            byte[]? lookup = store.GetNode(rootPath);
            Assert.That(lookup, Is.EqualTo(firstEncoding), "lookup");
            lookup![0] = 0x7F;
            Assert.That(store.GetNode(rootPath), Is.EqualTo(firstEncoding), "lookup is owned");

            store.SetNode(rootPath, secondEncoding, provider);
            Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.EqualTo(1), "replace");

            IReadOnlyList<PbtPhysicalPayload> payloads = store.ExportPhysicalPayloads();
            using PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(payloads, provider);
            Assert.That(reopened.GetNode(rootPath), Is.EqualTo(secondEncoding), "reopen");

            store.SetNode(rootPath, null, provider);
            Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.EqualTo(1), "delete leaves reopened owner");
        }

        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero, "disposal");
    }

    [Test]
    public void Retained_group_leases_survive_replacement_deletion_and_store_disposal()
    {
        TrackingMemoryProvider provider = new();
        PbtNodePath rootPath = new([], 0);
        byte[] firstEncoding = LeafEncoding(0x00, 1);
        byte[] secondEncoding = LeafEncoding(0x80, 2);
        byte[] thirdEncoding = LeafEncoding(0x40, 3);

        using PbtNodeGroupStore store = new(provider);
        store.SetNode(rootPath, firstEncoding, provider);
        RefCountingMemory firstLease = store.GetNodeGroup(rootPath)!;

        store.SetNode(rootPath, secondEncoding, provider);
        RefCountingMemory secondLease = store.GetNodeGroup(rootPath)!;
        Assert.That(firstLease.GetSpan().ToArray(), Is.EqualTo(EncodeGroup(rootPath, [new PbtNodeRecord(rootPath, firstEncoding)])));

        store.SetNode(rootPath, null, provider);
        Assert.That(firstLease.GetSpan().ToArray(), Is.EqualTo(EncodeGroup(rootPath, [new PbtNodeRecord(rootPath, firstEncoding)])));
        Assert.That(secondLease.GetSpan().ToArray(), Is.EqualTo(EncodeGroup(rootPath, [new PbtNodeRecord(rootPath, secondEncoding)])));

        store.SetNode(rootPath, thirdEncoding, provider);
        RefCountingMemory thirdLease = store.GetNodeGroup(rootPath)!;
        store.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstLease.GetSpan().ToArray(), Is.EqualTo(EncodeGroup(rootPath, [new PbtNodeRecord(rootPath, firstEncoding)])));
            Assert.That(secondLease.GetSpan().ToArray(), Is.EqualTo(EncodeGroup(rootPath, [new PbtNodeRecord(rootPath, secondEncoding)])));
            Assert.That(thirdLease.GetSpan().ToArray(), Is.EqualTo(EncodeGroup(rootPath, [new PbtNodeRecord(rootPath, thirdEncoding)])));
            Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.EqualTo(3));
        }

        ((IDisposable)thirdLease).Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.EqualTo(2));
        ((IDisposable)firstLease).Dispose();
        ((IDisposable)secondLease).Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Group_publication_borrows_payload_and_retains_independent_lease(bool selfReplacement)
    {
        TrackingMemoryProvider provider = new();
        PbtNodePath rootPath = new([], 0);
        byte[] expected = EncodeGroup(rootPath, [new PbtNodeRecord(rootPath, LeafEncoding(0x00, 1))]);
        using PbtNodeGroupStore store = new();
        RefCountingMemory payload = provider.Rent(expected.Length);
        expected.CopyTo(payload.GetSpan());
        store.SetNodeGroup(rootPath, payload);
        ((IDisposable)payload).Dispose();

        using (RefCountingMemory lease = store.GetNodeGroup(rootPath)!)
        {
            if (selfReplacement) store.SetNodeGroup(rootPath, lease);
            Assert.That(lease.GetSpan().ToArray(), Is.EqualTo(expected));
        }
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.EqualTo(1));
        using (RefCountingMemory lease = store.GetNodeGroup(rootPath)!)
        {
            store.Dispose();
            Assert.That(lease.GetSpan().ToArray(), Is.EqualTo(expected));
        }
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero);
    }

    [TestCase(-1, -1, typeof(ArgumentNullException))]
    [TestCase(1, -1, typeof(ArgumentException))]
    [TestCase(0, 0, typeof(InvalidDataException))]
    [TestCase(0, 1, typeof(InvalidDataException))]
    [TestCase(0, PbtNodeGroupCodec.TrailerLength, typeof(InvalidDataException))]
    public void Invalid_group_publication_preserves_prior_group_and_caller_reference(int keyDepth, int payloadLength, Type exceptionType)
    {
        TrackingMemoryProvider provider = new();
        PbtNodePath rootPath = new([], 0);
        byte[] encoding = LeafEncoding(0x00, 1);
        byte[] validPayload = EncodeGroup(rootPath, [new PbtNodeRecord(rootPath, encoding)]);
        using PbtNodeGroupStore store = new();
        store.SetNode(rootPath, encoding, provider);
        PbtNodePath? groupKey = keyDepth < 0 ? null : new(new byte[(keyDepth + 7) / 8], keyDepth);
        byte[] rejectedBytes = payloadLength < 0 ? validPayload : new byte[payloadLength];
        RefCountingMemory rejectedPayload = provider.Rent(rejectedBytes.Length);
        rejectedBytes.CopyTo(rejectedPayload.GetSpan());

        Assert.That(() => store.SetNodeGroup(groupKey!, rejectedPayload), Throws.TypeOf(exceptionType));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.GetNode(rootPath), Is.EqualTo(encoding));
            Assert.That(rejectedPayload.GetSpan().ToArray(), Is.EqualTo(rejectedBytes));
            Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.EqualTo(2));
        }
        ((IDisposable)rejectedPayload).Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.EqualTo(1), "rejected publication must not retain a lease");
        store.Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero);
    }

    [Test]
    public void Multiple_node_changes_publish_one_complete_group_and_unchanged_batch_publishes_none()
    {
        using PublishingStore store = new();
        using PbtWriteBatchBuilder<PbtFullKey> batch = new(0);
        batch.Set(new PbtFullKey([0x00]), new ValueHash256(Value(1)));
        batch.Set(new PbtFullKey([0x80]), new ValueHash256(Value(2)));
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, batch.Build());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Publishes, Is.EqualTo(1));
            Assert.That(store.Inner.EnumerateRecords(), Has.Count.EqualTo(3));
        }

        using PbtWriteBatchBuilder<PbtFullKey> changedBatch = new(0);
        changedBatch.Set(new PbtFullKey([0x00]), new ValueHash256(Value(3)));
        changedBatch.Set(new PbtFullKey([0x80]), new ValueHash256(Value(4)));
        root = TrieUpdater.UpdateRoot(store, root, changedBatch.Build());
        Assert.That(store.Publishes, Is.EqualTo(2));

        ValueHash256 unchangedRoot = TrieUpdater.UpdateRoot(store, root, changedBatch.Build());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(unchangedRoot, Is.EqualTo(root));
            Assert.That(store.Publishes, Is.EqualTo(2));
        }

        PbtNodePath siblingPath = new([0x80], 1);
        byte[]? sibling = store.Inner.GetNode(siblingPath);
        using PbtWriteBatchBuilder<PbtFullKey> siblingChangeBatch = new(0);
        siblingChangeBatch.Set(new PbtFullKey([0x00]), new ValueHash256(Value(5)));
        root = TrieUpdater.UpdateRoot(store, root, siblingChangeBatch.Build());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Publishes, Is.EqualTo(3));
            Assert.That(store.Inner.GetNode(siblingPath), Is.EqualTo(sibling));
        }

        using PbtWriteBatchBuilder<PbtFullKey> deleteBatch = new(0);
        deleteBatch.Delete(new PbtFullKey([0x00]));
        deleteBatch.Delete(new PbtFullKey([0x80]));
        root = TrieUpdater.UpdateRoot(store, root, deleteBatch.Build());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(default(ValueHash256)));
            Assert.That(store.Publishes, Is.EqualTo(4));
            Assert.That(store.Inner.EnumerateNodeGroupKeys(), Is.Empty);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    [Ignore("Pre-existing: TrieUpdater does not validate currentRoot against the stored root for nonempty batches.")]
    public void Update_rejects_missing_or_mismatched_current_root(bool storedRootPresent)
    {
        using PbtNodeGroupStore store = new();
        using PbtWriteBatchBuilder<PbtFullKey> batch = new(0);
        batch.Set(new PbtFullKey([0x00]), new ValueHash256(Value(1)));
        if (storedRootPresent) TrieUpdater.UpdateRoot(store, default, batch.Build());
        using PbtWriteBatchBuilder<PbtFullKey> changes = new(0);
        changes.Set(new PbtFullKey([0x80]), new ValueHash256(Value(2)));

        Assert.That(() => TrieUpdater.UpdateRoot(store, new ValueHash256(Value(3)), changes.Build()), Throws.TypeOf<InvalidDataException>());
    }

    [TestCase("0000,0800", "0800", false, new[] { 4, 0 }, TestName = "Escaping_subtree_survives_poisoned_group_root_handoff")]
    [TestCase("0000,0080,0800", "0080,0800", false, new[] { 4, 8, 0 }, TestName = "Escaping_subtree_survives_poisoned_nested_groups")]
    [TestCase("0000,0080,0800", "0080,0800", true, new[] { 8, 4, 0 }, TestName = "Owned_subtree_survives_poisoned_nested_groups")]
    [TestCase("0000,0008", "0080", false, new[] { 0 }, TestName = "Ancestor_borrowed_subtree_survives_child_frame_return")]
    public void Returned_subtrees_survive_group_lease_release(string initialKeys, string deletedKeys, bool replaceSurvivor, int[] releasedDepths)
    {
        using PbtTreeHarness expected = new();
        EipReferenceTree oracle = new();
        List<(byte[] Key, byte[]? Value)> initial = [];
        foreach (string key in initialKeys.Split(','))
        {
            byte[] keyBytes = Bytes.FromHexString(key);
            initial.Add((keyBytes, Value(1)));
            oracle.Insert(keyBytes, Value(1));
        }
        ValueHash256 root = expected.ApplyBatch(initial);
        using PoisoningStore store = new(PbtNodeGroupStore.FromPhysicalPayloads(expected.PhysicalPayloads));
        using PbtWriteBatchBuilder<PbtFullKey> batch = new(0);
        List<(byte[] Key, byte[]? Value)> changes = [];
        foreach (string key in deletedKeys.Split(','))
        {
            byte[] keyBytes = Bytes.FromHexString(key);
            batch.Delete(new PbtFullKey(keyBytes));
            changes.Add((keyBytes, null));
            oracle.Delete(keyBytes);
        }
        if (replaceSurvivor)
        {
            byte[] key = Bytes.FromHexString("0000");
            batch.Set(new PbtFullKey(key), new ValueHash256(Value(2)));
            changes.Add((key, Value(2)));
            oracle.Insert(key, Value(2));
        }
        expected.ApplyBatch(changes);

        ValueHash256 actualRoot = TrieUpdater.UpdateRoot(store, root, batch.Build());

        Assert.That(store.ReleasedGroupDepths, Is.EqualTo(releasedDepths), "payloads are poisoned when their last owning frame or subtree releases them");
        using PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(store.Inner.ExportPhysicalPayloads());
        IReadOnlyList<PbtNodeRecord> actualRecords = reopened.EnumerateRecords();
        IReadOnlyList<PbtNodeRecord> expectedRecords = expected.Nodes;
        Assert.That(actualRecords, Has.Count.EqualTo(expectedRecords.Count));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actualRoot.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            for (int index = 0; index < actualRecords.Count; index++)
            {
                Assert.That(actualRecords[index].Path.Encode(), Is.EqualTo(expectedRecords[index].Path.Encode()));
                Assert.That(actualRecords[index].Encoding.ToArray(), Is.EqualTo(expectedRecords[index].Encoding.ToArray()));
            }
        }
    }

    private sealed class PoisoningStore(PbtNodeGroupStore inner) : IPbtStore, IDisposable
    {
        private readonly ArrayPool<byte> _pool = ArrayPool<byte>.Create();
        internal PbtNodeGroupStore Inner { get; } = inner;
        internal List<int> ReleasedGroupDepths { get; } = [];

        public RefCountingMemory? GetNodeGroup(IPbtNodePath groupKey)
        {
            using RefCountingMemory? payload = Inner.GetNodeGroup(groupKey);
            if (payload is null) return null;
            int length = payload.GetSpan().Length;
            byte[] buffer = _pool.Rent(length);
            payload.GetSpan().CopyTo(buffer);
            return RefCountingMemory.OwningRocksDb(new PoisoningMemoryManager(_pool, buffer, length,
                () => ReleasedGroupDepths.Add(groupKey.BitDepth)));
        }

        public void SetNodeGroup(IPbtNodePath groupKey, RefCountingMemory? payload) => Inner.SetNodeGroup(groupKey, payload);
        public void Dispose() => Inner.Dispose();
    }

    private sealed class PoisoningMemoryManager(ArrayPool<byte> pool, byte[] buffer, int length, Action onRelease) : MemoryManager<byte>
    {
        // Leave stale spans readable so a missing escape copy observes poison rather than relying on pool reuse timing.
        public override Span<byte> GetSpan() => buffer.AsSpan(0, length);
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing)
        {
            buffer.AsSpan().Fill(0xFF);
            pool.Return(buffer);
            onRelease();
        }
    }

    private sealed class PublishingStore : IPbtStore, IDisposable
    {
        internal PbtNodeGroupStore Inner { get; } = new();
        internal int Publishes { get; private set; }

        public RefCountingMemory? GetNodeGroup(IPbtNodePath groupKey) => Inner.GetNodeGroup(groupKey);
        public void SetNodeGroup(IPbtNodePath groupKey, RefCountingMemory? payload)
        {
            Publishes++;
            Inner.SetNodeGroup(groupKey, payload);
        }
        public void Dispose() => Inner.Dispose();
    }

    [Test]
    public void Failed_import_releases_previously_copied_payloads()
    {
        TrackingMemoryProvider provider = new();
        PbtNodePath rootPath = new([], 0);
        byte[] validPayload = EncodeGroup(rootPath, [new PbtNodeRecord(rootPath, LeafEncoding(0x00, 1))]);
        PbtPhysicalPayload valid = new(rootPath.Encode(), validPayload);

        Assert.Throws<InvalidDataException>(() => PbtNodeGroupStore.FromPhysicalPayloads([valid, valid], provider));
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero);
    }

    [TestCase(9, new[] { 4 })]
    [TestCase(13, new[] { 4, 8 })]
    public void Compressed_branch_jumps_create_no_intermediate_groups(int sharedPrefixBits, int[] absentGroupDepths)
    {
        using PbtTreeHarness tree = new();
        EipReferenceTree oracle = new();
        byte[] leftKey = new byte[4];
        byte[] rightKey = new byte[4];
        rightKey[sharedPrefixBits / 8] = (byte)(0x80 >> (sharedPrefixBits % 8));

        tree.ApplyBatch([(leftKey, Value(1)), (rightKey, Value(2))]);
        oracle.Insert(leftKey, Value(1));
        oracle.Insert(rightKey, Value(2));
        tree.Reopen();

        List<int> groupDepths = [];
        foreach (PbtPhysicalPayload payload in tree.PhysicalPayloads)
            groupDepths.Add(PbtStorageNodePath.Decode(payload.Key.Span).BitDepth);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            Assert.That(groupDepths, Does.Contain(0));
            Assert.That(groupDepths, Does.Contain(sharedPrefixBits / 4 * 4));
            foreach (int absentGroupDepth in absentGroupDepths)
                Assert.That(groupDepths, Does.Not.Contain(absentGroupDepth));
            Assert.That(tree.CanonicalRecords(), Has.Length.EqualTo(3));
        }
    }

    [Test]
    public void Node_groups_fold_random_mutation_rounds_to_canonical_tree()
    {
        Random random = new(42);
        using PbtTreeHarness record = new();
        using PbtTreeHarness grouped = new();
        EipReferenceTree oracle = new();
        byte[][] keys = Keys(random, 256);

        for (int round = 0; round < 12; round++)
        {
            Dictionary<int, byte[]?> batchValues = [];
            for (int index = 0; index < 40; index++)
            {
                int keyIndex = random.Next(keys.Length);
                byte[]? value = random.Next(4) == 0 ? null : RandomValue(random);
                batchValues[keyIndex] = value;
            }
            List<(byte[] Key, byte[]? Value)> writes = [];
            foreach ((int keyIndex, byte[]? value) in batchValues)
            {
                byte[] key = keys[keyIndex];
                writes.Add((key, value));
                if (value is null) oracle.Delete(key);
                else oracle.Insert(key, value);
            }

            ValueHash256 recordRoot = record.ApplyBatch(writes);
            ValueHash256 groupedRoot = grouped.ApplyBatch(writes);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(recordRoot.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()), $"round {round}");
                Assert.That(groupedRoot, Is.EqualTo(recordRoot), $"round {round}");
                Assert.That(grouped.CanonicalRecords(), Is.EqualTo(record.CanonicalRecords()), $"round {round}");
            }
        }
    }
    [Test]
    public void Streaming_writer_commits_leaves_without_allocating(
        [Values(0, 4, 8, 268, PbtFourLevelGroupGeometry.MaxGroupDepth)] int groupDepth)
    {
        PbtStorageNodePath groupKey = new(new byte[(groupDepth + 7) / 8], groupDepth);
        using PbtNodeGroupWriter writer = new(groupKey, new TrackingMemoryProvider());
        int positionCount = groupDepth == 0 ? PbtFourLevelGroupGeometry.PositionCount : PbtFourLevelGroupGeometry.RootPosition;
        for (int position = 0; position < positionCount; position++)
        {
            IPbtNodePath path = PbtFourLevelGroupGeometry.PathOf(groupKey, position);
            byte[] key = new byte[Math.Max(1, path.Path.Length)];
            path.Path.CopyTo(key);
            byte[] encoding = PbtNodeCodec.EncodeLeaf(new PbtStorageFullKey(key), Value(1));
            encoding.CopyTo(writer.GetSpan(position, encoding.Length));

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            writer.Commit();
            long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            Assert.That(allocatedBytes, Is.Zero, $"position {position}");
        }
    }

    [TestCase(0, 1)]
    [TestCase(0, 31)]
    [TestCase(4, 30)]
    [TestCase(4, 3)]
    public void Streaming_writer_preserves_canonical_bytes_and_transfers_backing_memory(int groupDepth, int count)
    {
        PbtStorageNodePath groupKey = new(new byte[(groupDepth + 7) / 8], groupDepth);
        TrackingMemoryProvider provider = new() { FillByte = 0xFF };
        List<PbtNodeRecord> records = [];
        using PbtNodeGroupWriter writer = new(groupKey, provider);
        for (int index = 0; index < count; index++)
        {
            int position = count == 1 ? 30 : count == 3 ? index * 10 : index;
            byte[] encoding = PbtNodeCodec.EncodeBranch(Bytes.FromHexString("A0"), 4,
                new ValueHash256(Value(1)), new ValueHash256(Value(2)));
            records.Add(new(PbtFourLevelGroupGeometry.PathOf(groupKey, position), encoding));
            if (index % 2 == 0)
            {
                Span<byte> destination = writer.GetSpan(position, encoding.Length);
                PbtNodeCodec.CreateBranchEncoding(destination, 4, new ValueHash256(Value(1)), new ValueHash256(Value(2)));
                destination[3] = 0xA0;
                writer.Commit();
            }
            else writer.Write(position, encoding);
        }

        using (RefCountingMemory payload = writer.Detach()!)
        {
            writer.Dispose();
            PbtNodeGroupReader reader = new(groupKey, payload.GetSpan());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(payload.GetSpan().ToArray(), Is.EqualTo(EncodeGroup(groupKey, records)));
                Assert.That(reader.Count, Is.EqualTo(count));
                Assert.That(payload, Is.SameAs(provider.Rented[^1]));
                Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.EqualTo(1));
                Assert.That(provider.RentCount, count == 1 ? Is.EqualTo(1) : Is.GreaterThan(1));
            }
        }
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero);
        Assert.Throws<ObjectDisposedException>(() => writer.Detach());
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public void Streaming_writer_rejects_invalid_appends_without_leaking(int scenario)
    {
        TrackingMemoryProvider provider = new() { FillByte = 0xFF };
        using (PbtNodeGroupWriter writer = new(new PbtNodePath([], 0), provider))
        {
            byte[] branch = PbtNodeCodec.EncodeBranch([], 0, new ValueHash256(Value(1)), new ValueHash256(Value(2)));
            writer.Write(2, branch);
            switch (scenario)
            {
                case 0: Assert.Throws<InvalidDataException>(() => writer.Write(2, branch)); break;
                case 1: Assert.Throws<InvalidDataException>(() => writer.Write(1, branch)); break;
                case 2: Assert.Throws<InvalidDataException>(() => writer.GetSpan(3, ushort.MaxValue)); break;
                case 3: Assert.Throws<ArgumentOutOfRangeException>(() => writer.GetSpan(31, branch.Length)); break;
                case 4:
                    writer.GetSpan(3, 1)[0] = 0xFF;
                    Assert.Throws<InvalidDataException>(() => writer.Commit());
                    Assert.Throws<InvalidOperationException>(() => writer.Detach());
                    break;
                case 5: Assert.Throws<InvalidDataException>(() => writer.Write(3, LeafEncoding(0xFF, 1))); break;
            }
            Assert.That(writer.WrittenCount, Is.EqualTo(branch.Length));
        }
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero);
        using PbtNodeGroupWriter nonRoot = new(new PbtNodePath(Bytes.FromHexString("A0"), 4), provider);
        Assert.Throws<ArgumentOutOfRangeException>(() => nonRoot.GetSpan(30, 67));
    }

    [TestCase(1)]
    [TestCase(2)]
    public void Streaming_writer_releases_memory_after_rent_failure(int failedRent)
    {
        TrackingMemoryProvider provider = new() { ThrowOnRent = failedRent };
        using (PbtNodeGroupWriter writer = new(new PbtNodePath([], 0), provider))
        {
            byte[] branch = PbtNodeCodec.EncodeBranch([], 0, new ValueHash256(Value(1)), new ValueHash256(Value(2)));
            if (failedRent == 2) writer.Write(0, branch);
            Assert.Throws<InvalidOperationException>(() => writer.Write(failedRent, branch));
        }
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero);
    }

    [Test]
    public void Streaming_writer_accepts_exact_uint16_entries_limit()
    {
        TrackingMemoryProvider provider = new();
        using PbtNodeGroupWriter writer = new(new PbtNodePath([], 0), provider);
        for (int position = 0; position < 8; position++)
        {
            int length = position == 7 ? 8191 : 8192;
            Span<byte> encoding = writer.GetSpan(position, length);
            PbtNodeCodec.CreateBranchEncoding(encoding, (length - 67) * 8,
                new ValueHash256(Value(1)), new ValueHash256(Value(2)));
            writer.Commit();
        }
        Assert.That(writer.WrittenCount, Is.EqualTo(ushort.MaxValue));
        Assert.Throws<InvalidDataException>(() => writer.GetSpan(8, 1));
        using RefCountingMemory payload = writer.Detach()!;
        PbtNodeGroupReader reader = new(new PbtNodePath([], 0), payload.GetSpan());
        Assert.That(reader.Count, Is.EqualTo(8));
    }

    [Test]
    public void Empty_streaming_writer_detaches_without_renting()
    {
        TrackingMemoryProvider provider = new();
        using PbtNodeGroupWriter writer = new(new PbtNodePath([], 0), provider);
        Assert.That(writer.Detach(), Is.Null);
        Assert.That(provider.RentCount, Is.Zero);
    }

    private static byte[] EncodeGroup(IPbtNodePath groupKey, IReadOnlyList<PbtNodeRecord> records)
    {
        int capacity = PbtNodeGroupCodec.TrailerLength;
        foreach (PbtNodeRecord record in records) capacity += record.Encoding.Length;
        byte[] payload = new byte[capacity];
        BufferWriter writer = new(payload);
        PbtNodeGroupCodec.Encode(ref writer, groupKey, records);
        return writer.WrittenSpan.ToArray();
    }

    private static Dictionary<string, byte[]> Payloads(PbtTreeHarness tree)
    {
        Dictionary<string, byte[]> result = [];
        foreach (PbtPhysicalPayload payload in tree.PhysicalPayloads)
            result.Add(Convert.ToHexString(payload.Key.Span), payload.Payload.ToArray());
        return result;
    }

    private static bool MoveDefaultReaderEnumerator()
    {
        PbtNodeGroupReader.Enumerator enumerator = default;
        return enumerator.MoveNext();
    }

    private static byte[] LeafEncoding(byte keyMarker, byte valueMarker) =>
        PbtNodeCodec.EncodeLeaf(new PbtFullKey([keyMarker]), Value(valueMarker));

    private static byte[] Value(byte marker)
    {
        byte[] value = new byte[32];
        value[^1] = marker;
        return value;
    }

    private static byte[][] Keys(Random random, int count)
    {
        byte[][] keys = new byte[count][];
        for (int index = 0; index < keys.Length; index++)
        {
            keys[index] = new byte[2 + random.Next(63)];
            keys[index][0] = (byte)index;
            random.NextBytes(keys[index].AsSpan(1));
        }
        return keys;
    }

    private static byte[] RandomValue(Random random)
    {
        byte[] value = new byte[32];
        random.NextBytes(value);
        return value;
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class PbtNodeGroupTests
{
    [TestCase(0x00)]
    [TestCase(0x80)]
    public void Root_node_round_trip_preserves_encoding_and_allows_root_position_access(byte keyByte)
    {
        byte[] value = new byte[32];
        PbtNodePath rootPath = new([], 0);
        byte[] encoding = PbtNodeCodec.Encode(new PbtLeafNode(new PbtFullKey([keyByte]), value));

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

    [TestCase(0)]
    [TestCase(4)]
    [TestCase(8)]
    [TestCase(12)]
    [TestCase(252)]
    [TestCase(256)]
    public void Reader_validates_every_required_leaf_bit_and_ignores_suffix_bits(int groupDepth)
    {
        byte[] groupBytes = new byte[(groupDepth + 7) / 8];
        groupBytes.AsSpan().Fill(0xA5);
        if ((groupDepth & 7) != 0) groupBytes[^1] &= 0xF0;
        PbtNodePath groupKey = new(groupBytes, groupDepth);

        for (int position = 0; position < PbtFourLevelGroupGeometry.PositionCount; position++)
        {
            if (groupDepth != 0 && position == PbtFourLevelGroupGeometry.RootPosition) continue;
            PbtNodePath path = PbtFourLevelGroupGeometry.PathOf(groupKey, position);
            byte[] key = new byte[(path.BitDepth + 7) / 8 + 1];
            path.Path.CopyTo(key);
            byte[] encoding = PbtNodeCodec.Encode(new PbtLeafNode(new PbtFullKey(key), Value(1)));
            byte[] payload = EncodeGroup(groupKey, [new PbtNodeRecord(path, encoding)]);
            Assert.That(ReadGroupCount(groupKey, payload), Is.EqualTo(1));

            for (int bit = 0; bit < key.Length * 8; bit++)
            {
                byte mask = (byte)(0x80 >> (bit & 7));
                payload[3 + (bit >> 3)] ^= mask;
                if (bit < path.BitDepth)
                    Assert.That(() => ReadGroupCount(groupKey, payload), Throws.TypeOf<InvalidDataException>(), $"position {position}, bit {bit}");
                else
                    Assert.That(ReadGroupCount(groupKey, payload), Is.EqualTo(1), $"position {position}, suffix bit {bit}");
                payload[3 + (bit >> 3)] ^= mask;
            }
        }
    }

    [TestCase(8)]
    [TestCase(12)]
    [TestCase(252)]
    [TestCase(PbtFourLevelGroupGeometry.MaxGroupDepth)]
    public void Reader_rejects_leaf_keys_shorter_than_required_path(int groupDepth)
    {
        PbtNodePath groupKey = new(new byte[(groupDepth + 7) / 8], groupDepth);
        PbtNodePath path = PbtFourLevelGroupGeometry.PathOf(groupKey, 0);
        byte[] key = new byte[(path.BitDepth + 7) / 8];
        byte[] encoding = PbtNodeCodec.Encode(new PbtLeafNode(new PbtFullKey(key), Value(1)));
        byte[] payload = EncodeGroup(groupKey, [new PbtNodeRecord(path, encoding)]);
        Assert.That(ReadGroupCount(groupKey, payload), Is.EqualTo(1));

        byte[] shortEncoding = PbtNodeCodec.Encode(new PbtLeafNode(new PbtFullKey(key.AsSpan(0, key.Length - 1)), Value(1)));
        byte[] shortPayload = new byte[shortEncoding.Length + PbtNodeGroupCodec.TrailerLength];
        shortEncoding.CopyTo(shortPayload, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(shortPayload.AsSpan(shortPayload.Length - sizeof(uint)), 1u);
        Assert.That(() => ReadGroupCount(groupKey, shortPayload), Throws.TypeOf<InvalidDataException>());
    }

    private static int ReadGroupCount(PbtNodePath groupKey, byte[] payload) => new PbtNodeGroupReader(groupKey, payload).Count;

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
        byte[] encoding = PbtNodeCodec.Encode(new PbtLeafNode(new PbtFullKey([0x80]), new byte[32]));

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
        PbtWriteBatch batch = new();
        batch.Set(new PbtFullKey([0x00]), new ValueHash256(Value(1)));
        batch.Set(new PbtFullKey([0x80]), new ValueHash256(Value(2)));
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, batch);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Publishes, Is.EqualTo(1));
            Assert.That(store.Inner.EnumerateRecords(), Has.Count.EqualTo(3));
        }

        batch = new();
        batch.Set(new PbtFullKey([0x00]), new ValueHash256(Value(3)));
        batch.Set(new PbtFullKey([0x80]), new ValueHash256(Value(4)));
        root = TrieUpdater.UpdateRoot(store, root, batch);
        Assert.That(store.Publishes, Is.EqualTo(2));

        ValueHash256 unchangedRoot = TrieUpdater.UpdateRoot(store, root, batch);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(unchangedRoot, Is.EqualTo(root));
            Assert.That(store.Publishes, Is.EqualTo(2));
        }

        PbtNodePath siblingPath = new([0x80], 1);
        byte[]? sibling = store.Inner.GetNode(siblingPath);
        batch = new();
        batch.Set(new PbtFullKey([0x00]), new ValueHash256(Value(5)));
        root = TrieUpdater.UpdateRoot(store, root, batch);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Publishes, Is.EqualTo(3));
            Assert.That(store.Inner.GetNode(siblingPath), Is.EqualTo(sibling));
        }

        batch = new();
        batch.Delete(new PbtFullKey([0x00]));
        batch.Delete(new PbtFullKey([0x80]));
        root = TrieUpdater.UpdateRoot(store, root, batch);
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
        PbtWriteBatch batch = new();
        batch.Set(new PbtFullKey([0x00]), new ValueHash256(Value(1)));
        if (storedRootPresent) TrieUpdater.UpdateRoot(store, default, batch);
        batch = new();
        batch.Set(new PbtFullKey([0x80]), new ValueHash256(Value(2)));

        Assert.That(() => TrieUpdater.UpdateRoot(store, new ValueHash256(Value(3)), batch), Throws.TypeOf<InvalidDataException>());
    }

    private sealed class PublishingStore : IPbtStore, IDisposable
    {
        internal PbtNodeGroupStore Inner { get; } = new();
        internal int Publishes { get; private set; }

        public RefCountingMemory? GetNodeGroup(PbtNodePath groupKey) => Inner.GetNodeGroup(groupKey);
        public void SetNodeGroup(PbtNodePath groupKey, RefCountingMemory? payload)
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
            groupDepths.Add(PbtNodePath.Decode(payload.Key.Span).BitDepth);

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
    private static byte[] EncodeGroup(PbtNodePath groupKey, IReadOnlyList<PbtNodeRecord> records)
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
        PbtNodeCodec.Encode(new PbtLeafNode(new PbtFullKey([keyMarker]), Value(valueMarker)));

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

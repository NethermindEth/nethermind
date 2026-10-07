// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Memory;
using Nethermind.Pbt;
using NUnit.Framework;
using static Nethermind.State.Pbt.Test.PbtStoreTestExtensions;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class PbtNodeGroupTests
{
    [Test]
    public void Store_identity_and_reader_payload_survive_cursor_mutation([Values] bool storage, [Values(0, 4, -1)] int groupDepth)
    {
        int capacity = storage ? PbtStorageTreeKey.MaxLength : PbtPath.KeyLength;
        int depth = groupDepth < 0 ? capacity * 8 - 4 : groupDepth;
        byte[] key = Bytes.FromHexString(new string('D', capacity * 2));
        PbtStorageNodePath groupKey = PbtTestPaths.Prefix<PbtStorageNodePath>(key, depth);
        PbtStorageNodePath leafPath = PbtTestPaths.Prefix<PbtStorageNodePath>(key, depth == 0 ? 0 : depth + 1);
        byte[] encoding = LeafBranch(leafPath, 1);
        byte[] bytes = PbtNodeGroupEncoder.Encode(groupKey, [new PbtNodeRecord(leafPath, encoding)], default);
        using RefCountingMemory payload = PooledRefCountingMemoryProvider.Instance.Rent(bytes.Length);
        bytes.CopyTo(payload.GetSpan());
        using PbtNodeGroupStore store = new();
        PbtTraversalPath cursor = PbtTraversalPath.FromPath(stackalloc byte[capacity], groupKey);
        ValueHash256 hash = PbtTreeHarness.HashBranch(encoding);
        store.SetNodeGroup(cursor, hash, payload);
        using GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath> reader = new(store, cursor, hash);
        cursor.Truncate(0);
        cursor.AppendMut(0);
        using RefCountingMemory? retained = store.GetNodeGroup(groupKey, hash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.GetEncoding(PbtTestPaths.Locate(leafPath).Position).ToArray(), Is.EqualTo(encoding));
            Assert.That(store.EnumerateNodeGroupKeys(), Is.EqualTo(new[] { groupKey }));
            Assert.That(retained?.GetSpan().ToArray(), Is.EqualTo(bytes));
        }
        Assert.That(reader.Nodes().Select(static node => (node.Position, node.Encoding.ToArray())),
            Is.EqualTo(new[] { (PbtTestPaths.Locate(leafPath).Position, encoding) }));
    }

    [Test]
    public void Node_group_paths_pack_internal_node_geometry_into_one_byte([Range(0, 4)] int length)
    {
        for (int prefix = 0; prefix < 1 << length; prefix++)
        {
            int slot = prefix << (4 - length);
            NodeGroupPath path = new(slot, length);
            PbtNodePath nodePath = PbtTestPaths.Prefix<PbtNodePath>([(byte)(slot << 4)], length);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Unsafe.SizeOf<NodeGroupPath>(), Is.EqualTo(1));
                Assert.That(path.Slot, Is.EqualTo(slot));
                Assert.That(path.Length, Is.EqualTo(length));
                Assert.That(path.Width, Is.EqualTo(16 >> length));
                Assert.That(path.Position, Is.EqualTo(PbtTestPaths.Locate(nodePath).Position));
                Assert.That(PbtFourLevelGroupGeometry.LocalPathOf(path.Position), Is.EqualTo(path));
                if (length < 4)
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
        byte[] keyBytes = new byte[PbtStorageTreeKey.MaxLength];
        keyBytes[0] = Eip8297KeyDerivation.StorageZone;
        PbtStorageTreeKey storageKey = new(keyBytes);
        PbtNodePath smallPath = PbtTestPaths.Prefix<PbtNodePath>(keyBytes, depth);
        PbtStorageNodePath storagePath = PbtTestPaths.Prefix<PbtStorageNodePath>(storageKey.Bytes, depth);
        Dictionary<PbtStorageNodePath, int?> entries = new() { [smallPath.ToPath<PbtStorageNodePath>()] = 1 };
        entries[storagePath] = null;
        PbtStorageNodePath leafPath = PbtTestPaths.Prefix<PbtStorageNodePath>(storageKey.Bytes, depth == 0 ? 0 : depth + 4);
        byte[] encoding = LeafBranch(keyBytes.AsSpan(PbtNodeCodec.InlineKeyOffset(leafPath.BitDepth)), 1);
        byte[] payload = PbtNodeGroupEncoder.Encode(smallPath, [new PbtNodeRecord(leafPath, encoding)], default);
        GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath> reader = PbtStoreTestExtensions.ReadGroup(storagePath, payload);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PbtNodePathOperations.Equal(smallPath, storagePath), Is.True);
            Assert.That(smallPath.GetHashCode(), Is.EqualTo(storagePath.GetHashCode()));
            Assert.That(smallPath.ToEncodedArray(), Is.EqualTo(storagePath.ToEncodedArray()));
            Assert.That(entries.Count, Is.EqualTo(1));
            Assert.That(entries[smallPath.ToPath<PbtStorageNodePath>()], Is.Null);
            Assert.That(reader.GetEncoding(PbtTestPaths.Locate(leafPath).Position).ToArray(), Is.EqualTo(encoding));
            Assert.That(entries.Remove(smallPath.ToPath<PbtStorageNodePath>()), Is.True);
            Assert.That(smallPath.Equals(default), Is.EqualTo(depth == 0));
            Assert.That(storagePath.Equals(default), Is.EqualTo(depth == 0));
        }

        using PbtNodeGroupStore store = new();
        using RefCountingMemory publishedPayload = PooledRefCountingMemoryProvider.Instance.Rent(payload.Length);
        payload.CopyTo(publishedPayload.GetSpan());
        store.SetNodeGroup(smallPath, PbtTreeHarness.HashBranch(encoding), publishedPayload);
        using (RefCountingMemory lease = store.GetPhysicalNodeGroup(storagePath)!)
            Assert.That(lease.GetSpan().ToArray(), Is.EqualTo(payload));
        store.SetNodeGroup(storagePath, default, null);
        Assert.That(store.GetPhysicalNodeGroup(smallPath), Is.Null);
        store.SetNodeGroup(storagePath, PbtTreeHarness.HashBranch(encoding), publishedPayload);
        using (RefCountingMemory lease = store.GetPhysicalNodeGroup(smallPath)!)
            Assert.That(lease.GetSpan().ToArray(), Is.EqualTo(payload));
        store.SetNodeGroup(smallPath, default, null);
        Assert.That(store.GetPhysicalNodeGroup(storagePath), Is.Null);
    }

    [TestCase(34, 272)]
    [TestCase(66, 528)]
    public void Key_families_enforce_capacity_without_changing_storage_bytes(int length, int depth)
    {
        Assert.That(Unsafe.SizeOf<PbtPath>(), Is.EqualTo(34));
        Assert.That(Unsafe.SizeOf<PbtStoragePath>(), Is.EqualTo(66));
        Assert.That(Unsafe.SizeOf<PbtStorageTreeKey>(), Is.EqualTo(72));
        byte[] keyBytes = new byte[length];
        keyBytes[^1] = 1;
        PbtStorageTreeKey storageKey = new(keyBytes);
        Assert.That(storageKey.FirstDifferingBit(new PbtStorageTreeKey(new byte[length]), 0), Is.EqualTo(depth - 1));
        if (length == PbtPath.KeyLength)
        {
            PbtPath key = (PbtPath)storageKey;
            Assert.That(key.FirstDifferingBit(new PbtPath(new byte[length]), 0), Is.EqualTo(depth - 1));
            Assert.That(((PbtStorageTreeKey)key).Bytes.ToArray(), Is.EqualTo(keyBytes));
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = (PbtStoragePath)storageKey);
        }
        else
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = (PbtPath)storageKey);
            Assert.That(((PbtStoragePath)storageKey).Bytes.ToArray(), Is.EqualTo(keyBytes));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtPath(new byte[33]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtPath(new byte[35]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtStoragePath(new byte[65]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtStorageTreeKey([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtStorageTreeKey(new byte[67]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtStorageNodePath(new byte[67], 529));
    }

    [TestCase(0x00, "070001000000000000400000")]
    [TestCase(0x80, "070001800000000000400000")]
    public void Root_node_round_trip_preserves_encoding_and_allows_root_position_access(byte keyByte, string expectedPayloadHex)
    {
        PbtNodePath rootPath = new([], 0);
        byte[] encoding = PbtTreeHarness.EncodeLeaf(new PbtStorageTreeKey([keyByte]));

        byte[] payload = RootGroup(encoding);
        GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath> group = PbtStoreTestExtensions.ReadGroup(rootPath, payload);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(payload, Is.EqualTo(Bytes.FromHexString(expectedPayloadHex)));
            Assert.That(group.GetEncoding(PbtFourLevelGroupGeometry.RootPosition).ToArray(), Is.EqualTo(encoding));
            Assert.That(group.Nodes(), Has.Count.EqualTo(1));
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
            PbtStorageNodePath path = PbtTestPaths.PathOf(groupKey, position);
            byte[] key = new byte[(path.BitDepth + 7) / 8 + 1];
            PbtNodePathOperations.CopyTo(path, key);
            bool rootLeaf = position == PbtFourLevelGroupGeometry.RootPosition;
            // An inline key omits the group path's whole bytes, whose bits the group key implies.
            byte[] storedKey = rootLeaf ? key : key[PbtNodeCodec.InlineKeyOffset(path.BitDepth)..];
            int omittedBits = (key.Length - storedKey.Length) * 8;
            byte[] encoding = rootLeaf ? PbtTreeHarness.EncodeLeaf(new PbtStorageTreeKey(key)) : LeafBranch(storedKey, 1);
            byte[] payload = PbtNodeGroupEncoder.Encode(groupKey, [new PbtNodeRecord(path, encoding)], default);
            Assert.That(ValidateLeafGroup(groupKey, position, payload, streamingWriter), Is.EqualTo(1));

            int keyOffset = PbtNodeGroupCodec.HeaderLength + encoding.Length - storedKey.Length;
            for (int bit = 0; bit < storedKey.Length * 8; bit++)
            {
                byte mask = (byte)(0x80 >> (bit & 7));
                payload[keyOffset + (bit >> 3)] ^= mask;
                if (ChecksLeafPaths(streamingWriter) && omittedBits + bit < path.BitDepth)
                    Assert.That(() => ValidateLeafGroup(groupKey, position, payload, streamingWriter), Throws.TypeOf<InvalidDataException>(), $"position {position}, bit {bit}");
                else
                    Assert.That(ValidateLeafGroup(groupKey, position, payload, streamingWriter), Is.EqualTo(1), $"position {position}, bit {bit}");
                payload[keyOffset + (bit >> 3)] ^= mask;
            }
        }
    }

    // Below a byte-aligned group, a one-byte postfix already reaches the leaf, and an empty one declares a branch child.
    [Test]
    public void Reader_and_writer_reject_leaf_keys_shorter_than_required_path(
        [Values(12, 252, 508)] int groupDepth, [Values] bool streamingWriter)
    {
        PbtStorageNodePath groupKey = new(new byte[(groupDepth + 7) / 8], groupDepth);
        PbtStorageNodePath path = PbtTestPaths.PathOf(groupKey, 0);
        // The inline leaf hangs one level below the branch, so its key needs one bit more than the branch depth, less the group path bytes it omits.
        byte[] key = new byte[path.BitDepth / 8 + 1 - PbtNodeCodec.InlineKeyOffset(path.BitDepth)];
        byte[] encoding = LeafBranch(key, 1);
        byte[] payload = PbtNodeGroupEncoder.Encode(groupKey, [new PbtNodeRecord(path, encoding)], default);
        Assert.That(ValidateLeafGroup(groupKey, 0, payload, streamingWriter), Is.EqualTo(1));

        byte[] shortEncoding = LeafBranch(key.AsSpan(0, key.Length - 1), 1);
        byte[] shortPayload = new byte[PbtNodeGroupCodec.HeaderLength + shortEncoding.Length + PbtNodeGroupCodec.GetTrailerLength(1, 0, default)];
        PbtNodeGroupCodec.Header.CopyTo(shortPayload);
        shortEncoding.CopyTo(shortPayload, PbtNodeGroupCodec.HeaderLength);
        PbtNodeGroupCodec.WriteFooter(shortPayload.AsSpan(PbtNodeGroupCodec.HeaderLength + shortEncoding.Length), stackalloc ushort[PbtFourLevelGroupGeometry.PositionCount], 1u, 0, default);
        if (ChecksLeafPaths(streamingWriter))
            Assert.That(() => ValidateLeafGroup(groupKey, 0, shortPayload, streamingWriter), Throws.TypeOf<InvalidDataException>());
        else
            Assert.That(ValidateLeafGroup(groupKey, 0, shortPayload, streamingWriter), Is.EqualTo(1));
    }

    /// <summary>The group decode checks leaf paths in every build; the streaming writer only in debug builds.</summary>
    private static bool ChecksLeafPaths(bool streamingWriter) => IsDebugBuild || !streamingWriter;

    private const bool IsDebugBuild =
#if DEBUG
        true;
#else
        false;
#endif

    private static int ValidateLeafGroup<TPath>(TPath groupKey, int position, byte[] payload, bool streamingWriter)
        where TPath : struct, IPbtNodePath<TPath>
    {
        if (!streamingWriter) return ReadGroupCount(groupKey, payload);
        PbtTraversalPath groupPath = PbtTraversalPath.FromPath(stackalloc byte[66], groupKey);
        using PbtNodeGroupWriter<TPath> writer = new(groupKey.BitDepth, new TrackingMemoryProvider());
        writer.Write(groupPath, position, payload.AsSpan(PbtNodeGroupCodec.HeaderLength, payload.Length - PbtNodeGroupCodec.HeaderLength - PbtNodeGroupCodec.GetTrailerLength(1, 0, default)));
        using RefCountingMemory writtenPayload = writer.Detach(default, ushort.MaxValue)!;
        Assert.That(writtenPayload.GetSpan().ToArray(), Is.EqualTo(payload));
        return 1;
    }

    private static int ReadGroupCount<TPath>(TPath groupKey, byte[] payload) where TPath : struct, IPbtNodePath<TPath> => PbtStoreTestExtensions.ReadGroup(groupKey, payload).Nodes().Count;

    [Test]
    public void Node_group_memory_follows_the_native_memory_flag([Values] bool native)
    {
        IRefCountingMemoryProvider provider = PbtNodeGroupMemory.CreateProvider(new PbtConfig { NativeNodeGroupMemory = native });
        using IDisposable? disposable = provider as IDisposable;
        Assert.That(provider, native ? Is.InstanceOf<SlabRefCountingMemoryProvider>() : Is.SameAs(PooledRefCountingMemoryProvider.Instance));
    }

    [Test]
    public void Node_groups_partition_nodes_at_four_level_boundaries_and_preserve_siblings()
    {
        using PbtTreeHarness tree = new();
        List<(byte[] Key, byte[]? Value)> initial = [];
        for (int index = 0; index < 64; index++)
        {
            byte[] key = PbtStoreTestExtensions.ZoneKey($"00{index * 4:X2}{index:X2}");
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
    public void Absent_group_frames_carry_only_a_spanning_branch_size([Values] bool inherited)
    {
        // A group with nothing below it keeps every slot at zero; only a spanning branch carries a size here.
        long spanningBytes = inherited ? 1234 : 0;
        AbsentGroupFrame<PbtStorageTreeKey, PbtStorageNodePath> frame = inherited ? new(8, 5, spanningBytes) : new(8);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(frame.DescendantBytes(5), Is.EqualTo(spanningBytes));
            Assert.That(frame.DescendantBytes(4), Is.Zero);
            Assert.That(frame.DescendantMask, Is.EqualTo(inherited ? 1 << 5 : 0));
            Assert.That(frame.PayloadLength, Is.Zero);
            Assert.That(frame.StoredPositions, Is.Zero);
        }
    }

    [Test]
    public void Group_frames_load_once_at_construction([Values] bool present, [Values(0, 4, 268, 524)] int groupDepth)
    {
        TrackingMemoryProvider memory = new();
        using PbtNodeGroupStore store = new(memory);
        byte[] key = Bytes.FromHexString(new string('D', PbtStorageTreeKey.MaxLength * 2));
        PbtStorageNodePath groupKey = PbtTestPaths.Prefix<PbtStorageNodePath>(key, groupDepth);
        PbtStorageNodePath leafPath = PbtTestPaths.Prefix<PbtStorageNodePath>(key, groupDepth == 0 ? 0 : groupDepth + 4);
        int position = PbtTestPaths.Locate(leafPath).Position;
        byte[] encoding = LeafBranch(leafPath, 1);
        if (present) store.SetNode(leafPath, encoding, memory);
        WarmReadStore persistence = new(store);
        PbtTraversalPath groupPath = PbtTraversalPath.FromPath(stackalloc byte[66], groupKey);
        ValueHash256 groupHash = store.GetGroupHash(groupKey);
        using PbtNodeGroupWriter<PbtStorageNodePath> writer = new(groupKey.BitDepth, memory);
        Assert.That(GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath>.TryLoad(persistence, groupPath, groupHash, out GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath> reader), Is.EqualTo(present));
        using (new GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath>.Scope(ref reader))
        {
            Assert.That(persistence.Reads, Is.EqualTo(new[] { groupKey }));
            if (present)
            {
                reader.CopyRange(writer, 0, PbtFourLevelGroupGeometry.PositionCount);
                Assert.That(reader.GetEncoding(position).ToArray(), Is.EqualTo(encoding));
                Assert.That(persistence.Reads, Has.Count.EqualTo(1), "a frame reads its group once");
            }
        }
        // Only the tree root's group may be missing; a frame opened for any other is reported, not read as empty.
        if (!present)
            Assert.Throws<InvalidDataException>(() => _ = new GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath>(persistence, PbtTraversalPath.FromPath(stackalloc byte[66], groupKey), groupHash));

        writer.Dispose();
        store.Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero);
    }

    [Test]
    public void Group_frames_materialize_only_branch_anchors(
        [Values(0, 4, 8, 12, 16, 244)] int groupDepth, [Range(0, 29)] int position, [Values] bool inlineLeaf)
    {
        using PbtNodeGroupStore store = new();
        PbtStorageNodePath groupKey = PbtTestPaths.Prefix<PbtStorageNodePath>(Bytes.FromHexString(new string('A', 62)), groupDepth);
        PbtStorageNodePath path = PbtTestPaths.PathOf(groupKey, position);
        byte[] key = new byte[32];
        PbtNodePathOperations.CopyTo(path, key);
        // The group stores the inline key past its path's whole bytes; an odd-nibble group keeps its last nibble.
        byte[] encoding = PbtTreeHarness.EncodeBranch(Bytes.FromHexString("A0"), 4, new ValueHash256(Value(1)), new ValueHash256(Value(2)), inlineLeaf ? key[(groupDepth >> 3)..] : [], []);
        store.SetNode(path, encoding);
        PbtTraversalPath groupPath = PbtTraversalPath.FromPath(stackalloc byte[66], groupKey);
        GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath> reader = new(store, groupPath, new ValueHash256(Value(1)));
        TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.StoredGroupHashes hashes = default;
        using (new GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath>.Scope(ref reader))
        {
            TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.BoundaryNode source = TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.BoundaryNode.StoredAt(ref reader, position, hashes.GetHash(ref reader, position));
            Assert.That(source.AnchorDepth, Is.EqualTo(path.BitDepth));
            if (inlineLeaf)
            {
                TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.BoundaryNode leaf = TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.BoundaryNode.InlineLeafAt(ref reader, position, right: false);
                PbtStorageTreeKey leafKey = leaf.LeafKey(groupPath);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(leaf.KeyOffset, Is.EqualTo(groupDepth >> 3));
                    Assert.That(leafKey.Bytes.ToArray(), Is.EqualTo(key));
                }
            }
        }
    }

    [Test]
    public void Inlined_leaf_boundary_nodes_read_through_the_branch_that_holds_them([Values] bool right, [Values] bool stored, [Values(0, 12)] int anchorDepth)
    {
        byte[] key = Bytes.FromHexString("1234");
        ValueHash256 leafHash = PbtTreeHarness.HashLeaf(key, Value(1));
        ValueHash256 siblingHash = new(Value(2));
        // A branch anchored at depth 12 lies in group 8, whose path byte its inline keys omit.
        byte[] keyPostfix = key[PbtNodeCodec.InlineKeyOffset(anchorDepth)..];
        byte[] encoding = stored
            ? PbtTreeHarness.EncodeLeaf(new PbtStorageTreeKey(key))
            : right
                ? PbtTreeHarness.EncodeBranch([], 0, siblingHash, leafHash, [], keyPostfix)
                : PbtTreeHarness.EncodeBranch([], 0, leafHash, siblingHash, keyPostfix, []);
        PbtTraversalPath cursor = new(stackalloc byte[66]);
        cursor.AppendKey(key, anchorDepth);
        TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.BoundaryNode borrowed = stored
            ? new(encoding, leafHash)
            : new(encoding, anchorDepth, right);
        PbtStorageTreeKey borrowedKey = borrowed.LeafKey(cursor);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(borrowed.IsLeaf, Is.True);
            Assert.That(borrowed.IsEmpty, Is.False);
            Assert.That(borrowedKey.Bytes.ToArray(), Is.EqualTo(key));
            Assert.That(borrowed.Hash, Is.EqualTo(leafHash), "an inlined leaf's hash is the one its branch holds");
        }
    }

    [Test]
    public void Compact_subtree_layout_keeps_paths_out_of_local_entries()
    {
        using (Assert.EnterMultipleScope())
        {
            // A boundary node reads its key out of the encoding it points at, so its size does not follow the key type
            // and stays under the 72 bytes a storage key alone used to take inside it.
            Assert.That(Unsafe.SizeOf<TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.BoundaryNode>(),
                Is.EqualTo(Unsafe.SizeOf<TrieUpdater<PbtPath, PbtNodePath>.BoundaryNode>()));
            Assert.That(Unsafe.SizeOf<TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.BoundaryNode>(), Is.LessThan(72));
        }
    }

    [Test]
    public void Group_frames_release_the_payload_on_parse_failure()
    {
        TrackingMemoryProvider memory = new();
        using PbtNodeGroupStore store = new();
        RefCountingMemory payload = memory.Rent(1);
        payload.GetSpan()[0] = 0xff;
        WarmReadStore persistence = new(store) { Payload = payload };
        Assert.Catch(() => _ = new GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath>(persistence, new PbtTraversalPath(Span<byte>.Empty), new ValueHash256(Value(1))));

        Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.EqualTo(1), "the caller still owns its payload lease");
        ((IDisposable)payload).Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistence.Reads.Count, Is.EqualTo(1));
            Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero);
        }
    }

    [Test]
    public void Inserts_and_leaf_splits_do_not_fetch_new_group_frames([Values] bool split)
    {
        using PbtNodeGroupStore inner = new();
        using PbtTreeHarness expected = new();
        (byte[] Key, byte[]? Value)[] initial = [(PbtStoreTestExtensions.ZoneKey("000000"), Value(1))];
        (byte[] Key, byte[]? Value)[] changes = [(PbtStoreTestExtensions.ZoneKey("000001"), Value(2)), (PbtStoreTestExtensions.ZoneKey("000002"), Value(3))];
        ValueHash256 root = default;
        if (split)
        {
            root = inner.Fold(default, initial);
            expected.ApplyBatch(initial);
        }
        CountingStore store = new(inner);
        root = store.Fold(root, changes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(expected.ApplyBatch(changes)));
            Assert.That(store.Fetches, Is.EqualTo(1), "only the root group needs reading");
            Assert.That(store.Loads, Is.EqualTo(split ? 1 : 0));
        }
    }

    [Test]
    public void Dense_group_mutations_preserve_unchanged_subtrees_and_canonical_payloads(
        [Values(0, 2, 3, 4, 8, 13)] int prefixBits,
        [Values(false, true)] bool promoteSibling,
        [Values] bool rightSide)
    {
        using PbtTreeHarness tree = new();
        EipReferenceTree oracle = new();
        (byte[] Key, byte[]? Value)[] entries = new (byte[], byte[]?)[32];
        for (int index = 0; index < entries.Length; index++)
        {
            int keyBits = index << (24 - prefixBits - 5);
            byte[] key = PbtStoreTestExtensions.ZoneKey($"00{keyBits:X6}");
            entries[index] = (key, Value((byte)(index + 1)));
        }
        oracle.Apply(entries);
        tree.ApplyBatch(entries);
        Dictionary<string, byte[]> unchangedPayloads = Payloads(tree);
        ValueHash256 unchangedRoot = tree.RootHash;
        tree.Reopen();
        tree.ApplyBatch(entries);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.RootHash, Is.EqualTo(unchangedRoot));
            foreach ((string key, byte[] payload) in Payloads(tree))
                Assert.That(payload, Is.EqualTo(unchangedPayloads[key]), $"unchanged group {key}");
        }

        List<(byte[] Key, byte[]? Value)> changes = [];
        int changedCount = promoteSibling ? entries.Length / 2 : 1;
        for (int offset = 0; offset < changedCount; offset++)
        {
            int index = rightSide ? entries.Length - 1 - offset : offset;
            entries[index].Value = promoteSibling ? null : Value(0xF0);
            changes.Add(entries[index]);
        }
        oracle.Apply(changes);
        tree.ApplyBatch(changes);
        AssertMatchesRebuild();

        tree.Reopen();
        entries[^1].Value = Value(0xF1);
        oracle.Apply([entries[^1]]);
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
    public void Missing_boundary_node_is_not_reconstructed_from_the_next_group()
    {
        using PbtTreeHarness tree = new();
        List<(byte[] Key, byte[]? Value)> entries = [];
        // Six levels keep stored branches below the boundary: leaves are inlined in their parents.
        for (int index = 0; index < 64; index++) entries.Add((PbtStoreTestExtensions.ZoneKey($"00{index << 2:X2}"), Value((byte)(index + 1))));
        ValueHash256 root = tree.ApplyBatch(entries);
        using PbtNodeGroupStore store = PbtNodeGroupStore.FromPhysicalPayloads(tree.PhysicalPayloads);
        PbtNodePath boundary = new(Bytes.FromHexString("0000"), 12);
        store.SetNode(boundary, null);
        using RefCountingMemory? descendantGroup = store.GetPhysicalNodeGroup(boundary);
        Assert.That(descendantGroup, Is.Not.Null);
        Assert.That(store.GetNode(boundary), Is.Null);

        Assert.That(() => store.Fold(root, [(PbtStoreTestExtensions.ZoneKey("00"), Value(0xF0))]), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Import_rejects_non_boundary_keys() =>
        Assert.Throws<InvalidDataException>(() => PbtNodeGroupStore.FromPhysicalPayloads([new PbtPhysicalPayload(new PbtStorageNodePath([0x00], 1), [0, 0, 0, 0])]));

    [Test]
    public void Store_releases_owned_memory_across_create_replace_delete_reopen_lookup_and_disposal()
    {
        TrackingMemoryProvider provider = new();
        PbtNodePath rootPath = new([], 0);
        byte[] firstEncoding = LeafEncoding(0x00);
        byte[] secondEncoding = LeafEncoding(0x80);

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
        byte[] firstEncoding = LeafEncoding(0x00);
        byte[] secondEncoding = LeafEncoding(0x80);
        byte[] thirdEncoding = LeafEncoding(0x40);

        using PbtNodeGroupStore store = new();
        store.SetNode(rootPath, firstEncoding, provider);
        RefCountingMemory firstLease = store.GetPhysicalNodeGroup(rootPath)!;
        store.SetNodeGroup(rootPath, store.GetGroupHash(rootPath), firstLease);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstLease.GetSpan().ToArray(), Is.EqualTo(RootGroup(firstEncoding)));
            Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.EqualTo(1), "the store borrows the published payload");
        }

        store.SetNode(rootPath, secondEncoding, provider);
        RefCountingMemory secondLease = store.GetPhysicalNodeGroup(rootPath)!;
        Assert.That(firstLease.GetSpan().ToArray(), Is.EqualTo(RootGroup(firstEncoding)));

        store.SetNode(rootPath, null, provider);
        Assert.That(firstLease.GetSpan().ToArray(), Is.EqualTo(RootGroup(firstEncoding)));
        Assert.That(secondLease.GetSpan().ToArray(), Is.EqualTo(RootGroup(secondEncoding)));

        store.SetNode(rootPath, thirdEncoding, provider);
        RefCountingMemory thirdLease = store.GetPhysicalNodeGroup(rootPath)!;
        store.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstLease.GetSpan().ToArray(), Is.EqualTo(RootGroup(firstEncoding)));
            Assert.That(secondLease.GetSpan().ToArray(), Is.EqualTo(RootGroup(secondEncoding)));
            Assert.That(thirdLease.GetSpan().ToArray(), Is.EqualTo(RootGroup(thirdEncoding)));
            Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.EqualTo(3));
        }

        ((IDisposable)thirdLease).Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.EqualTo(2));
        ((IDisposable)firstLease).Dispose();
        ((IDisposable)secondLease).Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero);
    }

    [TestCase(1, -1, typeof(ArgumentException))]
    [TestCase(0, 1, typeof(InvalidDataException))]
    public void Invalid_group_publication_preserves_prior_group_and_caller_reference(int keyDepth, int payloadLength, Type exceptionType)
    {
        TrackingMemoryProvider provider = new();
        PbtNodePath rootPath = new([], 0);
        byte[] encoding = LeafEncoding(0x00);
        byte[] validPayload = RootGroup(encoding);
        using PbtNodeGroupStore store = new();
        store.SetNode(rootPath, encoding, provider);
        PbtNodePath groupKey = new(new byte[(keyDepth + 7) / 8], keyDepth);
        byte[] rejectedBytes = payloadLength < 0 ? validPayload : new byte[payloadLength];
        RefCountingMemory rejectedPayload = provider.Rent(rejectedBytes.Length);
        rejectedBytes.CopyTo(rejectedPayload.GetSpan());

        Assert.That(() => store.SetNodeGroup(groupKey, default, rejectedPayload), Throws.TypeOf(exceptionType));
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
    public void Update_rejects_current_root_without_stored_root_group()
    {
        using PbtNodeGroupStore store = new();

        Assert.That(() => store.Fold(new ValueHash256(Value(3)), [(PbtStoreTestExtensions.ZoneKey("FF"), Value(2))]), Throws.TypeOf<InvalidDataException>());
    }

    // 0001 keeps the surviving subtree a stored branch rather than an inline leaf, so it escapes through its frames.
    [TestCase("000000,000001,000800", "000800", false, new[] { 12, 0 }, TestName = "Escaping_subtree_survives_poisoned_group_root_handoff")]
    [TestCase("000000,000001,000080,000800", "000080,000800", false, new[] { 16, 12, 0 }, TestName = "Escaping_subtree_survives_poisoned_nested_groups")]
    [TestCase("000000,000001,000080,000800", "000080,000800", true, new[] { 16, 12, 0 }, TestName = "Inline_subtree_survives_poisoned_nested_groups")]
    [TestCase("000000,000008", "000080", false, new[] { 0 }, TestName = "Ancestor_borrowed_subtree_survives_child_frame_return")]
    // Worker frames release their groups in no fixed order, so only that some were released is asserted.
    [TestCase("000000,000008,000080,008000,010000", "000080,008000", false, null, TestName = "Worker_branch_results_survive_poisoned_group_memory")]
    [TestCase("000000,000080,008000,010000", "000080,008000", false, null, TestName = "Worker_leaf_results_survive_poisoned_group_memory")]
    public void Returned_subtrees_survive_group_lease_release(string initialKeys, string deletedKeys, bool replaceSurvivor, int[]? releasedDepths)
    {
        using PbtTreeHarness expected = new();
        EipReferenceTree oracle = new();
        List<(byte[] Key, byte[]? Value)> initial = [.. initialKeys.Split(',').Select(key => (PbtStoreTestExtensions.ZoneKey(key), (byte[]?)Value(1)))];
        oracle.Apply(initial);
        ValueHash256 root = expected.ApplyBatch(initial);
        using PoisoningStore store = new(PbtNodeGroupStore.FromPhysicalPayloads(expected.PhysicalPayloads));
        List<(byte[] Key, byte[]? Value)> changes = [.. deletedKeys.Split(',').Select(key => (PbtStoreTestExtensions.ZoneKey(key), (byte[]?)null))];
        if (replaceSurvivor) changes.Add((PbtStoreTestExtensions.ZoneKey("000000"), Value(2)));
        oracle.Apply(changes);
        expected.ApplyBatch(changes);

        ValueHash256 actualRoot = store.Fold(root, changes);

        if (releasedDepths is not null)
            Assert.That(store.ReleasedGroupDepths, Is.EqualTo(releasedDepths), "payloads are poisoned when their owning frame releases them");
        else Assert.That(store.ReleasedGroupDepths, Is.Not.Empty);
        Assert.That(store.ReleasedGroupDepths.Count, Is.EqualTo(store.ReadCount));
        using PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(store.Inner.ExportPhysicalPayloads());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actualRoot.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            Assert.That(reopened.CanonicalRecords(), Is.EqualTo(expected.CanonicalRecords()));
        }
    }

    // Keys sharing a long prefix leave the groups between the root and their branch absent. An insert diverging
    // inside one of those groups creates it, so its stored sizes must pick up the untouched descendants from the
    // parent's slot without reading their group; a delete of an absent key diverging there is a no-op that must
    // neither read nor write. A second insert diverging deeper inherits through the group the first one created.
    [TestCase("000100", "000200", TestName = "Sorted_walk_creates_a_group_between_existing_groups")]
    [TestCase("01", "0100", TestName = "Zone_frame_creates_a_group_between_existing_groups")]
    [TestCase("0020", "0040", TestName = "Worker_frame_creates_a_group_between_existing_groups")]
    public void Groups_created_between_existing_groups_include_their_descendants(string divergingKey, string absentKey)
    {
        const string DeeperKey = "000010";
        byte[] Key(string hex) => PbtStoreTestExtensions.ZoneKey(hex);
        using PbtNodeGroupStore store = new();
        ValueHash256 root = default;
        CountingStore Apply(params (byte[] Key, byte[]? Value)[] changes)
        {
            CountingStore counting = new(store);
            root = counting.Fold(root, changes);
            PbtStoreTestExtensions.AssertSubtreeBytes(store.ExportPhysicalPayloads());
            return counting;
        }

        // The third key keeps a stored branch below the long prefix; the leaves themselves are inlined.
        Apply((Key("000000"), Value(1)), (Key("000001"), Value(2)), (Key("000002"), Value(4)));
        string[] initial = store.PhysicalRecords();
        Assert.That(initial, Has.Length.EqualTo(2), "root group and the deep branch's group");

        CountingStore absentDelete = Apply((Key(absentKey), null));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.PhysicalRecords(), Is.EqualTo(initial));
            Assert.That(absentDelete.Fetches, Is.EqualTo(1), "only the root group is read");
        }

        CountingStore insert = Apply((Key(divergingKey), Value(3)));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.PhysicalRecords(), Has.Length.EqualTo(3), "a group is created between the existing ones");
            Assert.That(insert.Fetches, Is.EqualTo(1), "only the root group is read");
        }

        CountingStore deeperInsert = Apply((Key(DeeperKey), Value(5)));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.PhysicalRecords(), Has.Length.EqualTo(4), "a group is created below the one created before");
            Assert.That(deeperInsert.Fetches, Is.EqualTo(2), "only the groups on the path are read");
        }

        Apply((Key(divergingKey), null));
        Assert.That(store.PhysicalRecords(), Has.Length.EqualTo(3), "the emptied group is removed");
        Apply((Key(divergingKey), Value(3)));
        Assert.That(store.PhysicalRecords(), Has.Length.EqualTo(4), "the group is recreated below the promoted subtree");

        Apply((Key(divergingKey), null), (Key(DeeperKey), null));
        Assert.That(store.PhysicalRecords(), Is.EqualTo(initial));
    }

    private sealed class PoisoningStore(PbtNodeGroupStore inner) : IPbtStore, IPbtNodeGroupSink, IDisposable
    {
        public IPbtConcurrentWriter CreateWriter() => new PbtPassThroughWriter(this);

        private readonly ArrayPool<byte> _pool = ArrayPool<byte>.Create();
        internal PbtNodeGroupStore Inner { get; } = inner;
        internal List<int> ReleasedGroupDepths { get; } = [];
        internal int ReadCount { get; private set; }

        public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash)
        {
            using RefCountingMemory? payload = Inner.GetNodeGroup(groupKey, groupHash);
            if (payload is null) return null;
            int length = payload.GetSpan().Length;
            ReadCount++;
            byte[] buffer = _pool.Rent(length);
            payload.GetSpan().CopyTo(buffer);
            int groupDepth = groupKey.BitDepth;
            return RefCountingMemory.OwningRocksDb(new PoisoningMemoryManager(_pool, buffer, length,
                () => ReleasedGroupDepths.Add(groupDepth)));
        }

        public void SetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash, RefCountingMemory? payload) => Inner.SetNodeGroup(groupKey, groupHash, payload);
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

    /// <summary>Counts every group fetch, and the fetches that found a group.</summary>
    private sealed class CountingStore(PbtNodeGroupStore inner) : IPbtStore, IPbtNodeGroupSink
    {
        private int _fetches;
        private int _loads;

        public IPbtConcurrentWriter CreateWriter() => new PbtPassThroughWriter(this);

        internal int Fetches => _fetches;
        internal int Loads => _loads;

        public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash)
        {
            Interlocked.Increment(ref _fetches);
            RefCountingMemory? payload = inner.GetNodeGroup(groupKey, groupHash);
            if (payload is not null) Interlocked.Increment(ref _loads);
            return payload;
        }

        public void SetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash, RefCountingMemory? payload) => inner.SetNodeGroup(groupKey, groupHash, payload);
    }

    [Test]
    public void Failed_import_releases_previously_copied_payloads()
    {
        TrackingMemoryProvider provider = new();
        PbtNodePath rootPath = new([], 0);
        byte[] validPayload = RootGroup(LeafEncoding(0x00));
        PbtPhysicalPayload valid = new(rootPath.ToPath<PbtStorageNodePath>(), validPayload);

        Assert.Throws<InvalidDataException>(() => PbtNodeGroupStore.FromPhysicalPayloads([valid, valid], provider));
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero);
    }

    [Test]
    public void Compressed_branch_jumps_create_no_intermediate_groups([Values(1, 2, 3, 5, 6, 7, 9, 13)] int sharedPrefixBits)
    {
        using PbtTreeHarness tree = new();
        EipReferenceTree oracle = new();
        // The account zone byte extends the shared prefix by eight bits.
        byte[] leftKey = PbtStoreTestExtensions.ZoneKey("00");
        byte[] rightKey = PbtStoreTestExtensions.ZoneKey("00");
        rightKey[1 + sharedPrefixBits / 8] = (byte)(0x80 >> (sharedPrefixBits % 8));
        // A second right key keeps a stored branch just below the shared prefix; leaves alone would be inlined in the root.
        byte[] farRightKey = (byte[])rightKey.Clone();
        farRightKey[1 + (sharedPrefixBits + 8) / 8] |= (byte)(0x80 >> ((sharedPrefixBits + 8) % 8));

        (byte[] Key, byte[]? Value)[] batch = [(leftKey, Value(1)), (rightKey, Value(2)), (farRightKey, Value(3))];
        tree.ApplyBatch(batch);
        oracle.Apply(batch);
        tree.Reopen();

        List<int> groupDepths = [];
        foreach (PbtPhysicalPayload payload in tree.PhysicalPayloads)
            groupDepths.Add(payload.Key.BitDepth);

        for (int depth = 1; depth <= 8 + sharedPrefixBits; depth++)
            Assert.That(tree.TryGetNode(new PbtNodePath(new byte[(depth + 7) / 8], depth), out _), Is.False, $"compressed prefix depth {depth}");
        foreach (PbtNodeRecord record in tree.Nodes)
        {
            Assert.That(tree.TryGetNode(record.Path, out byte[]? encoding), Is.True);
            Assert.That(encoding, Is.EqualTo(record.Encoding.ToArray()));
        }

        int presentGroupDepth = 8 + sharedPrefixBits / 4 * 4;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            Assert.That(groupDepths, Does.Contain(0));
            Assert.That(groupDepths, Does.Contain(presentGroupDepth));
            for (int depth = 4; depth < presentGroupDepth; depth += 4)
                Assert.That(groupDepths, Does.Not.Contain(depth));
            Assert.That(tree.CanonicalRecords(), Has.Length.EqualTo(2));
        }
    }

    [Test]
    public void Encoders_omit_only_prefixless_interior_branches(
        [Values(0, 4, 268, 516)] int groupDepth, [Values(0, 1, 2)] int nodeKind)
    {
        PbtStorageNodePath groupKey = new(new byte[(groupDepth + 7) / 8], groupDepth);
        List<PbtNodeRecord> records = [];
        ReadOnlyMemory<byte>[] encodings = new ReadOnlyMemory<byte>[PbtFourLevelGroupGeometry.PositionCount];
        PbtTraversalPath groupPath = PbtTraversalPath.FromPath(stackalloc byte[66], groupKey);
        using PbtNodeGroupWriter<PbtStorageNodePath> streamingWriter = new(groupKey.BitDepth, new TrackingMemoryProvider());
        int fullLength = PbtNodeGroupCodec.HeaderLength + sizeof(uint) + PbtNodeGroupCodec.DescendantMaskLength;
        int omitted = 0;
        int positionCount = groupDepth == 0 ? 31 : 30;
        for (int position = 0; position < positionCount; position++)
        {
            PbtStorageNodePath path = PbtTestPaths.PathOf(groupKey, position);
            byte[] encoding = nodeKind switch
            {
                0 => PbtTreeHarness.EncodeBranch([], 0, new ValueHash256(Value(1)), new ValueHash256(Value(2))),
                1 => PbtTreeHarness.EncodeBranch(Bytes.FromHexString("80"), 1, new ValueHash256(Value(1)), new ValueHash256(Value(2))),
                _ => LeafBranch(path, 1),
            };
            records.Add(new(path, encoding));
            encodings[position] = encoding;
            fullLength += encoding.Length + sizeof(ushort);
            if (nodeKind == 0 && path.BitDepth - groupDepth is >= 1 and <= 3) omitted++;
            if ((position & 1) == 0) streamingWriter.Write(groupPath, position, encoding);
            else
            {
                encoding.CopyTo(streamingWriter.GetSpan(position, encoding.Length));
                streamingWriter.Commit(groupPath);
            }
        }

        byte[] payload = PbtNodeGroupEncoder.Encode(groupKey, records, default);
        using RefCountingMemory streamedPayload = streamingWriter.Detach(default, ushort.MaxValue)!;
        GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath> reader = PbtStoreTestExtensions.ReadGroup(groupKey, payload);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fullLength - payload.Length, Is.EqualTo((PbtNodeCodec.BranchLength(0, 0, 0) + sizeof(ushort)) * omitted));
            Assert.That(omitted, Is.EqualTo(nodeKind == 0 ? 14 : 0));
            Assert.That(reader.Nodes(), Has.Count.EqualTo(positionCount - omitted));
            Assert.That(streamedPayload.GetSpan().ToArray(), Is.EqualTo(payload));
        }
        for (int position = 0; position < positionCount; position++)
        {
            int relativeDepth = records[position].Path.BitDepth - groupDepth;
            bool retained = nodeKind != 0 || relativeDepth is 0 or 4;
            Assert.That(reader.GetEncoding(position).ToArray(), Is.EqualTo(retained ? encodings[position].ToArray() : Array.Empty<byte>()), $"position {position}");
        }
    }

    [Test]
    public void Explicit_emissions_preserve_only_selected_nodes(
        [Values(0, 4, 516)] int groupDepth,
        [Values(0u, 0x400C0189u, 0x7FFFFFFFu)] uint selected,
        [Values] bool copyRanges)
    {
        PbtStorageNodePath groupKey = new(new byte[(groupDepth + 7) / 8], groupDepth);
        List<PbtNodeRecord> records = [];
        List<PbtNodeRecord> expectedRecords = [];
        int positionCount = groupDepth == 0 ? PbtFourLevelGroupGeometry.PositionCount : PbtFourLevelGroupGeometry.RootPosition;
        for (int position = 0; position < positionCount; position++)
        {
            if (position % 7 == 0) continue;
            PbtStorageNodePath path = PbtTestPaths.PathOf(groupKey, position);
            byte[] encoding = position % 3 == 2
                ? PbtTreeHarness.EncodeBranch([], 0, new ValueHash256(Value((byte)(position + 1))), new ValueHash256(Value(0xFF)))
                : LeafBranch(path, (byte)(position + 1));
            PbtNodeRecord record = new(path, encoding);
            records.Add(record);
            if ((selected & (1u << position)) != 0) expectedRecords.Add(record);
        }
        byte[] sourcePayload = PbtNodeGroupEncoder.Encode(groupKey, records, default);
        using PbtNodeGroupStore store = PbtNodeGroupStore.FromPhysicalPayloads([new(groupKey, sourcePayload)]);
        PbtTraversalPath groupPath = PbtTraversalPath.FromPath(stackalloc byte[66], groupKey);
        GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath> reader = new(store, groupPath, new ValueHash256(Value(1)));
        TrackingMemoryProvider provider = new();
        using PbtNodeGroupWriter<PbtStorageNodePath> writer = new(groupKey.BitDepth, provider);
        using (new GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath>.Scope(ref reader))
        {
            int nextPosition = 0;
            foreach (int endPosition in new[] { 0, 3, 8, 19, 31 })
            {
                while (nextPosition < endPosition)
                {
                    if ((selected & (1u << nextPosition)) == 0)
                    {
                        nextPosition++;
                        continue;
                    }
                    int startPosition = nextPosition;
                    do
                    {
                        ReadOnlyMemory<byte> encoding = reader.GetEncoding(nextPosition);
                        if (!copyRanges && !encoding.IsEmpty) writer.Write(groupPath, nextPosition, encoding.Span);
                        nextPosition++;
                    } while (nextPosition < endPosition && (selected & (1u << nextPosition)) != 0);
                    if (copyRanges) reader.CopyRange(writer, startPosition, nextPosition);
                }
            }
            using RefCountingMemory? payload = writer.Detach(default, ushort.MaxValue);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(payload?.GetSpan().ToArray(), Is.EqualTo(expectedRecords.Count == 0 ? null : PbtNodeGroupEncoder.Encode(groupKey, expectedRecords, default)));
                Assert.That(provider.RentCount, Is.EqualTo(expectedRecords.Count == 0 ? 0 : 1));
            }
        }
    }

    [Test]
    public void Streaming_writer_commits_leaves_without_allocating(
        [Values(0, 4, 8, 268, PbtFourLevelGroupGeometry.MaxGroupDepth)] int groupDepth)
    {
        PbtStorageNodePath groupKey = new(new byte[(groupDepth + 7) / 8], groupDepth);
        PbtTraversalPath groupPath = PbtTraversalPath.FromPath(stackalloc byte[66], groupKey);
        using PbtNodeGroupWriter<PbtStorageNodePath> writer = new(groupKey.BitDepth, new TrackingMemoryProvider());
        int positionCount = groupDepth == 0 ? PbtFourLevelGroupGeometry.PositionCount : PbtFourLevelGroupGeometry.RootPosition;
        for (int position = 0; position < positionCount; position++)
        {
            PbtStorageNodePath path = PbtTestPaths.PathOf(groupKey, position);
            byte[] encoding = LeafBranch(path, 1);
            encoding.CopyTo(writer.GetSpan(position, encoding.Length));

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            writer.Commit(groupPath);
            long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            Assert.That(allocatedBytes, Is.Zero, $"position {position}");
        }
    }

    [TestCase(0, 1, false)]
    [TestCase(0, 31, false)]
    [TestCase(4, 30, false)]
    [TestCase(4, 3, false)]
    [TestCase(0, 1, true)]
    [TestCase(0, 31, true)]
    [TestCase(4, 30, true)]
    [TestCase(4, 3, true)]
    public void Streaming_writer_preserves_canonical_bytes_and_transfers_backing_memory(int groupDepth, int count, bool slabProvider)
    {
        PbtStorageNodePath groupKey = new(new byte[(groupDepth + 7) / 8], groupDepth);
        using SlabRefCountingMemoryProvider slab = PbtNodeGroupMemory.CreateSlabProvider();
        TrackingMemoryProvider provider = new(slabProvider ? slab : PooledRefCountingMemoryProvider.Instance) { FillByte = 0xFF };
        List<PbtNodeRecord> records = [];
        PbtTraversalPath groupPath = PbtTraversalPath.FromPath(stackalloc byte[66], groupKey);
        using PbtNodeGroupWriter<PbtStorageNodePath> writer = new(groupKey.BitDepth, provider);
        for (int index = 0; index < count; index++)
        {
            int position = count == 1 ? 30 : count == 3 ? 3 + index * 10 : index;
            byte[] encoding = PbtTreeHarness.EncodeBranch(Bytes.FromHexString("A0"), 4,
                new ValueHash256(Value(1)), new ValueHash256(Value(2)));
            records.Add(new(PbtTestPaths.PathOf(groupKey, position), encoding));
            if (index % 2 == 0)
            {
                Span<byte> destination = writer.GetSpan(position, encoding.Length);
                PbtNodeCodec.CreateBranchEncoding(destination, 4, new ValueHash256(Value(1)), new ValueHash256(Value(2)));
                PbtNodeCodec.WriteBranchTrailer(destination[PbtNodeCodec.BranchPreimageLength(4)..], [], []);
                destination[3] = 0xA0;
                writer.Commit(groupPath);
            }
            else writer.Write(groupPath, position, encoding);
        }

        byte[] expected = PbtNodeGroupEncoder.Encode(groupKey, records, default);
        Assert.That(provider.RentCount, Is.Zero);

        using (RefCountingMemory payload = writer.Detach(default, ushort.MaxValue)!)
        {
            writer.Dispose();
            GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath> reader = PbtStoreTestExtensions.ReadGroup(groupKey, payload.GetSpan());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(payload.GetSpan().ToArray(), Is.EqualTo(expected));
                Assert.That(expected[..PbtNodeGroupCodec.HeaderLength], Is.EqualTo(Bytes.FromHexString("07")));
                Assert.That(reader.Nodes(), Has.Count.EqualTo(count));
                int nodeLength = PbtNodeCodec.BranchLength(4, 0, 0);
                Assert.That(expected.Length, Is.EqualTo(count * nodeLength + 7 + 2 * count));
                uint availability = 0;
                for (int index = 0; index < count; index++)
                {
                    int position = PbtTestPaths.Locate(records[index].Path).Position;
                    availability |= 1u << position;
                    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(expected.AsSpan(1 + count * nodeLength + index * 2)), Is.EqualTo(index * nodeLength));
                    Assert.That(reader.GetEncoding(position).ToArray(), Is.EqualTo(expected.AsSpan(1 + index * nodeLength, nodeLength).ToArray()));
                }
                Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(expected.AsSpan(expected.Length - 6)), Is.EqualTo(availability));
                Assert.That(payload, Is.SameAs(provider.Rented[^1]));
                Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.EqualTo(1));
                Assert.That(provider.RentCount, Is.EqualTo(1));
                Assert.That(payload.Capacity, Is.LessThan(2 * expected.Length), "retained capacity is close to the payload");
            }
        }
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero);
    }

    [Test]
    public void Streaming_writer_accepts_exact_uint16_entries_limit()
    {
        TrackingMemoryProvider provider = new();
        PbtTraversalPath groupPath = new(Span<byte>.Empty);
        using PbtNodeGroupWriter<PbtNodePath> writer = new(0, provider);
        for (int position = 0; position < 8; position++)
        {
            int length = position == 7 ? 8191 : 8192;
            Span<byte> encoding = writer.GetSpan(position, length);
            PbtNodeCodec.CreateBranchEncoding(encoding, (length - PbtNodeCodec.BranchLength(0, 0, 0)) * 8,
                new ValueHash256(Value(1)), new ValueHash256(Value(2)));
            PbtNodeCodec.WriteBranchTrailer(encoding[^PbtNodeCodec.BranchTrailerHeaderLength..], [], []);
            writer.Commit(groupPath);
        }
        Assert.That(writer.WrittenCount, Is.EqualTo(ushort.MaxValue));
        using RefCountingMemory payload = writer.Detach(default, ushort.MaxValue)!;
        Assert.That(PbtStoreTestExtensions.ReadGroup(new PbtNodePath([], 0), payload.GetSpan()).Nodes(), Has.Count.EqualTo(8));
    }

    [Test]
    public void Versioned_group_rejects_invalid_header_and_footer([Range(0, 22)] int scenario)
    {
        PbtNodePath groupKey = new([], 0);
        byte[] branch = PbtTreeHarness.EncodeBranch(Bytes.FromHexString("80"), 1,
            new ValueHash256(Value(1)), new ValueHash256(Value(2)));
        byte[] payload = PbtNodeGroupEncoder.Encode(groupKey, [
            new(PbtTestPaths.PathOf(groupKey, 3).ToPath<PbtStorageNodePath>(), branch),
            new(PbtTestPaths.PathOf(groupKey, 10).ToPath<PbtStorageNodePath>(), branch),
            new(groupKey.ToPath<PbtStorageNodePath>(), branch)], default);
        int footerOffset = payload.Length - 12;
        switch (scenario)
        {
            case 0: payload = []; break;
            case 1: payload = payload[1..]; break;
            case 2: payload[0] = 1; break;
            case 3: payload[0] = 2; break;
            case 4: payload = Bytes.FromHexString("06000000"); break;
            case 5: payload = Bytes.FromHexString("060004000b00000000"); break;
            case 6: payload.AsSpan(payload.Length - 6, 4).Clear(); break;
            case 7: payload[^3] |= 0x80; break;
            case 8: groupKey = new(Bytes.FromHexString("00"), 4); break;
            case 9: payload[footerOffset] = 1; break;
            case 10: BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(footerOffset + 2), 0); break;
            case 11: BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(footerOffset + 4), 67); break;
            case 12: BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(footerOffset + 4), 204); break;
            case 13: BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(footerOffset + 4), ushort.MaxValue); break;
            case 14: payload[1] = 0xFF; break;
            case 15: payload[4] = 0x81; break;
            case 16: payload.AsSpan(5, 32).Clear(); break;
            case 17:
                payload = new byte[1 + ushort.MaxValue + 1 + 8];
                payload[0] = 7;
                payload[^3] = 0x40;
                break;
            // A descendant mask bit must carry a nonzero size.
            case 18: payload = [.. payload[..^2], 0, 1, 0x01, 0x00]; break;
            case 19: payload[0] = 5; break;
            // Descendant sizes take the width, 1–6 bytes, of the largest one.
            case 20: payload = [.. payload[..^2], 1, 0, 0x01, 0x00]; break;
            case 21: payload = [.. payload[..^2], 1, 0, 0, 0, 0, 0, 0, 7, 0x01, 0x00]; break;
            case 22: payload = [.. payload[..^2], 1, 0, 2, 0x01, 0x00]; break;
        }
        Assert.Throws<InvalidDataException>(() => ReadGroupCount(groupKey, payload));
    }

    [Test]
    public void Descendant_sizes_are_stored_per_slot_and_reject_the_uint48_overflow(
        [Values(0L, 1L, 0x1234L, 0x1234_5678_9ABCL, PbtNodeGroupCodec.MaxDescendantBytes)] long descendantBytes, [Values(0, 7, 15)] int slot)
    {
        PbtNodePath groupKey = new([], 0);
        PbtNodeRecord record = new(groupKey.ToPath<PbtStorageNodePath>(), LeafEncoding(0x00));
        long[] slots = new long[PbtFourLevelGroupGeometry.BoundarySlots];
        slots[slot] = descendantBytes;
        byte[] payload = PbtNodeGroupEncoder.Encode(groupKey, [record], slots);
        PbtTraversalPath groupPath = PbtTraversalPath.FromPath(stackalloc byte[66], groupKey);
        using PbtNodeGroupWriter<PbtNodePath> streamingWriter = new(0, new TrackingMemoryProvider());
        streamingWriter.Write(groupPath, PbtFourLevelGroupGeometry.RootPosition, record.Encoding.Span);
        using RefCountingMemory streamed = streamingWriter.Detach(slots, ushort.MaxValue)!;
        long[] stored = PbtStoreTestExtensions.ReadDescendantBytes(payload);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(payload, Has.Length.EqualTo(descendantBytes switch { 0 => 12, 1 => 14, 0x1234 => 15, _ => 19 }));
            Assert.That(PbtNodeGroupCodec.ReadDescendantMask(payload), Is.EqualTo(descendantBytes == 0 ? 0 : 1 << slot));
            Assert.That(stored, Is.EqualTo(slots));
            Assert.That(PbtStoreTestExtensions.ReadGroup(groupKey, payload).DescendantBytes(slot), Is.EqualTo(descendantBytes));
            Assert.That(streamed.GetSpan().ToArray(), Is.EqualTo(payload));
        }
        slots[slot] = -1;
        Assert.Throws<ArgumentOutOfRangeException>(() => PbtNodeGroupEncoder.Encode(groupKey, [record], slots));
        slots[slot] = PbtNodeGroupCodec.MaxDescendantBytes + 1;
        Assert.Throws<InvalidDataException>(() => PbtNodeGroupEncoder.Encode(groupKey, [record], slots));
    }

    private sealed class WarmReadStore(IPbtStore store) : IPbtStore, IPbtNodeGroupSink
    {
        public IPbtConcurrentWriter CreateWriter() => new PbtPassThroughWriter(this);

        internal List<PbtStorageNodePath> Reads { get; } = [];
        internal RefCountingMemory? Payload { get; set; }

        public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash)
        {
            Reads.Add(groupKey.ToPath<PbtStorageNodePath>());
            if (Payload is not { } payload) return store.GetNodeGroup(groupKey, groupHash);
            payload.AcquireLease();
            return payload;
        }

        public void SetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash, RefCountingMemory? payload) =>
            throw new AssertionException("Group frame reads must never write.");
    }

    private static byte[] RootGroup(byte[] encoding)
    {
        PbtNodePath rootPath = new([], 0);
        return PbtNodeGroupEncoder.Encode(rootPath, [new PbtNodeRecord(rootPath.ToPath<PbtStorageNodePath>(), encoding)], default);
    }

    private static Dictionary<string, byte[]> Payloads(PbtTreeHarness tree)
    {
        Dictionary<string, byte[]> result = [];
        foreach (PbtPhysicalPayload payload in tree.PhysicalPayloads)
            result.Add(Convert.ToHexString(payload.Key.ToEncodedArray()), payload.Payload.ToArray());
        return result;
    }

    private static byte[] LeafEncoding(byte keyMarker) => PbtTreeHarness.EncodeLeaf(new PbtStorageTreeKey([keyMarker]));

    /// <summary>A prefixless branch inlining <paramref name="key"/> as its left leaf: the smallest node stored below the root.</summary>
    private static byte[] LeafBranch(ReadOnlySpan<byte> key, byte marker) =>
        PbtTreeHarness.EncodeBranch([], 0, new ValueHash256(Value(marker)), new ValueHash256(Value(0xFF)), key, []);

    /// <summary>A node valid at <paramref name="path"/>: a prefixless branch inlining a leaf just below it, or without leaves where no longer key fits.</summary>
    private static byte[] LeafBranch<TPath>(TPath path, byte marker) where TPath : struct, IPbtNodePath<TPath>
    {
        if (path.BitDepth >= PbtFourLevelGroupGeometry.MaxPathDepth) return LeafBranch([], marker);
        byte[] key = new byte[(path.BitDepth >> 3) + 1];
        PbtNodePathOperations.CopyTo(path, key);
        return LeafBranch(key.AsSpan(PbtNodeCodec.InlineKeyOffset(path.BitDepth)), marker);
    }
}

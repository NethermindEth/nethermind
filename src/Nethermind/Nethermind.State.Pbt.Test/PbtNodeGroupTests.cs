// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.State.Pbt.NodeBuffer;
using NUnit.Framework;
using static Nethermind.State.Pbt.Test.PbtStoreTestExtensions;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class PbtNodeGroupTests
{
    [Test]
    public void Store_identity_and_reader_payload_survive_cursor_mutation([Values] bool storage, [Values(0, 3, -1)] int groupDepth)
    {
        int capacity = storage ? PbtVariableTreeKey.MaxLength : PbtPath.KeyLength;
        int depth = groupDepth < 0 ? (capacity * 8 - 1) / PbtThreeLevelGroupGeometry.LevelsPerGroup * PbtThreeLevelGroupGeometry.LevelsPerGroup : groupDepth;
        byte[] key = Bytes.FromHexString(new string('D', capacity * 2));
        PbtStorageNodePath groupKey = PbtTestPaths.Prefix<PbtStorageNodePath>(key, depth);
        PbtStorageNodePath leafPath = PbtTestPaths.Prefix<PbtStorageNodePath>(key, depth == 0 ? 0 : depth + 1);
        byte[] encoding = LeafBranch(leafPath, 1);
        byte[] bytes = PbtNodeGroupEncoder.Encode(groupKey, [new PbtNodeRecord(leafPath, encoding)], default);
        using PbtNodeGroupStore store = new();
        PbtTraversalPath cursor = PbtTraversalPath.FromPath(stackalloc byte[capacity], groupKey);
        store.SetNodeGroup(cursor, bytes);
        GroupFrameReader<PbtVariableTreeKey, PbtStorageNodePath> reader = PbtStoreTestExtensions.ReadGroup(cursor, store.GetNodeGroup(cursor)!);
        cursor.Truncate(0);
        cursor.AppendMut(0);
        byte[]? retained = store.GetNodeGroup(groupKey);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.GetEncoding(PbtTestPaths.Locate(leafPath).Position).ToArray(), Is.EqualTo(encoding));
            Assert.That(store.EnumerateNodeGroupKeys(), Is.EqualTo(new[] { groupKey }));
            Assert.That(retained, Is.EqualTo(bytes));
        }
        Assert.That(reader.Nodes().Select(static node => (node.Position, node.Encoding.ToArray())),
            Is.EqualTo(new[] { (PbtTestPaths.Locate(leafPath).Position, encoding) }));
    }

    [Test]
    public void Node_group_paths_pack_internal_node_geometry_into_one_byte([Range(0, 3)] int length)
    {
        for (int prefix = 0; prefix < 1 << length; prefix++)
        {
            int slot = prefix << (3 - length);
            NodeGroupPath path = new(slot, length);
            PbtNodePath nodePath = PbtTestPaths.Prefix<PbtNodePath>([(byte)(slot << 5)], length);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Unsafe.SizeOf<NodeGroupPath>(), Is.EqualTo(1));
                Assert.That(path.Slot, Is.EqualTo(slot));
                Assert.That(path.Length, Is.EqualTo(length));
                Assert.That(path.Width, Is.EqualTo(8 >> length));
                Assert.That(path.Position, Is.EqualTo(PbtTestPaths.Locate(nodePath).Position));
                Assert.That(PbtThreeLevelGroupGeometry.LocalPathOf(path.Position), Is.EqualTo(path));
                if (length < 3)
                {
                    Assert.That(path.Left.Slot, Is.EqualTo(slot));
                    Assert.That(path.Right.Slot, Is.EqualTo(slot + (4 >> length)));
                    Assert.That(path.Left.Length, Is.EqualTo(length + 1));
                    Assert.That(path.Right.Length, Is.EqualTo(length + 1));
                }
            }
        }
    }

    [Test]
    public void Boundary_slot_reads_three_bits_across_bytes_and_zero_past_the_key_end([Range(0, 15)] int depth)
    {
        byte[] key = Bytes.FromHexString("b55a");
        int expected = 0;
        for (int bit = depth; bit < depth + PbtThreeLevelGroupGeometry.LevelsPerGroup; bit++)
            expected = expected << 1 | (bit < key.Length * 8 ? TrieUpdater.GetBit(key, bit) : 0);
        Assert.That(TrieUpdater.BoundarySlot(key, depth), Is.EqualTo(expected));
    }

    [TestCase(0)]
    [TestCase(3)]
    [TestCase(267)]
    public void Small_and_storage_paths_share_identity_and_accept_wide_leaf_payloads(int depth)
    {
        byte[] keyBytes = new byte[PbtVariableTreeKey.MaxLength];
        keyBytes[0] = Eip8297KeyDerivation.StorageZone;
        PbtVariableTreeKey storageKey = new(keyBytes);
        PbtNodePath smallPath = PbtTestPaths.Prefix<PbtNodePath>(keyBytes, depth);
        PbtStorageNodePath storagePath = PbtTestPaths.Prefix<PbtStorageNodePath>(storageKey.Bytes, depth);
        Dictionary<PbtStorageNodePath, int?> entries = new() { [smallPath.ToPath<PbtStorageNodePath>()] = 1 };
        entries[storagePath] = null;
        PbtStorageNodePath leafPath = PbtTestPaths.Prefix<PbtStorageNodePath>(storageKey.Bytes, depth == 0 ? 0 : depth + 3);
        byte[] encoding = LeafBranch(keyBytes.AsSpan(PbtNodeCodec.InlineKeyOffset(leafPath.BitDepth)), 1);
        byte[] payload = PbtNodeGroupEncoder.Encode(smallPath, [new PbtNodeRecord(leafPath, encoding)], default);
        GroupFrameReader<PbtVariableTreeKey, PbtStorageNodePath> reader = PbtStoreTestExtensions.ReadGroup(storagePath, payload);

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
        store.SetNodeGroup(smallPath, payload);
        Assert.That(store.GetPhysicalNodeGroup(storagePath), Is.EqualTo(payload));
        store.SetNodeGroup(storagePath, null);
        Assert.That(store.GetPhysicalNodeGroup(smallPath), Is.Null);
        store.SetNodeGroup(storagePath, payload);
        Assert.That(store.GetPhysicalNodeGroup(smallPath), Is.EqualTo(payload));
        store.SetNodeGroup(smallPath, null);
        Assert.That(store.GetPhysicalNodeGroup(storagePath), Is.Null);
    }

    [TestCase(34, 272)]
    [TestCase(66, 528)]
    public void Key_families_enforce_capacity_without_changing_storage_bytes(int length, int depth)
    {
        Assert.That(Unsafe.SizeOf<PbtPath>(), Is.EqualTo(34));
        Assert.That(Unsafe.SizeOf<PbtStoragePath>(), Is.EqualTo(66));
        Assert.That(Unsafe.SizeOf<PbtVariableTreeKey>(), Is.EqualTo(72));
        byte[] keyBytes = new byte[length];
        keyBytes[^1] = 1;
        PbtVariableTreeKey storageKey = new(keyBytes);
        Assert.That(storageKey.FirstDifferingBit(new PbtVariableTreeKey(new byte[length]), 0), Is.EqualTo(depth - 1));
        if (length == PbtPath.KeyLength)
        {
            PbtPath key = (PbtPath)storageKey;
            Assert.That(key.FirstDifferingBit(new PbtPath(new byte[length]), 0), Is.EqualTo(depth - 1));
            Assert.That(((PbtVariableTreeKey)key).Bytes.ToArray(), Is.EqualTo(keyBytes));
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
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtVariableTreeKey([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtVariableTreeKey(new byte[67]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtStorageNodePath(new byte[67], 529));
    }

    [TestCase(0x00, "080001000000004000")]
    [TestCase(0x80, "080001800000004000")]
    public void Root_node_round_trip_preserves_encoding_and_allows_root_position_access(byte keyByte, string expectedPayloadHex)
    {
        PbtNodePath rootPath = new([], 0);
        byte[] encoding = PbtTreeHarness.EncodeLeaf(new PbtVariableTreeKey([keyByte]));

        byte[] payload = RootGroup(encoding);
        GroupFrameReader<PbtVariableTreeKey, PbtStorageNodePath> group = PbtStoreTestExtensions.ReadGroup(rootPath, payload);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(payload, Is.EqualTo(Bytes.FromHexString(expectedPayloadHex)));
            Assert.That(group.GetEncoding(PbtThreeLevelGroupGeometry.RootPosition).ToArray(), Is.EqualTo(encoding));
            Assert.That(group.Nodes(), Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void Reader_and_writer_validate_every_required_leaf_bit_and_ignore_suffix_bits(
        [Values(0, 3, 6, 9, 12, 15, 30, 252, 255, 510)] int groupDepth, [Values] bool streamingWriter)
    {
        byte[] groupBytes = new byte[(groupDepth + 7) / 8];
        groupBytes.AsSpan().Fill(0xA5);
        if ((groupDepth & 7) != 0) groupBytes[^1] &= (byte)(0xFF << (8 - (groupDepth & 7)));
        PbtStorageNodePath groupKey = new(groupBytes, groupDepth);

        for (int position = 0; position < PbtThreeLevelGroupGeometry.PositionCount; position++)
        {
            if (groupDepth != 0 && position == PbtThreeLevelGroupGeometry.RootPosition) continue;
            PbtStorageNodePath path = PbtTestPaths.PathOf(groupKey, position);
            byte[] key = new byte[(path.BitDepth + 7) / 8 + 1];
            PbtNodePathOperations.CopyTo(path, key);
            bool rootLeaf = position == PbtThreeLevelGroupGeometry.RootPosition;
            // An inline key omits the group path's whole bytes, whose bits the group key implies.
            byte[] storedKey = rootLeaf ? key : key[PbtNodeCodec.InlineKeyOffset(path.BitDepth)..];
            int omittedBits = (key.Length - storedKey.Length) * 8;
            byte[] encoding = rootLeaf ? PbtTreeHarness.EncodeLeaf(new PbtVariableTreeKey(key)) : LeafBranch(storedKey, 1);
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

    // Unless the leaf below a group's boundary slot falls in the next byte, a one-byte postfix already reaches it, and an empty one declares a branch child.
    [Test]
    public void Reader_and_writer_reject_leaf_keys_shorter_than_required_path(
        [Values(15, 21, 255, 510)] int groupDepth, [Values] bool streamingWriter)
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
        PbtNodeGroupCodec.WriteFooter(shortPayload.AsSpan(PbtNodeGroupCodec.HeaderLength + shortEncoding.Length), stackalloc ushort[PbtThreeLevelGroupGeometry.PositionCount], 1u, 0, default);
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
        using PbtNodeGroupWriter<TPath> writer = new(groupKey.BitDepth);
        writer.Write(groupPath, position, payload.AsSpan(PbtNodeGroupCodec.HeaderLength, payload.Length - PbtNodeGroupCodec.HeaderLength - PbtNodeGroupCodec.GetTrailerLength(1, 0, default)));
        byte[] writtenPayload = writer.Detach(default, 0)!;
        Assert.That(writtenPayload, Is.EqualTo(payload));
        return 1;
    }

    private static int ReadGroupCount<TPath>(TPath groupKey, byte[] payload) where TPath : struct, IPbtNodePath<TPath> => PbtStoreTestExtensions.ReadGroup(groupKey, payload).Nodes().Count;

    [Test]
    public void Node_groups_partition_nodes_at_three_level_boundaries_and_preserve_siblings()
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
        AbsentGroupFrame<PbtVariableTreeKey, PbtStorageNodePath> frame = inherited ? new(9, 5, spanningBytes) : new(9, 0, 0);
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
    public void Group_frames_load_once_at_construction([Values] bool present, [Values(0, 3, 270, 525)] int groupDepth)
    {
        using PbtNodeGroupStore store = new();
        byte[] key = Bytes.FromHexString(new string('D', PbtVariableTreeKey.MaxLength * 2));
        PbtStorageNodePath groupKey = PbtTestPaths.Prefix<PbtStorageNodePath>(key, groupDepth);
        PbtStorageNodePath leafPath = PbtTestPaths.Prefix<PbtStorageNodePath>(key, groupDepth == 0 ? 0 : groupDepth + 3);
        int position = PbtTestPaths.Locate(leafPath).Position;
        byte[] encoding = LeafBranch(leafPath, 1);
        if (present) store.SetNode(leafPath, encoding);
        WarmReadStore persistence = new(store);
        PbtTraversalPath groupPath = PbtTraversalPath.FromPath(stackalloc byte[66], groupKey);
        using PbtNodeGroupWriter<PbtStorageNodePath> writer = new(groupKey.BitDepth);
        byte[]? groupPayload = persistence.GetNodeGroup(groupPath);
        Assert.That(groupPayload is not null, Is.EqualTo(present));
        GroupFrameReader<PbtVariableTreeKey, PbtStorageNodePath> reader = groupPayload is null ? default : PbtStoreTestExtensions.ReadGroup(groupPath, groupPayload);
        Assert.That(persistence.Reads, Is.EqualTo(new[] { groupKey }));
        if (present)
        {
            reader.CopyRange(writer, 0, PbtThreeLevelGroupGeometry.PositionCount);
            Assert.That(reader.GetEncoding(position).ToArray(), Is.EqualTo(encoding));
            Assert.That(persistence.Reads, Has.Count.EqualTo(1), "a frame reads its group once");
        }
    }

    [Test]
    public void Group_frames_materialize_only_branch_anchors(
        [Values(0, 3, 6, 9, 12, 15, 243)] int groupDepth, [Range(0, 13)] int position, [Values] bool inlineLeaf)
    {
        using PbtNodeGroupStore store = new();
        PbtStorageNodePath groupKey = PbtTestPaths.Prefix<PbtStorageNodePath>(Bytes.FromHexString(new string('A', 62)), groupDepth);
        PbtStorageNodePath path = PbtTestPaths.PathOf(groupKey, position);
        byte[] key = new byte[32];
        PbtNodePathOperations.CopyTo(path, key);
        // The group stores the inline key past its path's whole bytes; a group off a byte boundary keeps its partial byte.
        byte[] encoding = PbtTreeHarness.EncodeBranch(Bytes.FromHexString("A0"), 4, new ValueHash256(Value(1)), new ValueHash256(Value(2)), inlineLeaf ? key[(groupDepth >> 3)..] : [], []);
        store.SetNode(path, encoding);
        PbtTraversalPath groupPath = PbtTraversalPath.FromPath(stackalloc byte[66], groupKey);
        GroupFrameReader<PbtVariableTreeKey, PbtStorageNodePath> reader = PbtStoreTestExtensions.ReadGroup(groupPath, store.GetNodeGroup(groupPath)!);
        TrieUpdater<PbtVariableTreeKey, PbtStorageNodePath>.BoundaryNode source = TrieUpdater<PbtVariableTreeKey, PbtStorageNodePath>.BoundaryNode.StoredAt(ref reader, position, default);
        Assert.That(source.AnchorDepth, Is.EqualTo(path.BitDepth));
        if (inlineLeaf)
        {
            TrieUpdater<PbtVariableTreeKey, PbtStorageNodePath>.BoundaryNode leaf = TrieUpdater<PbtVariableTreeKey, PbtStorageNodePath>.BoundaryNode.InlineLeafAt(ref reader, position, right: false);
            PbtVariableTreeKey leafKey = leaf.LeafKey(groupPath);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(leaf.KeyOffset, Is.EqualTo(groupDepth >> 3));
                Assert.That(leafKey.Bytes.ToArray(), Is.EqualTo(key));
            }
        }
    }

    [Test]
    public void Inlined_leaf_boundary_nodes_read_through_the_branch_that_holds_them([Values] bool right, [Values] bool stored, [Values(0, 12)] int anchorDepth)
    {
        byte[] key = Bytes.FromHexString("1234");
        ValueHash256 leafHash = PbtTreeHarness.HashLeaf(key, Value(1));
        ValueHash256 siblingHash = new(Value(2));
        // A branch anchored at depth 12 lies in group 9, whose path byte its inline keys omit.
        byte[] keyPostfix = key[PbtNodeCodec.InlineKeyOffset(anchorDepth)..];
        byte[] encoding = stored
            ? PbtTreeHarness.EncodeLeaf(new PbtVariableTreeKey(key))
            : right
                ? PbtTreeHarness.EncodeBranch([], 0, siblingHash, leafHash, [], keyPostfix)
                : PbtTreeHarness.EncodeBranch([], 0, leafHash, siblingHash, keyPostfix, []);
        PbtTraversalPath cursor = new(stackalloc byte[66]);
        cursor.AppendKey(key, anchorDepth);
        TrieUpdater<PbtVariableTreeKey, PbtStorageNodePath>.BoundaryNode borrowed = stored
            ? new(encoding, leafHash)
            : new(encoding, anchorDepth, right);
        PbtVariableTreeKey borrowedKey = borrowed.LeafKey(cursor);
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
            Assert.That(Unsafe.SizeOf<TrieUpdater<PbtVariableTreeKey, PbtStorageNodePath>.BoundaryNode>(),
                Is.EqualTo(Unsafe.SizeOf<TrieUpdater<PbtPath, PbtNodePath>.BoundaryNode>()));
            Assert.That(Unsafe.SizeOf<TrieUpdater<PbtVariableTreeKey, PbtStorageNodePath>.BoundaryNode>(), Is.LessThan(72));
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
            Assert.That(store.Fetches, Is.EqualTo(split ? 1 : 0), "only a non-empty tree's root group needs reading");
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
        byte[]? descendantGroup = store.GetPhysicalNodeGroup(boundary);
        Assert.That(descendantGroup, Is.Not.Null);
        Assert.That(store.GetNode(boundary), Is.Null);

        Assert.That(() => store.Fold(root, [(PbtStoreTestExtensions.ZoneKey("00"), Value(0xF0))]), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Update_rejects_current_root_without_stored_root_group()
    {
        using PbtNodeGroupStore store = new();

        Assert.That(() => store.Fold(new ValueHash256(Value(3)), [(PbtStoreTestExtensions.ZoneKey("FF"), Value(2))]), Throws.TypeOf<InvalidDataException>());
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

    /// <summary>Counts every group fetch, and the fetches that found a group.</summary>
    private sealed class CountingStore(PbtNodeGroupStore inner) : IPbtStore
    {
        private int _fetches;
        private int _loads;

        public IPbtGroupRecordWriteBatch StartWriteBatch(int alignedTotalLength) => inner.StartWriteBatch(alignedTotalLength);
        public bool TryLocate(ulong pointer, out PbtGroupRecord record)
        {
            record = default;
            return false;
        }
        public bool TryLoad<TArg>(scoped in PbtTraversalPath path, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct =>
            PbtNodeGroupStore.TryLoadPayload(GetNodeGroup(path), action, arg);

        internal int Fetches => _fetches;
        internal int Loads => _loads;

        public byte[]? GetNodeGroup(scoped in PbtTraversalPath groupKey)
        {
            Interlocked.Increment(ref _fetches);
            byte[]? payload = inner.GetNodeGroup(groupKey);
            if (payload is not null) Interlocked.Increment(ref _loads);
            return payload;
        }
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

        int presentGroupDepth = (8 + sharedPrefixBits) / 3 * 3;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            Assert.That(groupDepths, Does.Contain(0));
            Assert.That(groupDepths, Does.Contain(presentGroupDepth));
            for (int depth = 3; depth < presentGroupDepth; depth += 3)
                Assert.That(groupDepths, Does.Not.Contain(depth));
            Assert.That(tree.CanonicalRecords(), Has.Length.EqualTo(2));
        }
    }

    [Test]
    public void Encoders_omit_only_prefixless_interior_branches(
        [Values(0, 3, 267, 516)] int groupDepth, [Values(0, 1, 2)] int nodeKind)
    {
        PbtStorageNodePath groupKey = new(new byte[(groupDepth + 7) / 8], groupDepth);
        List<PbtNodeRecord> records = [];
        ReadOnlyMemory<byte>[] encodings = new ReadOnlyMemory<byte>[PbtThreeLevelGroupGeometry.PositionCount];
        PbtTraversalPath groupPath = PbtTraversalPath.FromPath(stackalloc byte[66], groupKey);
        using PbtNodeGroupWriter<PbtStorageNodePath> streamingWriter = new(groupKey.BitDepth);
        int fullLength = PbtNodeGroupCodec.HeaderLength + PbtNodeGroupCodec.AvailabilityLength + PbtNodeGroupCodec.DescendantMaskLength;
        int omitted = 0;
        int positionCount = groupDepth == 0 ? 15 : 14;
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
            if (nodeKind == 0 && path.BitDepth - groupDepth is >= 1 and <= 2) omitted++;
            streamingWriter.Write(groupPath, position, encoding);
        }

        byte[] payload = PbtNodeGroupEncoder.Encode(groupKey, records, default);
        byte[] streamedPayload = streamingWriter.Detach(default, 0)!;
        GroupFrameReader<PbtVariableTreeKey, PbtStorageNodePath> reader = PbtStoreTestExtensions.ReadGroup(groupKey, payload);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fullLength - payload.Length, Is.EqualTo((PbtNodeCodec.BranchLength(0, 0, 0) + sizeof(ushort)) * omitted));
            Assert.That(omitted, Is.EqualTo(nodeKind == 0 ? 6 : 0));
            Assert.That(reader.Nodes(), Has.Count.EqualTo(positionCount - omitted));
            Assert.That(streamedPayload, Is.EqualTo(payload));
        }
        for (int position = 0; position < positionCount; position++)
        {
            int relativeDepth = records[position].Path.BitDepth - groupDepth;
            bool retained = nodeKind != 0 || relativeDepth is 0 or 3;
            Assert.That(reader.GetEncoding(position).ToArray(), Is.EqualTo(retained ? encodings[position].ToArray() : Array.Empty<byte>()), $"position {position}");
        }
    }

    [Test]
    public void Explicit_emissions_preserve_only_selected_nodes(
        [Values(0, 3, 6, 516)] int groupDepth,
        [Values(0u, 0x4D89u, 0x7FFFu)] uint selected,
        [Values] bool copyRanges)
    {
        PbtStorageNodePath groupKey = new(new byte[(groupDepth + 7) / 8], groupDepth);
        List<PbtNodeRecord> records = [];
        List<PbtNodeRecord> expectedRecords = [];
        int positionCount = groupDepth == 0 ? PbtThreeLevelGroupGeometry.PositionCount : PbtThreeLevelGroupGeometry.RootPosition;
        for (int position = 0; position < positionCount; position++)
        {
            if (position % 5 == 0) continue;
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
        GroupFrameReader<PbtVariableTreeKey, PbtStorageNodePath> reader = PbtStoreTestExtensions.ReadGroup(groupPath, store.GetNodeGroup(groupPath)!);
        using PbtNodeGroupWriter<PbtStorageNodePath> writer = new(groupKey.BitDepth);
        int nextPosition = 0;
        foreach (int endPosition in new[] { 0, 3, 8, 11, 15 })
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
        byte[]? payload = writer.Detach(default, 0);
        Assert.That(payload, Is.EqualTo(expectedRecords.Count == 0 ? null : PbtNodeGroupEncoder.Encode(groupKey, expectedRecords, default)));
    }

    [TestCase(0, 1)]
    [TestCase(0, 15)]
    [TestCase(3, 14)]
    [TestCase(3, 3)]
    public void Streaming_writer_preserves_canonical_bytes(int groupDepth, int count)
    {
        PbtStorageNodePath groupKey = new(new byte[(groupDepth + 7) / 8], groupDepth);
        List<PbtNodeRecord> records = [];
        PbtTraversalPath groupPath = PbtTraversalPath.FromPath(stackalloc byte[66], groupKey);
        using PbtNodeGroupWriter<PbtStorageNodePath> writer = new(groupKey.BitDepth);
        for (int index = 0; index < count; index++)
        {
            int position = count == 1 ? 14 : count == 3 ? 3 + index * 4 : index;
            byte[] encoding = PbtTreeHarness.EncodeBranch(Bytes.FromHexString("A0"), 4,
                new ValueHash256(Value(1)), new ValueHash256(Value(2)));
            records.Add(new(PbtTestPaths.PathOf(groupKey, position), encoding));
            if (index % 2 == 0)
            {
                Span<byte> destination = writer.Append(position, encoding.Length);
                PbtNodeCodec.CreateBranchEncoding(destination, 4, new ValueHash256(Value(1)), new ValueHash256(Value(2)));
                PbtNodeCodec.WriteBranchTrailer(destination[PbtNodeCodec.BranchPreimageLength(4)..], [], []);
                destination[3] = 0xA0;
            }
            else writer.Write(groupPath, position, encoding);
        }

        byte[] expected = PbtNodeGroupEncoder.Encode(groupKey, records, default);

        byte[] payload = writer.Detach(default, 0)!;
        writer.Dispose();
        GroupFrameReader<PbtVariableTreeKey, PbtStorageNodePath> reader = PbtStoreTestExtensions.ReadGroup(groupKey, payload);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(payload, Is.EqualTo(expected));
            Assert.That(expected[..PbtNodeGroupCodec.HeaderLength], Is.EqualTo(Bytes.FromHexString("08")));
            Assert.That(reader.Nodes(), Has.Count.EqualTo(count));
            int nodeLength = PbtNodeCodec.BranchLength(4, 0, 0);
            Assert.That(expected.Length, Is.EqualTo(count * nodeLength + 4 + 2 * count));
            uint availability = 0;
            for (int index = 0; index < count; index++)
            {
                int position = PbtTestPaths.Locate(records[index].Path).Position;
                availability |= 1u << position;
                Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(expected.AsSpan(1 + count * nodeLength + index * 2)), Is.EqualTo(index * nodeLength));
                Assert.That(reader.GetEncoding(position).ToArray(), Is.EqualTo(expected.AsSpan(1 + index * nodeLength, nodeLength).ToArray()));
            }
            Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(expected.AsSpan(expected.Length - 3)), Is.EqualTo(availability));
        }
    }

    [Test]
    public void Streaming_writer_rejects_groups_above_the_maximum_payload_length()
    {
        using (PbtNodeGroupWriter<PbtNodePath> writer = new(0))
        {
            for (int position = 0; position < 3; position++) AppendBranch(writer, position, 8192);
            byte[] payload = writer.Detach(default, 0)!;
            Assert.That(PbtStoreTestExtensions.ReadGroup(new PbtNodePath([], 0), payload).Nodes(), Has.Count.EqualTo(3));
        }
        using (PbtNodeGroupWriter<PbtNodePath> writer = new(0))
        {
            for (int position = 0; position < 4; position++) AppendBranch(writer, position, 8191);
            using (Assert.EnterMultipleScope())
            {
                Assert.Throws<InvalidDataException>(() => writer.Detach(default, 0), "the trailer pushes the group past the maximum");
                Assert.Throws<InvalidDataException>(() => writer.Append(4, 8192));
            }
        }

        static void AppendBranch(PbtNodeGroupWriter<PbtNodePath> writer, int position, int length)
        {
            Span<byte> encoding = writer.Append(position, length);
            PbtNodeCodec.CreateBranchEncoding(encoding, (length - PbtNodeCodec.BranchLength(0, 0, 0)) * 8,
                new ValueHash256(Value(1)), new ValueHash256(Value(2)));
            PbtNodeCodec.WriteBranchTrailer(encoding[^PbtNodeCodec.BranchTrailerHeaderLength..], [], []);
        }
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
        int footerOffset = payload.Length - 9;
        switch (scenario)
        {
            case 0: payload = []; break;
            case 1: payload = payload[1..]; break;
            case 2: payload[0] = 1; break;
            case 3: payload[0] = 2; break;
            case 4: payload = Bytes.FromHexString("06000000"); break;
            case 5: payload = Bytes.FromHexString("060004000b00000000"); break;
            case 6: payload.AsSpan(payload.Length - 3, 2).Clear(); break;
            case 7: payload[^2] |= 0x80; break;
            case 8: groupKey = new(Bytes.FromHexString("00"), 3); break;
            case 9: payload[footerOffset] = 1; break;
            case 10: BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(footerOffset + 2), 0); break;
            case 11: BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(footerOffset + 4), 67); break;
            case 12: BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(footerOffset + 4), 204); break;
            case 13: BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(footerOffset + 4), ushort.MaxValue); break;
            case 14: payload[1] = 0xFF; break;
            case 15: payload[4] = 0x81; break;
            case 16: payload.AsSpan(5, 32).Clear(); break;
            case 17:
                payload = new byte[1 + ushort.MaxValue + 1 + 5];
                payload[0] = 8;
                payload[^2] = 0x40;
                break;
            // A descendant mask bit must carry a nonzero size.
            case 18: payload = [.. payload[..^1], 0, 1, 0x01]; break;
            case 19: payload[0] = 5; break;
            // Descendant sizes take the width, 1–6 bytes, of the largest one.
            case 20: payload = [.. payload[..^1], 1, 0, 0x01]; break;
            case 21: payload = [.. payload[..^1], 1, 0, 0, 0, 0, 0, 0, 7, 0x01]; break;
            case 22: payload = [.. payload[..^1], 1, 0, 2, 0x01]; break;
        }
        Assert.Throws<InvalidDataException>(() => ReadGroupCount(groupKey, payload));
    }

    [Test]
    public void Descendant_sizes_are_stored_per_slot_and_reject_the_uint48_overflow(
        [Values(0L, 1L, 0x1234L, 0x1234_5678_9ABCL, PbtNodeGroupCodec.MaxDescendantBytes)] long descendantBytes, [Values(0, 3, 7)] int slot)
    {
        PbtNodePath groupKey = new([], 0);
        PbtNodeRecord record = new(groupKey.ToPath<PbtStorageNodePath>(), LeafEncoding(0x00));
        long[] slots = new long[PbtThreeLevelGroupGeometry.BoundarySlots];
        slots[slot] = descendantBytes;
        byte[] payload = PbtNodeGroupEncoder.Encode(groupKey, [record], slots);
        PbtTraversalPath groupPath = PbtTraversalPath.FromPath(stackalloc byte[66], groupKey);
        using PbtNodeGroupWriter<PbtNodePath> streamingWriter = new(0);
        streamingWriter.Write(groupPath, PbtThreeLevelGroupGeometry.RootPosition, record.Encoding.Span);
        byte[] streamed = streamingWriter.Detach(slots, 0xFF)!;
        long[] stored = PbtStoreTestExtensions.ReadDescendantBytes(payload);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(payload, Has.Length.EqualTo(descendantBytes switch { 0 => 9, 1 => 11, 0x1234 => 12, _ => 16 }));
            Assert.That(PbtNodeGroupCodec.ReadDescendantMask(payload), Is.EqualTo(descendantBytes == 0 ? 0 : 1 << slot));
            Assert.That(stored, Is.EqualTo(slots));
            Assert.That(PbtStoreTestExtensions.ReadGroup(groupKey, payload).DescendantBytes(slot), Is.EqualTo(descendantBytes));
            Assert.That(streamed, Is.EqualTo(payload));
        }
        slots[slot] = -1;
        Assert.Throws<ArgumentOutOfRangeException>(() => PbtNodeGroupEncoder.Encode(groupKey, [record], slots));
        slots[slot] = PbtNodeGroupCodec.MaxDescendantBytes + 1;
        Assert.Throws<InvalidDataException>(() => PbtNodeGroupEncoder.Encode(groupKey, [record], slots));
    }

    [Test]
    public void Group_buffer_write_batches_preserve_canonical_payloads_and_immutable_links([Values(0, 3, 267)] int depth)
    {
        using PbtGroupRecordBuffer buffer = new(0, PbtGroupRecordBuffer.DefaultGenerationBytes, PbtGroupRecordBuffer.DefaultLiveGenerations);
        using PbtGroupRecordBufferPin pin = buffer.PinHead();
        byte[] key = new byte[PbtVariableTreeKey.MaxLength];
        PbtStorageNodePath path = PbtTestPaths.Prefix<PbtStorageNodePath>(key, depth);
        PbtStorageNodePath childPath = PbtTestPaths.Prefix<PbtStorageNodePath>(key, depth + 6);
        byte[] payload = GroupRecordPayload(path);
        ulong child = buffer.Append(childPath, GroupRecordPayload(childPath), default);
        PbtGroupLinks links = default;
        links[0] = child;
        links[1] = 0;
        ulong pointer = buffer.Append(path, payload, links);
        Assert.That(buffer.TryLocate(pointer, out PbtGroupRecord record), Is.True);
        PbtGroupLinks copiedLinks = record.Links;
        copiedLinks[0] = 0;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pointer != 0, Is.True);
            Assert.That(record.Path, Is.EqualTo(path));
            Assert.That(record.Payload.ToArray(), Is.EqualTo(payload));
            Assert.That(record.GetLink(0), Is.EqualTo(child));
            Assert.That(record.GetLink(1), Is.Zero);
            Assert.That(record.GetLink(2), Is.Zero);
        }
        PbtNodeGroupCodec.ValidateNodes(path, record.Payload.Span);
    }

    [Test]
    public void Group_buffer_concurrent_appends_and_reads_do_not_allocate_per_read()
    {
        using PbtGroupRecordBuffer buffer = new(0, PbtGroupRecordBuffer.DefaultGenerationBytes, PbtGroupRecordBuffer.DefaultLiveGenerations);
        using PbtGroupRecordBufferPin pin = buffer.PinHead();
        ConcurrentBag<(ulong Pointer, byte[] Payload)> written = [];
        Parallel.For(0, 128, index =>
        {
            byte[] payload = RootGroup(LeafEncoding((byte)index));
            written.Add((buffer.Append(default, payload, default), payload));
        });
        Assert.That(written.Select(static entry => entry.Pointer).Distinct().Count(), Is.EqualTo(128));
        foreach ((ulong pointer, byte[] payload) in written)
        {
            Assert.That(buffer.TryLocate(pointer, out PbtGroupRecord record), Is.True);
            Assert.That(record.Payload.ToArray(), Is.EqualTo(payload));
        }
        ulong retained = written.First().Pointer;
        for (int i = 0; i < 100; i++) buffer.TryLocate(retained, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        bool found = true;
        for (int i = 0; i < 10_000; i++) found &= buffer.TryLocate(retained, out _);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(allocated, Is.Zero);
        }
    }

    private static byte[] GroupRecordPayload(PbtStorageNodePath path)
    {
        PbtStorageNodePath node = PbtTestPaths.Prefix<PbtStorageNodePath>(new byte[PbtVariableTreeKey.MaxLength], path.BitDepth == 0 ? 0 : path.BitDepth + 1);
        return PbtNodeGroupEncoder.Encode(path, [new PbtNodeRecord(node, LeafBranch(node, 1))], default);
    }

    private sealed class WarmReadStore(PbtNodeGroupStore store) : IPbtStore
    {
        public IPbtGroupRecordWriteBatch StartWriteBatch(int alignedTotalLength) => throw new AssertionException("Group frame reads must never write.");
        public bool TryLocate(ulong pointer, out PbtGroupRecord record)
        {
            record = default;
            return false;
        }
        public bool TryLoad<TArg>(scoped in PbtTraversalPath path, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct =>
            PbtNodeGroupStore.TryLoadPayload(GetNodeGroup(path), action, arg);

        internal List<PbtStorageNodePath> Reads { get; } = [];

        public byte[]? GetNodeGroup(scoped in PbtTraversalPath groupKey)
        {
            Reads.Add(groupKey.ToPath<PbtStorageNodePath>());
            return store.GetNodeGroup(groupKey);
        }
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

    private static byte[] LeafEncoding(byte keyMarker) => PbtTreeHarness.EncodeLeaf(new PbtVariableTreeKey([keyMarker]));

    /// <summary>A prefixless branch inlining <paramref name="key"/> as its left leaf: the smallest node stored below the root.</summary>
    private static byte[] LeafBranch(ReadOnlySpan<byte> key, byte marker) =>
        PbtTreeHarness.EncodeBranch([], 0, new ValueHash256(Value(marker)), new ValueHash256(Value(0xFF)), key, []);

    /// <summary>A node valid at <paramref name="path"/>: a prefixless branch inlining a leaf just below it, or without leaves where no longer key fits.</summary>
    private static byte[] LeafBranch<TPath>(TPath path, byte marker) where TPath : struct, IPbtNodePath<TPath>
    {
        if (path.BitDepth >= PbtThreeLevelGroupGeometry.MaxPathDepth) return LeafBranch([], marker);
        byte[] key = new byte[(path.BitDepth >> 3) + 1];
        PbtNodePathOperations.CopyTo(path, key);
        return LeafBranch(key.AsSpan(PbtNodeCodec.InlineKeyOffset(path.BitDepth)), marker);
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.PersistedSnapshots;
using Nethermind.State.Pbt.Snapshot;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtNodeGroupKeyTests
{
    [TestCase("", 0, 0x30)]
    [TestCase("00", 4, 0x31)]
    [TestCase("01", 8, 0x31)]
    [TestCase("f0", 4, 0x31)]
    [TestCase("00000000", 32, 0x32)]
    [TestCase("01000000000000000000000000000000000000000000000000000000000000000000", 264, 0x33)]
    [TestCase("ff0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000", 524, 0x34)]
    public void Persisted_keys_follow_the_database_column_and_key(string pathHex, int depth, byte family)
    {
        PbtStorageNodePath path = new(Bytes.FromHexString(pathHex), depth);
        byte[] key = PbtPersistedKey.Group(path);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(key[0], Is.EqualTo(family));
            Assert.That(key[1..], Is.EqualTo(depth == 0 ? Array.Empty<byte>() : path.ToStorageKey(PbtColumns.StorageNodeGroups)));
            Assert.That(PbtPersistedKey.DecodeGroup(key), Is.EqualTo(path));
            Assert.That(key.Length, Is.LessThanOrEqualTo(255));
        }
    }

    [Test]
    public void Persisted_keys_reject_invalid_group_keys_partitions_and_padding([Values("3000", "31", "320004", "310008", "310f04")] string key) =>
        Assert.Throws<InvalidDataException>(() => PbtPersistedKey.ValidateDescriptor(Bytes.FromHexString(key)));

    [Test]
    public void Layer_groups_enumerate_root_first_then_in_database_key_order([Values(1, 2, 3)] int seed)
    {
        Random random = new(seed);
        // Along a zero chain a partial group sorts after its zero-byte descendants, and a byte-aligned one before them.
        List<PbtStorageNodePath> paths = [new(new byte[8], 60)];
        for (int index = 0; index < 64; index++)
        {
            byte[] bytes = new byte[8];
            random.NextBytes(bytes);
            paths.Add(new PbtStorageNodePath(bytes, 63).Prefix(PbtFourLevelGroupGeometry.LevelsPerGroup * random.Next(1, 16)));
        }
        using PbtSnapshotContent content = new();
        content.SetTree([.. paths.Distinct().Select(static path => (path, (byte[]?)[1]))]);
        PbtGroupRecord[] ordered = [.. content.EnumerateNodeGroups()];
        byte[][] keys = [.. ordered.Skip(1).Select(static group => group.Path.ToStorageKey(PbtColumns.StorageNodeGroups))];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ordered[0].Path, Is.EqualTo(default(PbtStorageNodePath)));
            Assert.That(ordered.Select(static group => group.Path), Is.Unique.And.SupersetOf(paths));
            Assert.That(keys, Is.EqualTo(keys.Order(Comparer<byte[]>.Create(static (a, b) => a.AsSpan().SequenceCompareTo(b))).ToArray()));
        }
    }

    [TestCase("f0", 4, "f004")]
    [TestCase("ff80", 12, "ff8004")]
    [TestCase("01ab", 16, "01ab00")]
    [TestCase("abcdef", 24, "abcdef00")]
    [TestCase("ff0000000000000000000000000000000000000000000000000000000000000000", 260, "ff000000000000000000000000000000000000000000000000000000000000000004")]
    [TestCase("fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff0", 524, "fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff004")]
    public void Encode_trails_the_path_with_its_alignment(string pathHex, int bitDepth, string keyHex)
    {
        byte[] expected = Bytes.FromHexString(keyHex);
        PbtStorageNodePath storagePath = new(Bytes.FromHexString(pathHex), bitDepth);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(storagePath.ToStorageKey(PbtColumns.StorageNodeGroups), Is.EqualTo(expected));
            if (bitDepth <= PbtNodePath.MaxBitDepth)
                Assert.That(new PbtNodePath(Bytes.FromHexString(pathHex), bitDepth).ToStorageKey(PbtColumns.StorageNodeGroups), Is.EqualTo(expected));
            Assert.That(PbtNodeGroupKey.Decode(expected), Is.EqualTo(storagePath));
        }
    }

    [TestCase("ab", 8, "ab00", 12, true, TestName = "Byte_aligned_group_precedes_zero_nibble_child")]
    [TestCase("ab", 8, "ab00", 16, true, TestName = "Byte_aligned_group_precedes_zero_byte_descendant")]
    [TestCase("a0", 4, "a0", 8, false, TestName = "Nibble_group_follows_zero_nibble_child")]
    [TestCase("a0", 4, "a1", 8, true, TestName = "Nibble_group_precedes_non_zero_nibble_child")]
    [TestCase("a0", 4, "a010", 16, true, TestName = "Nibble_group_precedes_non_zero_byte_descendant")]
    [TestCase("a0", 4, "a000", 16, false, TestName = "Nibble_group_follows_zero_byte_descendant")]
    public void Sorts_a_group_relative_to_its_descendants(string groupHex, int groupDepth, string descendantHex, int descendantDepth, bool groupFirst)
    {
        byte[] group = new PbtStorageNodePath(Bytes.FromHexString(groupHex), groupDepth).ToStorageKey(PbtColumns.StorageNodeGroups);
        byte[] descendant = new PbtStorageNodePath(Bytes.FromHexString(descendantHex), descendantDepth).ToStorageKey(PbtColumns.StorageNodeGroups);
        Assert.That(group.AsSpan().SequenceCompareTo(descendant) < 0, Is.EqualTo(groupFirst));
    }

    [TestCase("", TestName = "Rejects_empty_key")]
    [TestCase("01", TestName = "Rejects_trailer_only_key")]
    [TestCase("ab08", TestName = "Rejects_unknown_trailer")]
    [TestCase("ab02", TestName = "Rejects_non_group_depth")]
    [TestCase("0f04", TestName = "Rejects_non_zero_unused_bits")]
    [TestCase("00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000", TestName = "Rejects_depth_past_the_maximum_group_depth")]
    [TestCase("0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001", TestName = "Rejects_path_past_the_storage_capacity")]
    public void Decode_rejects_malformed_keys(string keyHex) =>
        Assert.That(() => PbtNodeGroupKey.Decode(Bytes.FromHexString(keyHex)), Throws.TypeOf<InvalidDataException>());
}

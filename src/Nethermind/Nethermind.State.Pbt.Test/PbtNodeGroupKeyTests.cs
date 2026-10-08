// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.PersistedSnapshots;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtNodeGroupKeyTests
{
    [TestCase("", 0, 0x30)]
    [TestCase("00", 4, 0x30)]
    [TestCase("01", 8, 0x31)]
    [TestCase("f0", 4, 0x32)]
    [TestCase("ff", 8, 0x32)]
    [TestCase("ff0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000", 524, 0x32)]
    public void Retained_keys_preserve_root_depth_and_partition(string pathHex, int depth, byte family)
    {
        PbtStorageNodePath path = new(Bytes.FromHexString(pathHex), depth);
        byte[] key = PbtRetainedKey.Group(path);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(key[0], Is.EqualTo(family));
            Assert.That(PbtRetainedKey.DecodeGroup(key), Is.EqualTo(path));
            Assert.That(key.Length, Is.LessThanOrEqualTo(255));
        }
    }

    [Test]
    public void Retained_keys_reject_invalid_group_depth_partition_and_padding([Values("3000040100", "31000000", "3000050000", "300114000000000000000000000000000000000000000000000000000000000000000000000000", "3000000000")] string key) =>
        Assert.Throws<InvalidDataException>(() => PbtRetainedKey.ValidateDescriptor(Bytes.FromHexString(key)));

    [TestCase("00", 4, "0001")]
    [TestCase("f0", 4, "f001")]
    [TestCase("ff0000000000000000000000000000000000000000000000000000000000000000", 260, "ff000000000000000000000000000000000000000000000000000000000000000001")]
    [TestCase("80", 8, "8000")]
    [TestCase("01ab", 16, "01ab00")]
    [TestCase("fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff0", 524, "fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff001")]
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
    [TestCase("ab02", TestName = "Rejects_unknown_trailer")]
    [TestCase("ab04", TestName = "Rejects_bit_count_trailer")]
#if DEBUG
    [TestCase("0f01", TestName = "Rejects_non_zero_unused_bits")]
#endif
    [TestCase("00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000", TestName = "Rejects_depth_past_the_maximum_group_depth")]
    [TestCase("0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001", TestName = "Rejects_path_past_the_storage_capacity")]
    public void Decode_rejects_malformed_keys(string keyHex) =>
        Assert.That(() => PbtNodeGroupKey.Decode(Bytes.FromHexString(keyHex)), Throws.TypeOf<InvalidDataException>());
}

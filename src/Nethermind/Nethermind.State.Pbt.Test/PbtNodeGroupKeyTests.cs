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
    [TestCase("00", 3, 0x30)]
    [TestCase("0100", 9, 0x31)]
    [TestCase("e0", 3, 0x32)]
    [TestCase("fc", 6, 0x32)]
    [TestCase("ff00", 9, 0x32)]
    [TestCase("ff0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000", 525, 0x32)]
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
    public void Retained_keys_reject_invalid_group_depth_partition_and_padding([Values("3000060100", "31000000", "3000050000", "300114000000000000000000000000000000000000000000000000000000000000000000000000", "3000000000")] string key) =>
        Assert.Throws<InvalidDataException>(() => PbtRetainedKey.ValidateDescriptor(Bytes.FromHexString(key)));

    [TestCase("e0", 3, "e003")]
    [TestCase("fc", 6, "fc06")]
    [TestCase("ff80", 9, "ff8001")]
    [TestCase("abc0", 12, "abc004")]
    [TestCase("abfe", 15, "abfe07")]
    [TestCase("01ab00", 18, "01ab0002")]
    [TestCase("abcdef", 24, "abcdef00")]
    [TestCase("ff0000000000000000000000000000000000000000000000000000000000000000", 261, "ff000000000000000000000000000000000000000000000000000000000000000005")]
    [TestCase("fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff8", 525, "fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff805")]
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

    [TestCase("abcdef", 24, "abcdef00", 27, true, TestName = "Byte_aligned_group_precedes_zero_child")]
    [TestCase("abcdef", 24, "abcdef000000", 48, true, TestName = "Byte_aligned_group_precedes_zero_byte_descendant")]
    [TestCase("e0", 3, "e0", 6, true, TestName = "Partial_group_precedes_zero_child")]
    [TestCase("e0", 3, "e4", 6, true, TestName = "Partial_group_precedes_non_zero_child")]
    [TestCase("e0", 3, "e010", 12, true, TestName = "Partial_group_precedes_non_zero_byte_descendant")]
    [TestCase("e0", 3, "e000", 9, false, TestName = "Partial_group_follows_zero_byte_descendant")]
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
    [TestCase("1f03", TestName = "Rejects_non_zero_unused_bits")]
    [TestCase("00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000", TestName = "Rejects_depth_past_the_maximum_group_depth")]
    [TestCase("0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001", TestName = "Rejects_path_past_the_storage_capacity")]
    public void Decode_rejects_malformed_keys(string keyHex) =>
        Assert.That(() => PbtNodeGroupKey.Decode(Bytes.FromHexString(keyHex)), Throws.TypeOf<InvalidDataException>());
}

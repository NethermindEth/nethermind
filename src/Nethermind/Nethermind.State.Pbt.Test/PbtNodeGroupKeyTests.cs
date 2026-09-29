// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtNodeGroupKeyTests
{
    [TestCase(PbtColumns.TopNodeGroups, "00", 4, "0001")]
    [TestCase(PbtColumns.TopNodeGroups, "f0", 4, "f001")]
    [TestCase(PbtColumns.TopNodeGroups, "ff0000000000000000000000000000000000000000000000000000000000000000", 260, "ff000000000000000000000000000000000000000000000000000000000000000001")]
    [TestCase(PbtColumns.AccountNodeGroups, "80", 8, "8000")]
    [TestCase(PbtColumns.CodeNodeGroups, "01ab", 16, "01ab00")]
    [TestCase(PbtColumns.StorageNodeGroups, "f0", 4, "f001")]
    [TestCase(PbtColumns.StorageNodeGroups, "fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff0", 524, "fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff001")]
    public void Encode_trails_the_path_with_its_alignment(PbtColumns column, string pathHex, int bitDepth, string keyHex)
    {
        byte[] expected = Bytes.FromHexString(keyHex);
        PbtStorageNodePath storagePath = new(Bytes.FromHexString(pathHex), bitDepth);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(storagePath.ToStorageKey(column), Is.EqualTo(expected));
            if (bitDepth <= PbtNodePath.MaxBitDepth)
                Assert.That(new PbtNodePath(Bytes.FromHexString(pathHex), bitDepth).ToStorageKey(column), Is.EqualTo(expected));
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

    [Test]
    public void Encode_rejects_a_path_beyond_the_column_capacity() =>
        Assert.That(() => new PbtStorageNodePath(Bytes.FromHexString("ff" + new string('0', 68)), 280).ToStorageKey(PbtColumns.AccountNodeGroups),
            Throws.TypeOf<ArgumentOutOfRangeException>());

    [TestCase("", TestName = "Rejects_empty_key")]
    [TestCase("01", TestName = "Rejects_trailer_only_key")]
    [TestCase("ab02", TestName = "Rejects_unknown_trailer")]
    [TestCase("ab04", TestName = "Rejects_bit_count_trailer")]
    [TestCase("0f01", TestName = "Rejects_non_zero_unused_bits")]
    [TestCase("00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000", TestName = "Rejects_depth_past_the_maximum_group_depth")]
    [TestCase("0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001", TestName = "Rejects_path_past_the_storage_capacity")]
    public void Decode_rejects_malformed_keys(string keyHex) =>
        Assert.That(() => PbtNodeGroupKey.Decode(Bytes.FromHexString(keyHex)), Throws.TypeOf<InvalidDataException>());
}

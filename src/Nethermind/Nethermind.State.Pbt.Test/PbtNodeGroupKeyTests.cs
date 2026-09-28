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
    [TestCase(PbtColumns.TopNodeGroups, "80", 1, "8004")]
    [TestCase(PbtColumns.TopNodeGroups, "c0", 2, "c002")]
    [TestCase(PbtColumns.TopNodeGroups, "e0", 3, "e003")]
    [TestCase(PbtColumns.TopNodeGroups, "f8", 5, "f805")]
    [TestCase(PbtColumns.TopNodeGroups, "fc", 6, "fc06")]
    [TestCase(PbtColumns.TopNodeGroups, "fe", 7, "fe07")]
    [TestCase(PbtColumns.AccountNodeGroups, "ab80", 9, "ab8004")]
    [TestCase(PbtColumns.StorageNodeGroups, "ff00e0", 21, "ff00e005")]
    public void Encode_trails_the_path_with_its_last_byte_bit_count(PbtColumns column, string pathHex, int bitDepth, string keyHex)
    {
        byte[] expected = Bytes.FromHexString(keyHex);
        PbtStorageNodePath storagePath = new(Bytes.FromHexString(pathHex), bitDepth);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(storagePath.ToStorageKey(column), Is.EqualTo(expected));
            if (bitDepth <= PbtNodePath.MaxBitDepth)
                Assert.That(new PbtNodePath(Bytes.FromHexString(pathHex), bitDepth).ToStorageKey(column), Is.EqualTo(expected));
            if (PbtGroupGeometry.IsGroupDepth(bitDepth))
                Assert.That(PbtNodeGroupKey.Decode(expected), Is.EqualTo(storagePath));
            else
                Assert.That(() => PbtNodeGroupKey.Decode(expected), Throws.TypeOf<InvalidDataException>());
        }
    }

    [TestCase("ab", 8, "ab00", 12, true, TestName = "Byte_aligned_group_precedes_zero_nibble_child")]
    [TestCase("ab", 8, "ab00", 16, true, TestName = "Byte_aligned_group_precedes_zero_byte_descendant")]
    [TestCase("a0", 4, "a0", 8, false, TestName = "Nibble_group_follows_zero_nibble_child")]
    [TestCase("a0", 4, "a1", 8, true, TestName = "Nibble_group_precedes_non_zero_nibble_child")]
    [TestCase("a0", 4, "a010", 16, true, TestName = "Nibble_group_precedes_non_zero_byte_descendant")]
    [TestCase("a0", 4, "a000", 16, false, TestName = "Nibble_group_follows_zero_byte_descendant")]
    [TestCase("e0", 3, "e4", 6, true, TestName = "Three_bit_group_precedes_its_descendant_past_the_trailer")]
    [TestCase("e0", 3, "e002", 16, false, TestName = "Three_bit_group_follows_descendant_below_the_trailer")]
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
    [TestCase("ab08", TestName = "Rejects_trailer_past_seven_bits")]
    [TestCase("ab02", TestName = "Rejects_non_zero_unused_bits_of_a_two_bit_path")]
    [TestCase("0f01", TestName = "Rejects_non_zero_unused_bits_of_a_nibble_path")]
    [TestCase("c004", TestName = "Rejects_non_zero_unused_bits_of_a_one_bit_path")]
    [TestCase("00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000", TestName = "Rejects_depth_past_the_maximum_group_depth")]
    [TestCase("0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001", TestName = "Rejects_path_past_the_storage_capacity")]
    public void Decode_rejects_malformed_keys(string keyHex) =>
        Assert.That(() => PbtNodeGroupKey.Decode(Bytes.FromHexString(keyHex)), Throws.TypeOf<InvalidDataException>());
}

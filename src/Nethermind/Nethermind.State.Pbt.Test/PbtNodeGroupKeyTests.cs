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
    private static readonly string StorageZeroPadding = new('0', 132);
    private static readonly string AccountZeroPadding = new('0', 68);

    [TestCase(PbtColumns.TopNodeGroups, "00", 4, "01", "0001")]
    [TestCase(PbtColumns.TopNodeGroups, "f0", 4, "01", "f001")]
    [TestCase(PbtColumns.TopNodeGroups, "ff0000000000000000000000000000000000000000000000000000000000000000", 260, "41", "ff000000000000000000000000000000000000000000000000000000000000000001")]
    [TestCase(PbtColumns.AccountNodeGroups, "80", 8, "02", "8000")]
    [TestCase(PbtColumns.CodeNodeGroups, "01ab", 16, "04", "01ab00")]
    [TestCase(PbtColumns.StorageNodeGroups, "f0", 4, "01", "f001")]
    [TestCase(PbtColumns.StorageNodeGroups, "fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff0", 524, "83", "fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff001")]
    public void Encode_pads_or_trails_the_path_per_layout(PbtColumns column, string pathHex, int bitDepth, string nibbleCountHex, string variableHex)
    {
        string padding = column == PbtColumns.StorageNodeGroups ? StorageZeroPadding : AccountZeroPadding;
        byte[] expectedPadded = Bytes.FromHexString((pathHex + padding)[..padding.Length] + nibbleCountHex);
        byte[] expectedVariable = Bytes.FromHexString(variableHex);
        PbtStorageNodePath storagePath = new(Bytes.FromHexString(pathHex), bitDepth);

        using (Assert.EnterMultipleScope())
        {
            foreach ((PbtNodeGroupKeyLayout layout, byte[] expected) in new[] { (PbtNodeGroupKeyLayout.Padded, expectedPadded), (PbtNodeGroupKeyLayout.Variable, expectedVariable) })
            {
                byte[] fromStoragePath = storagePath.ToStorageKey(column, layout);
                Assert.That(fromStoragePath, Is.EqualTo(expected), layout.ToString());
                if (bitDepth <= PbtNodePath.MaxBitDepth)
                    Assert.That(new PbtNodePath(Bytes.FromHexString(pathHex), bitDepth).ToStorageKey(column, layout), Is.EqualTo(expected), layout.ToString());
                Assert.That(PbtNodeGroupKey.Decode(layout, fromStoragePath), Is.EqualTo(storagePath), layout.ToString());
            }
        }
    }

    [TestCase("ab", 8, "ab00", 12, true, TestName = "Byte_aligned_group_precedes_zero_nibble_child")]
    [TestCase("ab", 8, "ab00", 16, true, TestName = "Byte_aligned_group_precedes_zero_byte_descendant")]
    [TestCase("a0", 4, "a0", 8, false, TestName = "Nibble_group_follows_zero_nibble_child")]
    [TestCase("a0", 4, "a1", 8, true, TestName = "Nibble_group_precedes_non_zero_nibble_child")]
    [TestCase("a0", 4, "a010", 16, true, TestName = "Nibble_group_precedes_non_zero_byte_descendant")]
    [TestCase("a0", 4, "a000", 16, false, TestName = "Nibble_group_follows_zero_byte_descendant")]
    public void Variable_sorts_a_group_relative_to_its_descendants(string groupHex, int groupDepth, string descendantHex, int descendantDepth, bool groupFirst)
    {
        byte[] group = new PbtStorageNodePath(Bytes.FromHexString(groupHex), groupDepth).ToStorageKey(PbtColumns.StorageNodeGroups, PbtNodeGroupKeyLayout.Variable);
        byte[] descendant = new PbtStorageNodePath(Bytes.FromHexString(descendantHex), descendantDepth).ToStorageKey(PbtColumns.StorageNodeGroups, PbtNodeGroupKeyLayout.Variable);
        Assert.That(group.AsSpan().SequenceCompareTo(descendant) < 0, Is.EqualTo(groupFirst));
    }

    [Test]
    public void Encode_rejects_a_path_beyond_the_column_capacity([Values] PbtNodeGroupKeyLayout layout) =>
        Assert.That(() => new PbtStorageNodePath(Bytes.FromHexString("ff" + new string('0', 68)), 280).ToStorageKey(PbtColumns.AccountNodeGroups, layout),
            Throws.TypeOf<ArgumentOutOfRangeException>());

    [TestCase(PbtNodeGroupKeyLayout.Padded, "", TestName = "Padded_rejects_empty_key")]
    [TestCase(PbtNodeGroupKeyLayout.Padded, "00000000000000000000000000000000000000000000000000000000000000000001", TestName = "Padded_rejects_34_byte_key")]
    [TestCase(PbtNodeGroupKeyLayout.Padded, "000000000000000000000000000000000000000000000000000000000000000000000001", TestName = "Padded_rejects_36_byte_key")]
    [TestCase(PbtNodeGroupKeyLayout.Padded, "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001", TestName = "Padded_rejects_66_byte_key")]
    [TestCase(PbtNodeGroupKeyLayout.Padded, "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001", TestName = "Padded_rejects_68_byte_key")]
    [TestCase(PbtNodeGroupKeyLayout.Padded, "0000000000000000000000000000000000000000000000000000000000000000000000", TestName = "Padded_rejects_root_depth")]
    [TestCase(PbtNodeGroupKeyLayout.Padded, "00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000084", TestName = "Padded_rejects_depth_past_the_maximum_group_depth")]
    [TestCase(PbtNodeGroupKeyLayout.Padded, "000000000000000000000000000000000000000000000000000000000000000000004b", TestName = "Padded_rejects_depth_past_the_column_capacity")]
    [TestCase(PbtNodeGroupKeyLayout.Padded, "000000000000000000000000000000000000000000000000000000000000000000ff01", TestName = "Padded_rejects_non_zero_padding")]
    [TestCase(PbtNodeGroupKeyLayout.Padded, "0f00000000000000000000000000000000000000000000000000000000000000000001", TestName = "Padded_rejects_non_zero_unused_bits")]
    [TestCase(PbtNodeGroupKeyLayout.Variable, "", TestName = "Variable_rejects_empty_key")]
    [TestCase(PbtNodeGroupKeyLayout.Variable, "01", TestName = "Variable_rejects_trailer_only_key")]
    [TestCase(PbtNodeGroupKeyLayout.Variable, "ab02", TestName = "Variable_rejects_unknown_trailer")]
    [TestCase(PbtNodeGroupKeyLayout.Variable, "ab04", TestName = "Variable_rejects_bit_count_trailer")]
    [TestCase(PbtNodeGroupKeyLayout.Variable, "0f01", TestName = "Variable_rejects_non_zero_unused_bits")]
    [TestCase(PbtNodeGroupKeyLayout.Variable, "00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000", TestName = "Variable_rejects_depth_past_the_maximum_group_depth")]
    [TestCase(PbtNodeGroupKeyLayout.Variable, "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001", TestName = "Variable_rejects_path_past_the_storage_capacity")]
    public void Decode_rejects_malformed_keys(PbtNodeGroupKeyLayout layout, string keyHex) =>
        Assert.That(() => PbtNodeGroupKey.Decode(layout, Bytes.FromHexString(keyHex)), Throws.TypeOf<InvalidDataException>());
}

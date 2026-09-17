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

    [TestCase(PbtColumns.AccountNodeGroups, "00", 4, "01")]
    [TestCase(PbtColumns.AccountNodeGroups, "80", 8, "02")]
    [TestCase(PbtColumns.CodeNodeGroups, "01ab", 16, "04")]
    [TestCase(PbtColumns.StorageNodeGroups, "f0", 4, "01")]
    [TestCase(PbtColumns.StorageNodeGroups, "fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff0", 524, "83")]
    public void Encode_pads_the_path_to_the_column_key_length_and_appends_the_nibble_count(PbtColumns column, string pathHex, int bitDepth, string nibbleCountHex)
    {
        string padding = column == PbtColumns.StorageNodeGroups ? StorageZeroPadding : AccountZeroPadding;
        byte[] expected = Bytes.FromHexString((pathHex + padding)[..padding.Length] + nibbleCountHex);
        PbtStorageNodePath storagePath = new(Bytes.FromHexString(pathHex), bitDepth);
        byte[] fromStoragePath = storagePath.ToStorageKey(column);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fromStoragePath, Is.EqualTo(expected));
            if (bitDepth <= PbtNodePath.MaxBitDepth)
                Assert.That(new PbtNodePath(Bytes.FromHexString(pathHex), bitDepth).ToStorageKey(column), Is.EqualTo(expected));
            Assert.That(PbtNodeGroupKey.Decode(fromStoragePath), Is.EqualTo(storagePath));
        }
    }

    [Test]
    public void Encode_rejects_a_path_beyond_the_column_capacity() =>
        Assert.That(() => new PbtStorageNodePath(Bytes.FromHexString("ff" + new string('0', 68)), 280).ToStorageKey(PbtColumns.AccountNodeGroups),
            Throws.TypeOf<ArgumentOutOfRangeException>());

    [TestCase("", TestName = "Rejects_empty_key")]
    [TestCase("00000000000000000000000000000000000000000000000000000000000000000001", TestName = "Rejects_34_byte_key")]
    [TestCase("000000000000000000000000000000000000000000000000000000000000000000000001", TestName = "Rejects_36_byte_key")]
    [TestCase("000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001", TestName = "Rejects_66_byte_key")]
    [TestCase("0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001", TestName = "Rejects_68_byte_key")]
    [TestCase("0000000000000000000000000000000000000000000000000000000000000000000000", TestName = "Rejects_root_depth")]
    [TestCase("00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000084", TestName = "Rejects_depth_past_the_maximum_group_depth")]
    [TestCase("000000000000000000000000000000000000000000000000000000000000000000004b", TestName = "Rejects_depth_past_the_column_capacity")]
    [TestCase("000000000000000000000000000000000000000000000000000000000000000000ff01", TestName = "Rejects_non_zero_padding")]
    [TestCase("0f00000000000000000000000000000000000000000000000000000000000000000001", TestName = "Rejects_non_zero_unused_bits")]
    public void Decode_rejects_malformed_keys(string keyHex) =>
        Assert.That(() => PbtNodeGroupKey.Decode(Bytes.FromHexString(keyHex)), Throws.TypeOf<InvalidDataException>());
}

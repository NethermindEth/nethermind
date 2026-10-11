// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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

    [TestCase("a0", 3, "0503")]
    [TestCase("a8", 6, "a80006")]
    [TestCase("aa80", 9, "a80501")]
    [TestCase("e0", 3, "0703")]
    [TestCase("fc", 6, "fc0006")]
    [TestCase("ff80", 9, "fc0701")]
    [TestCase("abc0", 12, "abc00004")]
    [TestCase("abfe", 15, "abf00707")]
    [TestCase("01ab00", 18, "01ab000002")]
    [TestCase("abcdef", 24, "abcdef0000")]
    [TestCase("ff0000000000000000000000000000000000000000000000000000000000000000", 261, "ff00000000000000000000000000000000000000000000000000000000000000000005")]
    [TestCase("fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff8", 525, "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffc00705")]
    public void Encode_trails_the_six_bit_prefix_with_selector_and_original_alignment(string pathHex, int bitDepth, string keyHex)
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

    [Test]
    public void All_group_depths_and_selectors_round_trip_without_collisions()
    {
        Dictionary<string, PbtStorageNodePath> pathsByKey = [];
        byte[] destination = new byte[PbtNodeGroupKey.MaxLength];
        foreach (byte fill in new byte[] { 0, 0xff, 0xa5 })
        {
            for (int depth = 3; depth <= PbtThreeLevelGroupGeometry.MaxGroupDepth; depth += 3)
            {
                int selectorCount = depth % 6 == 0 ? 1 : 8;
                for (int selector = 0; selector < selectorCount; selector++)
                {
                    byte[] bytes = PathBytes(depth, fill);
                    if (selectorCount > 1) SetSelector(bytes, depth, selector);
                    PbtStorageNodePath path = new(bytes, depth);
                    Array.Fill(destination, (byte)0xff);
                    byte[] key = PbtNodeGroupKey.Encode(path, destination).ToArray();
                    string keyHex = Convert.ToHexString(key);
                    PbtStorageNodePath decoded = PbtNodeGroupKey.Decode(key);
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(decoded, Is.EqualTo(path), $"depth {depth}, fill {fill}, selector {selector}");
                        Assert.That(key.Length, Is.EqualTo(PbtBitPrefix.ByteCount(depth - depth % 6) + 2));
                        Assert.That(key[^2], Is.EqualTo(selector));
                        Assert.That(key[^1], Is.EqualTo(depth & 7));
                        Assert.That(PbtNodeGroupKey.Encode(decoded, destination).ToArray(), Is.EqualTo(key));
                        if (depth <= PbtNodePath.MaxBitDepth)
                            Assert.That(PbtNodeGroupKey.Encode(new PbtNodePath(bytes, depth), destination).ToArray(), Is.EqualTo(key));
                        if (pathsByKey.TryGetValue(keyHex, out PbtStorageNodePath previous))
                            Assert.That(path, Is.EqualTo(previous), $"colliding key {keyHex}");
                    }
                    pathsByKey[keyHex] = path;
                }
            }
        }
        Assert.That(PbtNodeGroupKey.MaxLength, Is.EqualTo(68));
    }

    [Test]
    public void Anchor_and_next_level_groups_share_the_prefix_and_sort_before_the_next_anchor(
        [Values(0, 6, 12, 18, 24, 258, 522)] int anchorDepth)
    {
        byte[] prefix = PathBytes(anchorDepth, 0x80);
        byte[] destination = new byte[PbtNodeGroupKey.MaxLength];
        if (anchorDepth > 0)
        {
            byte[] anchor = PbtNodeGroupKey.Encode(new PbtStorageNodePath(prefix, anchorDepth), destination).ToArray();
            Assert.That(anchor.AsSpan(0, prefix.Length).ToArray(), Is.EqualTo(prefix));
        }

        byte[]? previous = null;
        for (int selector = 0; selector < 8; selector++)
        {
            byte[] bytes = PathBytes(anchorDepth + 3, 0);
            prefix.CopyTo(bytes, 0);
            SetSelector(bytes, anchorDepth + 3, selector);
            byte[] key = PbtNodeGroupKey.Encode(new PbtStorageNodePath(bytes, anchorDepth + 3), destination).ToArray();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(key.AsSpan(0, prefix.Length).ToArray(), Is.EqualTo(prefix));
                if (previous is not null) Assert.That(previous.AsSpan().SequenceCompareTo(key), Is.LessThan(0));
                if (anchorDepth > 0)
                {
                    byte[] nextPrefix = (byte[])prefix.Clone();
                    int lastBit = anchorDepth - 1;
                    nextPrefix[lastBit >> 3] |= (byte)(1 << (7 - (lastBit & 7)));
                    byte[] nextAnchor = PbtNodeGroupKey.Encode(new PbtStorageNodePath(nextPrefix, anchorDepth), destination).ToArray();
                    Assert.That(key.AsSpan().SequenceCompareTo(nextAnchor), Is.LessThan(0));
                }
            }
            previous = key;
        }
    }

    [TestCase("abcdef", 24, "abcdef00", 27, true, TestName = "Byte_aligned_group_precedes_zero_child")]
    [TestCase("abcdef", 24, "abcdef000000", 48, true, TestName = "Byte_aligned_group_precedes_zero_byte_descendant")]
    [TestCase("e0", 3, "e0", 6, true, TestName = "Partial_group_precedes_zero_child")]
    [TestCase("e0", 3, "e4", 6, true, TestName = "Partial_group_precedes_non_zero_child")]
    [TestCase("e0", 3, "e010", 12, true, TestName = "Partial_group_precedes_non_zero_byte_descendant")]
    [TestCase("e0", 3, "e000", 9, true, TestName = "Depth_three_group_precedes_zero_byte_descendant")]
    [TestCase("fc", 6, "fc00", 9, false, TestName = "Six_bit_group_follows_zero_selector_child")]
    [TestCase("fc", 6, "fc80", 9, true, TestName = "Six_bit_group_precedes_nonzero_selector_child")]
    [TestCase("abc0", 12, "abc0", 15, true, TestName = "Depth_twelve_group_precedes_zero_selector_child")]
    [TestCase("01ab00", 18, "01ab00", 21, true, TestName = "Depth_eighteen_group_precedes_zero_selector_child")]
    public void Sorts_a_group_relative_to_its_descendants(string groupHex, int groupDepth, string descendantHex, int descendantDepth, bool groupFirst)
    {
        byte[] group = new PbtStorageNodePath(Bytes.FromHexString(groupHex), groupDepth).ToStorageKey(PbtColumns.StorageNodeGroups);
        byte[] descendant = new PbtStorageNodePath(Bytes.FromHexString(descendantHex), descendantDepth).ToStorageKey(PbtColumns.StorageNodeGroups);
        Assert.That(group.AsSpan().SequenceCompareTo(descendant) < 0, Is.EqualTo(groupFirst));
    }

    [TestCase("", TestName = "Rejects_empty_key")]
    [TestCase("01", TestName = "Rejects_trailer_only_key")]
    [TestCase("0008", TestName = "Rejects_unknown_trailer")]
    [TestCase("0002", TestName = "Rejects_non_group_depth")]
    [TestCase("0000", TestName = "Rejects_root_depth")]
    [TestCase("fd0006", TestName = "Rejects_non_zero_unused_prefix_bits")]
    [TestCase("fc0801", TestName = "Rejects_selector_upper_bits")]
    [TestCase("fc0106", TestName = "Rejects_nonzero_selector_at_anchor_depth")]
    [TestCase("000004", TestName = "Rejects_inconsistent_prefix_length_and_depth")]
    public void Decode_rejects_malformed_keys(string keyHex) =>
        Assert.That(() => PbtNodeGroupKey.Decode(Bytes.FromHexString(keyHex)), Throws.TypeOf<InvalidDataException>());

    [Test]
    public void Decode_rejects_excessive_depth_and_key_length([Values(0, 1)] int extraBytes)
    {
        byte[] key = new byte[PbtNodeGroupKey.MaxLength + extraBytes];
        Assert.That(() => PbtNodeGroupKey.Decode(key), Throws.TypeOf<InvalidDataException>());
    }

    private static byte[] PathBytes(int depth, byte fill)
    {
        byte[] bytes = new byte[PbtBitPrefix.ByteCount(depth)];
        Array.Fill(bytes, fill);
        if ((depth & 7) != 0) bytes[^1] &= (byte)(0xff << (8 - (depth & 7)));
        return bytes;
    }

    private static void SetSelector(byte[] path, int depth, int selector)
    {
        for (int index = 0; index < 3; index++)
        {
            int bitIndex = depth - 3 + index;
            int shift = 7 - (bitIndex & 7);
            path[bitIndex >> 3] = (byte)((path[bitIndex >> 3] & ~(1 << shift)) | (((selector >> (2 - index)) & 1) << shift));
        }
    }
}

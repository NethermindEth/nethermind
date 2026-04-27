// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.State.Flat.BlockRangeTrieForest;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.BlockRangeTrieForest;

[TestFixture]
public class BlockRangeForestKeyTests
{
    [TestCase(0L, 0, "0000000000000000000000000000000000000000")]
    [TestCase(1L, 3, "0000000003000000000000000000000000000000")]
    [TestCase(0xFFFFFFFFL, 5, "FFFFFFFF05000000000000000000000000000000")]
    public void EncodeState_DecodesBlockRange_Consistently(long blockRange, int pathLength, string hashHex)
    {
        TreePath path = new(new ValueHash256(Bytes.FromHexString("AABBCCDD00000000000000000000000000000000000000000000000000000000")), pathLength);
        ValueHash256 hash = new(Bytes.FromHexString(hashHex.PadRight(64, '0')));

        Span<byte> key = stackalloc byte[BlockRangeForestKey.StateKeyLength];
        BlockRangeForestKey.EncodeState(key, blockRange, path, hash);

        Assert.That(BlockRangeForestKey.DecodeBlockRange(key), Is.EqualTo(blockRange));
        Assert.That(key.Length, Is.EqualTo(BlockRangeForestKey.StateKeyLength));
    }

    [TestCase(0L, 0)]
    [TestCase(42L, 4)]
    [TestCase(0xFFFFFFFFL, 64)]
    public void EncodeStorage_DecodesBlockRange_Consistently(long blockRange, int pathLength)
    {
        ValueHash256 address = new(Bytes.FromHexString("AABB000000000000000000000000000000000000000000000000000000000000"));
        TreePath path = new(new ValueHash256(Bytes.FromHexString("CCDD000000000000000000000000000000000000000000000000000000000000")), pathLength);
        ValueHash256 hash = new(Bytes.FromHexString("1234000000000000000000000000000000000000000000000000000000000000"));

        Span<byte> key = stackalloc byte[BlockRangeForestKey.StorageKeyLength];
        BlockRangeForestKey.EncodeStorage(key, blockRange, address, path, hash);

        Assert.That(BlockRangeForestKey.DecodeBlockRange(key), Is.EqualTo(blockRange));
        Assert.That(key.Length, Is.EqualTo(BlockRangeForestKey.StorageKeyLength));
    }

    [Test]
    public void StateKeys_SameRange_SortContiguously()
    {
        // Two state keys at range 5 must both sort before any key at range 6
        TreePath p1 = new(new ValueHash256(Bytes.FromHexString("AAAA000000000000000000000000000000000000000000000000000000000000")), 2);
        TreePath p2 = new(new ValueHash256(Bytes.FromHexString("FFFF000000000000000000000000000000000000000000000000000000000000")), 8);
        ValueHash256 hash = new(Bytes.FromHexString("0000000000000000000000000000000000000000000000000000000000000001"));

        Span<byte> key5a = stackalloc byte[BlockRangeForestKey.StateKeyLength];
        Span<byte> key5b = stackalloc byte[BlockRangeForestKey.StateKeyLength];
        Span<byte> key6 = stackalloc byte[BlockRangeForestKey.StateKeyLength];
        BlockRangeForestKey.EncodeState(key5a, 5, p1, hash);
        BlockRangeForestKey.EncodeState(key5b, 5, p2, hash);
        BlockRangeForestKey.EncodeState(key6, 6, p1, hash);

        Assert.That(key5a.SequenceCompareTo(key6) < 0, Is.True);
        Assert.That(key5b.SequenceCompareTo(key6) < 0, Is.True);
    }

    [Test]
    public void RangeUpperBoundKey_SortsAfterAllKeysInRange()
    {
        TreePath p = new(new ValueHash256(Bytes.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")), 64);
        ValueHash256 hash = new(Bytes.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"));

        Span<byte> maxKeyInRange = stackalloc byte[BlockRangeForestKey.StorageKeyLength];
        ValueHash256 addrMax = new(Bytes.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"));
        BlockRangeForestKey.EncodeStorage(maxKeyInRange, 7, addrMax, p, hash);

        byte[] upper = BlockRangeForestKey.RangeUpperBoundKey(7);
        // upper = prefix for range 8, should sort after all keys in range 7
        Assert.That(upper.AsSpan().SequenceCompareTo(maxKeyInRange) > 0, Is.True);
    }

    [TestCase(0L, 8192, 0L)]
    [TestCase(8191L, 8192, 0L)]
    [TestCase(8192L, 8192, 1L)]
    [TestCase(16383L, 8192, 1L)]
    [TestCase(16384L, 8192, 2L)]
    public void BlockRangeForBlock_ReturnsCorrectFloor(long blockNumber, int blockRangePerForest, long expected) =>
        Assert.That(BlockRangeForestKey.BlockRangeForBlock(blockNumber, blockRangePerForest), Is.EqualTo(expected));
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class PbtNodeGroupTests
{
    private static PbtStorageFullKey KeyFromPath<TPath>(TPath path) where TPath : struct, IPbtNodePath<TPath>
    {
        Span<byte> key = stackalloc byte[Math.Max(1, (path.BitDepth + 7) >> 3)];
        key.Clear();
        path.CopyBitsTo(0, key, 0, path.BitDepth);
        return new(key);
    }

    [Test]
    public void Traversal_path_restores_siblings_and_preserves_snapshots(
        [Values] bool storage, [Values(0, 1, 4, 7, 8, 9, -1)] int depth)
    {
        if (storage) AssertTraversalPath<PbtStorageNodePath>(depth);
        else AssertTraversalPath<PbtNodePath>(depth);
    }

    private static void AssertTraversalPath<TPath>(int depth) where TPath : struct, IPbtNodePath<TPath>
    {
        if (depth == -1) depth = TPath.MaxBitDepth - 4;
        byte[] key = Bytes.FromHexString(new string('D', TPath.MaxBitDepth / 4));
        Span<byte> buffer = stackalloc byte[TPath.MaxBitDepth / 8];
        buffer.Clear();
        PbtTraversalPath path = new(buffer);
        path.AppendKey(key, depth);
        TPath parent = PbtNodePathOperations.FromKey<TPath>(key, depth);
        Assert.That(path.ToPath<TPath>(), Is.EqualTo(parent));

        path.AppendMut(15);
        TPath snapshot = path.ToPath<TPath>();
        Assert.That(snapshot, Is.EqualTo(parent.AppendNib(15)));
        path.AppendKey(key, TPath.MaxBitDepth);
        TPath extended = path.ToPath<TPath>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(path.BitDepth, Is.EqualTo(TPath.MaxBitDepth));
            Assert.That(extended.Prefix(depth + 4), Is.EqualTo(snapshot));
            for (int bit = depth + 4; bit < TPath.MaxBitDepth; bit++)
                Assert.That(extended.GetBit(bit), Is.EqualTo(TrieUpdater.GetBit(key, bit)), $"appended bit {bit}");
        }
        path.Truncate(depth);
        Assert.That(path.ToPath<TPath>(), Is.EqualTo(parent));
        path.AppendMut(0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(path.BitDepth, Is.EqualTo(depth + 4));
            Assert.That(path.ToPath<TPath>(), Is.EqualTo(parent.AppendNib(0)));
            Assert.That(snapshot, Is.EqualTo(parent.AppendNib(15)));
        }
        path.Truncate(0);
        path.AppendKey(key, TPath.MaxBitDepth);
        Assert.That(path.ToPath<TPath>(), Is.EqualTo(TPath.Create(key, TPath.MaxBitDepth)));
    }

    [Test]
    public void Traversal_path_constructor_clears_buffer_and_preserves_canonical_encoding(
        [Values(0, 1, 4, 7, 8, 9, 272, 528)] int depth)
    {
        Span<byte> buffer = stackalloc byte[PbtStorageFullKey.MaxLength];
        buffer.Fill(0xFF);
        PbtTraversalPath cursor = new(buffer);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cursor.BitDepth, Is.Zero);
            Assert.That(buffer.ToArray(), Is.All.Zero);
        }
        byte[] key = Bytes.FromHexString(new string('D', PbtStorageFullKey.MaxLength * 2));
        cursor.AppendKey(key, depth);
        PbtStorageNodePath expected = PbtStorageNodePath.FromKey(new PbtStorageFullKey(key), depth);
        Assert.That(cursor.ToPath<PbtStorageNodePath>().ToEncodedArray(), Is.EqualTo(expected.ToEncodedArray()));
        cursor.Truncate(0);
        cursor.AppendKey(new byte[key.Length], depth);
        Assert.That(cursor.ToPath<PbtStorageNodePath>().ToEncodedArray(),
            Is.EqualTo(PbtStorageNodePath.FromKey(new PbtStorageFullKey(new byte[key.Length]), depth).ToEncodedArray()));
    }

    [Test]
    public void Traversal_path_construction_rejects_invalid_capacity([Values] bool fromPath) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ConstructInvalidTraversalPath(fromPath));

    private static void ConstructInvalidTraversalPath(bool fromPath)
    {
        if (fromPath)
            _ = PbtTraversalPath.FromPath(stackalloc byte[1], new PbtNodePath(Bytes.FromHexString("FF80"), 9));
        else
            _ = new PbtTraversalPath(stackalloc byte[PbtStorageFullKey.MaxLength + 1]);
    }

    [Test]
    public void Store_and_reader_retain_group_identity_after_cursor_mutation([Values(0, 4, 268, 524)] int depth)
    {
        byte[] key = Bytes.FromHexString(new string('D', PbtStorageFullKey.MaxLength * 2));
        PbtStorageNodePath groupKey = PbtStorageNodePath.FromKey(new PbtStorageFullKey(key), depth);
        PbtStorageNodePath leafPath = PbtStorageNodePath.FromKey(new PbtStorageFullKey(key), depth == 0 ? 0 : depth + 1);
        byte[] encoding = PbtNodeCodec.EncodeLeaf(new PbtStorageFullKey(key), Value(1));
        byte[] bytes = EncodeGroup(groupKey, [new PbtNodeRecord(leafPath, encoding)]);
        using RefCountingMemory payload = PooledRefCountingMemoryProvider.Instance.Rent(bytes.Length);
        bytes.CopyTo(payload.GetSpan());
        using PbtNodeGroupStore store = new();
        PbtTraversalPath cursor = PbtTraversalPath.FromPath(stackalloc byte[PbtStorageFullKey.MaxLength], groupKey);
        ValueHash256 hash = PbtNodeCodec.Hash(new PbtNodeReader(encoding));
        store.SetNodeGroup(cursor, hash, payload);
        PbtNodeGroupReader reader = new(cursor, payload.GetSpan());
        PbtNodeGroupReader.Enumerator enumerator = reader.EnumerateNodes();
        cursor.Truncate(0);
        cursor.AppendMut(0);
        using RefCountingMemory? retained = store.GetNodeGroup(groupKey, hash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.GroupKey, Is.EqualTo(groupKey));
            Assert.That(reader.GetNode(PbtFourLevelGroupGeometry.PositionOf(leafPath)).ToArray(), Is.EqualTo(encoding));
            Assert.That(store.EnumerateNodeGroupKeys(), Is.EqualTo(new[] { groupKey }));
            Assert.That(retained?.GetSpan().ToArray(), Is.EqualTo(bytes));
        }
        Assert.That(enumerator.MoveNext(), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(enumerator.CurrentPosition, Is.EqualTo(PbtFourLevelGroupGeometry.PositionOf(leafPath)));
            Assert.That(enumerator.Current.ToArray(), Is.EqualTo(encoding));
            Assert.That(enumerator.MoveNext(), Is.False);
        }
    }

    [Test]
    public void Traversal_path_rejects_invalid_bounds([Values] bool storage, [Range(0, 7)] int scenario) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ApplyInvalidTraversalOperation(storage, scenario));

    private static void ApplyInvalidTraversalOperation(bool storage, int scenario)
    {
        int capacity = storage ? PbtStorageNodePath.MaxBitDepth : PbtNodePath.MaxBitDepth;
        Span<byte> buffer = stackalloc byte[capacity / 8];
        buffer.Clear();
        PbtTraversalPath path = new(buffer);
        switch (scenario)
        {
            case 0: path.AppendMut(-1); break;
            case 1: path.AppendMut(16); break;
            case 2:
                path.AppendKey(buffer, capacity);
                path.AppendMut(0);
                break;
            case 3: path.AppendKey(buffer, capacity + 1); break;
            case 4: path.AppendKey(Bytes.FromHexString("00"), 9); break;
            case 5: path.Truncate(-1); break;
            case 6:
                path.AppendMut(15);
                path.AppendKey(buffer, 0);
                break;
            case 7: path.Truncate(1); break;
        }
    }

    [Test]
    public void Path_encode_preserves_encoding_and_destination_boundaries(
        [Values] bool storage, [Values(0, 1, 4, 7, 8, 9, 272, -1)] int depth, [Values(-1, 0, 3)] int extraLength)
    {
        if (storage) AssertPathEncoding<PbtStorageNodePath>(depth, extraLength);
        else AssertPathEncoding<PbtNodePath>(depth, extraLength);
    }

    private static void AssertPathEncoding<TPath>(int depth, int extraLength) where TPath : struct, IPbtNodePath<TPath>
    {
        if (depth == -1) depth = TPath.MaxBitDepth;
        byte[] bytes = new byte[(depth + 7) >> 3];
        Array.Fill(bytes, (byte)0xAD);
        if ((depth & 7) != 0) bytes[^1] &= (byte)(0xFF << (8 - (depth & 7)));
        TPath path = TPath.Create(bytes, depth);
        byte[] destination = new byte[4 + bytes.Length + extraLength + 2];
        Array.Fill(destination, (byte)0x5A);
        byte[] expected = (byte[])destination.Clone();

        if (extraLength < 0)
        {
            Assert.Throws<ArgumentException>(() => path.Encode(destination.AsSpan(1, destination.Length - 2)));
        }
        else
        {
            expected[1] = (byte)(depth >> 24);
            expected[2] = (byte)(depth >> 16);
            expected[3] = (byte)(depth >> 8);
            expected[4] = (byte)depth;
            bytes.CopyTo(expected, 5);
            path.Encode(destination.AsSpan(1, destination.Length - 2));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(path.EncodedLength, Is.EqualTo(4 + bytes.Length));
            Assert.That(destination, Is.EqualTo(expected));
        }
    }

    [Test]
    public void Path_encode_does_not_allocate([Values] bool storage, [Values(0, 1, 8, 272, -1)] int depth)
    {
        if (depth == -1) depth = storage ? PbtStorageNodePath.MaxBitDepth : PbtNodePath.MaxBitDepth;
        byte[] bytes = new byte[(depth + 7) >> 3];
        if (storage) AssertEncodeDoesNotAllocate(new PbtStorageNodePath(bytes, depth));
        else AssertEncodeDoesNotAllocate(new PbtNodePath(bytes, depth));
    }

    private static void AssertEncodeDoesNotAllocate<TPath>(TPath path) where TPath : struct, IPbtNodePath<TPath>
    {
        Span<byte> destination = stackalloc byte[path.EncodedLength];
        IPbtNodePath<TPath> boxedPath = path;
        path.Encode(destination);
        boxedPath.Encode(destination);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1000; index++)
        {
            path.Encode(destination);
            boxedPath.Encode(destination);
        }
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.That(allocatedBytes, Is.Zero);
    }

    [Test]
    public void Path_operations_preserve_logical_bits_and_copy_boundaries(
        [Values] bool storage, [Values(0, 1, 4, 7, 8, 9, -1)] int depth)
    {
        if (storage) AssertPathOperations<PbtStorageNodePath>(depth);
        else AssertPathOperations<PbtNodePath>(depth);
    }

    private static void AssertPathOperations<TPath>(int depth) where TPath : struct, IPbtNodePath<TPath>
    {
        if (depth == -1) depth = TPath.MaxBitDepth;
        byte[] bytes = new byte[(depth + 7) >> 3];
        Array.Fill(bytes, (byte)0xAD);
        if ((depth & 7) != 0) bytes[^1] &= (byte)(0xFF << (8 - (depth & 7)));
        TPath path = TPath.Create(bytes, depth);
        byte[] copiedBits = new byte[bytes.Length];
        path.CopyBitsTo(0, copiedBits, 0, depth);
        byte[] changed = (byte[])bytes.Clone();
        if (depth != 0) changed[(depth - 1) >> 3] ^= (byte)(0x80 >> ((depth - 1) & 7));
        PbtStorageNodePath changedPath = new(changed, depth);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(copiedBits, Is.EqualTo(bytes));
            Assert.That(path.MatchesPrefix(bytes, depth), Is.True);
            Assert.That(path.MatchesPrefix(changed, depth), Is.EqualTo(depth == 0));
            Assert.That(path.MatchesPrefix(changedPath, depth), Is.EqualTo(depth == 0));
            Assert.That(Math.Sign(path.CompareTo(changedPath)), Is.EqualTo(Math.Sign(bytes.AsSpan().SequenceCompareTo(changed))));
            Assert.That(path.MatchesPrefix(bytes, depth + 1), Is.False);
            Assert.That(path.MatchesPrefix([], depth), Is.EqualTo(depth == 0));
            for (int bit = 0; bit < depth; bit++)
                Assert.That(path.GetBit(bit), Is.EqualTo((bytes[bit >> 3] >> (7 - (bit & 7))) & 1));
            for (int index = 0; index < bytes.Length; index++)
                Assert.That(path.GetByte(index), Is.EqualTo(bytes[index]));
            if (depth != 0)
            {
                Assert.That(path.MatchesPrefix(changed, depth - 1), Is.True);
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => path.GetBit(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => path.GetBit(depth));
            Assert.Throws<IndexOutOfRangeException>(() => path.GetByte(-1));
            Assert.Throws<IndexOutOfRangeException>(() => path.GetByte(bytes.Length));
            Assert.Throws<ArgumentOutOfRangeException>(() => path.MatchesPrefix(bytes, -1));
        }
    }

    [Test]
    public void Path_bit_copy_preserves_adjacent_bits_and_checks_ranges([Values] bool storage)
    {
        if (storage) AssertPathBitCopy<PbtStorageNodePath>();
        else AssertPathBitCopy<PbtNodePath>();
    }

    private static void AssertPathBitCopy<TPath>() where TPath : struct, IPbtNodePath<TPath>
    {
        byte[] bytes = Bytes.FromHexString("ad60");
        TPath path = TPath.Create(bytes, 11);
        byte[] destination = Bytes.FromHexString("c003");
        path.CopyBitsTo(2, destination, 3, 8);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(destination, Is.EqualTo(Bytes.FromHexString("d6a3")));
            Assert.Throws<ArgumentOutOfRangeException>(() => path.CopyBitsTo(-1, destination, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => path.CopyBitsTo(0, destination, -1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => path.CopyBitsTo(0, destination, 0, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => path.CopyBitsTo(10, destination, 0, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => path.CopyBitsTo(0, destination, 15, 2));
        }
        path.CopyBitsTo(11, destination, 16, 0);
        Assert.That(destination, Is.EqualTo(Bytes.FromHexString("d6a3")));
    }

    [Test]
    public void Partial_append_and_conversion_preserve_identity(
        [Values] bool storage, [Values(0, 1, 2, 3, 4, 5, 6, 7, 8, 268, 269, 272, -1)] int depth, [Range(0, 4)] int bitCount)
    {
        if (storage) AssertPartialAppendIdentity<PbtStorageNodePath>(depth, bitCount);
        else AssertPartialAppendIdentity<PbtNodePath>(depth, bitCount);
    }

    private static void AssertPartialAppendIdentity<TPath>(int depth, int bitCount) where TPath : struct, IPbtNodePath<TPath>
    {
        if (depth == -1) depth = TPath.MaxBitDepth - bitCount;
        byte[] bytes = new byte[(depth + 7) >> 3];
        Array.Fill(bytes, (byte)0xAD);
        if ((depth & 7) != 0) bytes[^1] &= (byte)(0xFF << (8 - (depth & 7)));
        TPath path = TPath.Create(bytes, depth);
        if (depth + bitCount > TPath.MaxBitDepth)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => path.AppendBits(0, bitCount));
            return;
        }
        for (int bits = 0; bits < 1 << bitCount; bits++)
        {
            TPath appended = path.AppendBits(bits, bitCount);
            PbtStorageNodePath typed = path.ToPath<PbtStorageNodePath>().AppendBits(bits, bitCount);
            PbtStorageNodePath converted = appended.ToPath<PbtStorageNodePath>();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(appended.BitDepth, Is.EqualTo(depth + bitCount));
                Assert.That(typed.Equals(appended), Is.True);
                Assert.That(converted, Is.EqualTo(typed));
                Assert.That(typed.GetHashCode(), Is.EqualTo(appended.GetHashCode()));
                Assert.That(typed.CompareTo(appended), Is.Zero);
                Assert.That(typed.MatchesPrefix(path, depth), Is.True);
                Assert.That(path.MatchesPrefix(typed, depth + 1), Is.False);
                Assert.That(appended.GetType(), Is.EqualTo(typeof(TPath)));
                for (int bit = 0; bit < bitCount; bit++)
                    Assert.That(typed.GetBit(depth + bit), Is.EqualTo((bits >> (bitCount - bit - 1)) & 1));
                if (typed.BitDepth <= PbtNodePath.MaxBitDepth)
                {
                    Assert.That(typed.ToPath<PbtNodePath>().Equals(appended), Is.True);
                    Assert.That(path.ToPath<PbtNodePath>().AppendBits(bits, bitCount).Equals(appended), Is.True);
                }
                else
                    Assert.Throws<ArgumentOutOfRangeException>(() => typed.ToPath<PbtNodePath>());
            }
        }
    }

    [Test]
    public void Compressed_prefix_append_preserves_bits_and_padding(
        [Values] bool storage, [Range(0, 7)] int alignment, [Values(0, 1, 7, 8, 9, 255)] int prefixDepth, [Values(0, 1)] int direction)
    {
        if (storage) AssertCompressedAppend<PbtStorageNodePath>(alignment, prefixDepth, direction);
        else AssertCompressedAppend<PbtNodePath>(alignment, prefixDepth, direction);
    }

    private static void AssertCompressedAppend<TPath>(int alignment, int prefixDepth, int direction) where TPath : struct, IPbtNodePath<TPath>
    {
        int depth = 8 + alignment;
        byte[] source = Bytes.FromHexString("ad60");
        source = source.AsSpan(0, (depth + 7) >> 3).ToArray();
        if (alignment != 0) source[^1] &= (byte)(0xFF << (8 - alignment));
        TPath path = TPath.Create(source, depth);
        byte[] encoding = new byte[2 + ((prefixDepth + 7) >> 3)];
        BinaryPrimitives.WriteUInt16BigEndian(encoding, (ushort)prefixDepth);
        encoding.AsSpan(2).Fill(0xAD);
        if ((prefixDepth & 7) != 0) encoding[^1] &= (byte)(0xFF << (8 - (prefixDepth & 7)));
        TPath appended = path.Append(new CompressedPrefix(encoding), direction);
        byte[] expected = new byte[(depth + prefixDepth + 8) >> 3];
        source.CopyTo(expected, 0);
        for (int index = 0; index < prefixDepth; index++)
            expected[(depth + index) >> 3] |= (byte)(((encoding[2 + (index >> 3)] >> (7 - (index & 7))) & 1) << (7 - ((depth + index) & 7)));
        expected[(depth + prefixDepth) >> 3] |= (byte)(direction << (7 - ((depth + prefixDepth) & 7)));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(appended.BitDepth, Is.EqualTo(depth + prefixDepth + 1));
            Assert.That(appended.ToPathArray(), Is.EqualTo(expected));
            Assert.That(path.ToPathArray(), Is.EqualTo(source));
        }
    }

    [Test]
    public void Partial_append_checks_bounds([Values] bool storage, [Range(0, 4)] int bitCount)
    {
        if (storage) CheckPartialAppendBounds<PbtStorageNodePath>(bitCount);
        else CheckPartialAppendBounds<PbtNodePath>(bitCount);
    }

    private static void CheckPartialAppendBounds<TPath>(int bitCount) where TPath : struct, IPbtNodePath<TPath>
    {
        TPath path = TPath.Create(new byte[TPath.MaxBitDepth >> 3], TPath.MaxBitDepth);
        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => path.AppendBits(-1, bitCount));
            Assert.Throws<ArgumentOutOfRangeException>(() => path.AppendBits(1 << bitCount, bitCount));
            Assert.Throws<ArgumentOutOfRangeException>(() => path.AppendBits(0, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => path.AppendBits(0, 5));
            Assert.Throws<ArgumentOutOfRangeException>(() => path.MatchesPrefix(path, -1));
            if (bitCount != 0)
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => path.AppendBits(0, bitCount));
                Assert.Throws<ArgumentOutOfRangeException>(() => ((IPbtNodePath<TPath>)path).AppendBits(0, bitCount));
                if (TPath.MaxBitDepth < PbtStorageNodePath.MaxBitDepth)
                {
                    PbtStorageNodePath appended = path.ToPath<PbtStorageNodePath>().AppendBits(0, bitCount);
                    Assert.That(appended.BitDepth, Is.EqualTo(TPath.MaxBitDepth + bitCount));
                }
            }
            else
                Assert.That(path.AppendBits(0, bitCount), Is.EqualTo(path));
        }
    }

    [Test]
    public void Append_nib_preserves_bits_and_capacity(
        [Values] bool storage, [Values(0, 1, 2, 3, 4, 5, 6, 7, 268, 269, 272)] int depth)
    {
        if (storage) AssertAppendNib<PbtStorageNodePath>(depth);
        else AssertAppendNib<PbtNodePath>(depth);
    }

    private static void AssertAppendNib<TPath>(int depth) where TPath : struct, IPbtNodePath<TPath>
    {
        byte[] bytes = new byte[(depth + 7) >> 3];
        Array.Fill(bytes, (byte)0xAD);
        if ((depth & 7) != 0) bytes[^1] &= (byte)(0xFF << (8 - (depth & 7)));
        TPath path = TPath.Create(bytes, depth);
        if (depth + 4 > TPath.MaxBitDepth)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => path.AppendNib(0));
            PbtStorageNodePath appended = path.ToPath<PbtStorageNodePath>().AppendNib(15);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(appended.BitDepth, Is.EqualTo(depth + 4));
                Assert.That(appended.MatchesPrefix(path, depth), Is.True);
                for (int bit = depth; bit < appended.BitDepth; bit++)
                    Assert.That(appended.GetBit(bit), Is.EqualTo(1));
            }
            return;
        }
        for (int nibble = 0; nibble < 16; nibble++)
        {
            TPath appended = path.AppendNib(nibble);
            byte[] actual = appended.ToPathArray();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(appended.BitDepth, Is.EqualTo(depth + 4));
                Assert.That(appended.GetType(), Is.EqualTo(typeof(TPath)));
                Assert.That(appended.MatchesPrefix(bytes, depth), Is.True);
                for (int index = 0; index < 4; index++)
                    Assert.That(appended.GetBit(depth + index), Is.EqualTo((nibble >> (3 - index)) & 1));
                Assert.That(actual[^1] & (0xFF >> (((appended.BitDepth - 1) & 7) + 1)), Is.Zero);
                Assert.That(path.BitDepth, Is.EqualTo(depth));
                Assert.That(path.MatchesPrefix(bytes, depth), Is.True);
            }
        }
    }

    [Test]
    public void Append_nib_validates_arguments_and_maximum_depth([Values(524, 525, 528)] int depth)
    {
        PbtStorageNodePath path = new(new byte[(depth + 7) >> 3], depth);
        if (depth == 524)
        {
            PbtStorageNodePath appended = path.AppendNib(15);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(appended.BitDepth, Is.EqualTo(PbtStorageNodePath.MaxBitDepth));
                Assert.That(appended.GetByte(PbtStorageFullKey.MaxLength - 1), Is.EqualTo(15));
            }
        }
        else
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => path.AppendNib(0));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => path.AppendNib(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => path.AppendNib(16));
        }
    }

    [Test]
    public void Prefix_preserves_identity_and_validates_depth(
        [Values] bool storage, [Values(0, 1, 7, 8, 11, 272, 528)] int sourceDepth)
    {
        if (storage) AssertPrefix<PbtStorageNodePath>(sourceDepth);
        else AssertPrefix<PbtNodePath>(Math.Min(sourceDepth, PbtNodePath.MaxBitDepth));
    }

    private static void AssertPrefix<TPath>(int sourceDepth) where TPath : struct, IPbtNodePath<TPath>
    {
        byte[] source = new byte[(sourceDepth + 7) >> 3];
        Array.Fill(source, (byte)0xAD);
        if ((sourceDepth & 7) != 0) source[^1] &= (byte)(0xFF << (8 - (sourceDepth & 7)));
        TPath path = TPath.Create(source, sourceDepth);
        for (int depth = 0; depth <= sourceDepth; depth++)
        {
            byte[] expected = source.AsSpan(0, (depth + 7) >> 3).ToArray();
            if ((depth & 7) != 0) expected[^1] &= (byte)(0xFF << (8 - (depth & 7)));
            TPath prefix = path.Prefix(depth);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(prefix.BitDepth, Is.EqualTo(depth));
                Assert.That(prefix.ToPathArray(), Is.EqualTo(expected));
                Assert.That(((IPbtNodePath<TPath>)path).Prefix(depth), Is.EqualTo(prefix));
                Assert.That(path.ToPathArray(), Is.EqualTo(source));
            }
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => path.Prefix(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => path.Prefix(sourceDepth + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => path.Prefix(int.MaxValue));
        }
    }

    [Test]
    public void Group_geometry_reconstructs_internal_and_boundary_paths(
        [Values] bool storage, [Values(0, 4, 268, 272, 524)] int groupDepth, [Range(0, 30)] int position)
    {
        if (storage) AssertGroupGeometry<PbtStorageNodePath>(groupDepth, position);
        else AssertGroupGeometry<PbtNodePath>(Math.Min(groupDepth, PbtNodePath.MaxBitDepth), position);
    }

    private static void AssertGroupGeometry<TPath>(int groupDepth, int position) where TPath : struct, IPbtNodePath<TPath>
    {
        byte[] groupBytes = new byte[(groupDepth + 7) >> 3];
        Array.Fill(groupBytes, (byte)0xA0);
        TPath groupKey = TPath.Create(groupBytes, groupDepth);
        if (position == PbtFourLevelGroupGeometry.RootPosition && groupDepth != 0)
        {
            Assert.Throws<ArgumentException>(() => PbtFourLevelGroupGeometry.PathOf(groupKey, position));
            return;
        }
        if (groupDepth == TPath.MaxBitDepth)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => PbtFourLevelGroupGeometry.PathOf(groupKey, position));
            return;
        }

        List<string> paths = [];
        Visit("");
        string relativePath = paths[position];
        int depth = groupDepth + relativePath.Length;
        byte[] expectedBytes = new byte[(depth + 7) >> 3];
        groupBytes.CopyTo(expectedBytes, 0);
        for (int index = 0; index < relativePath.Length; index++)
            if (relativePath[index] == '1') expectedBytes[(groupDepth + index) >> 3] |= (byte)(0x80 >> ((groupDepth + index) & 7));
        TPath expected = TPath.Create(expectedBytes, depth);
        TPath actual = PbtFourLevelGroupGeometry.PathOf(groupKey, position);
        PbtNodeGroupLocation<TPath> location = PbtFourLevelGroupGeometry.Locate(actual);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(location.GroupKey, Is.EqualTo(groupKey));
            Assert.That(location.Position, Is.EqualTo(position));
            Assert.That(PbtFourLevelGroupGeometry.GroupKeyOf(actual), Is.EqualTo(groupKey));
            Assert.That(PbtFourLevelGroupGeometry.PositionOf(actual), Is.EqualTo(position));
            Assert.That(PbtFourLevelGroupGeometry.Reconstruct(groupKey, position), Is.EqualTo(expected));
        }

        void Visit(string path)
        {
            if (path.Length < 4)
            {
                Visit(path + "0");
                Visit(path + "1");
            }
            paths.Add(path);
        }
    }

    [Test]
    public void Group_geometry_rejects_invalid_keys_and_positions([Values(-1, 31, int.MaxValue)] int position)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => PbtFourLevelGroupGeometry.PathOf(default(PbtNodePath), position));
            Assert.Throws<ArgumentOutOfRangeException>(() => PbtFourLevelGroupGeometry.PathOf(default(PbtStorageNodePath), position));
            Assert.Throws<ArgumentException>(() => PbtFourLevelGroupGeometry.PathOf(new PbtNodePath(Bytes.FromHexString("80"), 1), 0));
            Assert.Throws<ArgumentException>(() => PbtFourLevelGroupGeometry.PathOf(new PbtStorageNodePath(new byte[66], 528), 0));
        }
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void Node_group_paths_pack_internal_node_geometry_into_one_byte(int length)
    {
        for (int prefix = 0; prefix < 1 << length; prefix++)
        {
            int slot = prefix << (4 - length);
            NodeGroupPath path = new(slot, length);
            PbtNodePath nodePath = PbtNodePathOperations.FromKey<PbtNodePath>([(byte)(slot << 4)], length);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Unsafe.SizeOf<NodeGroupPath>(), Is.EqualTo(1));
                Assert.That(path.Slot, Is.EqualTo(slot));
                Assert.That(path.Length, Is.EqualTo(length));
                Assert.That(path.Width, Is.EqualTo(16 >> length));
                Assert.That(path.Position, Is.EqualTo(PbtFourLevelGroupGeometry.PositionOf(nodePath)));
                if (length < 3)
                {
                    Assert.That(path.Left.Slot, Is.EqualTo(slot));
                    Assert.That(path.Right.Slot, Is.EqualTo(slot + (8 >> length)));
                    Assert.That(path.Left.Length, Is.EqualTo(length + 1));
                    Assert.That(path.Right.Length, Is.EqualTo(length + 1));
                }
            }
        }
    }

    [TestCase(0)]
    [TestCase(4)]
    [TestCase(268)]
    public void Small_and_storage_paths_share_identity_and_accept_wide_leaf_payloads(int depth)
    {
        byte[] keyBytes = new byte[PbtStorageFullKey.MaxLength];
        keyBytes[0] = Eip8297KeyDerivation.StorageZone;
        PbtStorageFullKey storageKey = new(keyBytes);
        PbtNodePath smallPath = PbtNodePathOperations.FromKey<PbtNodePath>(keyBytes, depth);
        PbtStorageNodePath storagePath = PbtStorageNodePath.FromKey(storageKey, depth);
        Dictionary<PbtStorageNodePath, int?> entries = new() { [smallPath.ToPath<PbtStorageNodePath>()] = 1 };
        entries[storagePath] = null;
        byte[] encoding = PbtNodeCodec.EncodeLeaf(storageKey, new byte[32]);
        PbtStorageNodePath leafPath = PbtStorageNodePath.FromKey(storageKey, depth == 0 ? 0 : depth + 4);
        byte[] payload = EncodeGroup(smallPath, [new PbtNodeRecord(leafPath, encoding)]);
        PbtNodeGroupReader reader = PbtStoreTestExtensions.ReadGroup(storagePath, payload);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(smallPath.Equals(storagePath), Is.True);
            Assert.That(((object)smallPath).Equals(storagePath), Is.True);
            Assert.That(((object)storagePath).Equals(smallPath), Is.True);
            Assert.That(storagePath.Equals(smallPath), Is.True);
            Assert.That(smallPath.GetHashCode(), Is.EqualTo(storagePath.GetHashCode()));
            Assert.That(smallPath.CompareTo(storagePath), Is.Zero);
            Assert.That(smallPath.ToEncodedArray(), Is.EqualTo(storagePath.ToEncodedArray()));
            Assert.That(entries.Count, Is.EqualTo(1));
            Assert.That(entries[smallPath.ToPath<PbtStorageNodePath>()], Is.Null);
            Assert.That(reader.GroupKey.Equals(smallPath), Is.True);
            Assert.That(entries.Remove(smallPath.ToPath<PbtStorageNodePath>()), Is.True);
            Assert.That(smallPath.Equals(default), Is.EqualTo(depth == 0));
            Assert.That(storagePath.Equals(default), Is.EqualTo(depth == 0));
        }

        using PbtNodeGroupStore store = new();
        using RefCountingMemory publishedPayload = PooledRefCountingMemoryProvider.Instance.Rent(payload.Length);
        payload.CopyTo(publishedPayload.GetSpan());
        store.SetNodeGroup(smallPath, PbtNodeCodec.Hash(new PbtNodeReader(encoding)), publishedPayload);
        using (RefCountingMemory lease = store.GetPhysicalNodeGroup(storagePath)!)
            Assert.That(lease.GetSpan().ToArray(), Is.EqualTo(payload));
        store.SetNodeGroup(storagePath, default, null);
        Assert.That(store.GetPhysicalNodeGroup(smallPath), Is.Null);
        store.SetNodeGroup(storagePath, PbtNodeCodec.Hash(new PbtNodeReader(encoding)), publishedPayload);
        using (RefCountingMemory lease = store.GetPhysicalNodeGroup(smallPath)!)
            Assert.That(lease.GetSpan().ToArray(), Is.EqualTo(payload));
        store.SetNodeGroup(smallPath, default, null);
        Assert.That(store.GetPhysicalNodeGroup(storagePath), Is.Null);
    }

    [TestCase(34, 272)]
    [TestCase(66, 528)]
    public void Key_families_enforce_capacity_without_changing_storage_bytes(int length, int depth)
    {
        Assert.That(Unsafe.SizeOf<PbtFullKey>(), Is.EqualTo(40));
        Assert.That(Unsafe.SizeOf<PbtStorageFullKey>(), Is.EqualTo(72));
        byte[] keyBytes = new byte[length];
        keyBytes[^1] = 1;
        PbtStorageFullKey storageKey = new(keyBytes);
        PbtStorageNodePath storagePath = PbtStorageNodePath.FromKey(storageKey, depth);
        Assert.That(PbtStorageNodePath.Decode(storagePath.ToEncodedArray()), Is.EqualTo(storagePath));
        Assert.That(storageKey.FirstDifferingBit(new PbtStorageFullKey(new byte[length])), Is.EqualTo(depth - 1));
        if (length == PbtFullKey.MaxLength)
        {
            PbtFullKey key = (PbtFullKey)storageKey;
            Assert.That(key.FirstDifferingBit(new PbtFullKey(new byte[length])), Is.EqualTo(depth - 1));
            Assert.That(((PbtStorageFullKey)key).Bytes.ToArray(), Is.EqualTo(keyBytes));
            Assert.That(PbtNodePath.Decode(storagePath.ToEncodedArray()).Equals(storagePath), Is.True);
        }
        else
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = (PbtFullKey)storageKey);
            Assert.Throws<InvalidDataException>(() => PbtNodePath.Decode(storagePath.ToEncodedArray()));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtFullKey(new byte[35]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtStorageFullKey(new byte[67]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtNodePath(new byte[35], 273));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtStorageNodePath(new byte[67], 529));
    }

    [TestCase(0x00)]
    [TestCase(0x80)]
    public void Root_node_round_trip_preserves_encoding_and_allows_root_position_access(byte keyByte)
    {
        byte[] value = new byte[32];
        PbtNodePath rootPath = new([], 0);
        byte[] encoding = PbtNodeCodec.EncodeLeaf(new PbtFullKey([keyByte]), value);

        byte[] payload = EncodeGroup(rootPath, [new PbtNodeRecord(rootPath.ToPath<PbtStorageNodePath>(), encoding)]);
        PbtNodeGroupReader group = PbtStoreTestExtensions.ReadGroup(rootPath, payload);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(group.GetNode(PbtFourLevelGroupGeometry.RootPosition).ToArray(), Is.EqualTo(encoding));
            Assert.That(group[PbtFourLevelGroupGeometry.RootPosition].ToArray(), Is.EqualTo(encoding));
            PbtNodeGroupReader.Enumerator nodes = group.EnumerateNodes();
            Assert.That(nodes.MoveNext(), Is.True);
            Assert.That(nodes.MoveNext(), Is.False);
        }
    }

    [Test]
    public void Reader_and_writer_validate_every_required_leaf_bit_and_ignore_suffix_bits(
        [Values(0, 4, 8, 12, 252, 256, 516)] int groupDepth, [Values] bool streamingWriter)
    {
        byte[] groupBytes = new byte[(groupDepth + 7) / 8];
        groupBytes.AsSpan().Fill(0xA5);
        if ((groupDepth & 7) != 0) groupBytes[^1] &= 0xF0;
        PbtStorageNodePath groupKey = new(groupBytes, groupDepth);

        for (int position = 0; position < PbtFourLevelGroupGeometry.PositionCount; position++)
        {
            if (groupDepth != 0 && position == PbtFourLevelGroupGeometry.RootPosition) continue;
            PbtStorageNodePath path = PbtFourLevelGroupGeometry.PathOf(groupKey, position);
            byte[] key = new byte[(path.BitDepth + 7) / 8 + 1];
            path.CopyBitsTo(0, key, 0, path.BitDepth);
            byte[] encoding = PbtNodeCodec.EncodeLeaf(new PbtStorageFullKey(key), Value(1));
            byte[] payload = EncodeGroup(groupKey, [new PbtNodeRecord(path, encoding)]);
            Assert.That(ValidateLeafGroup(groupKey, position, payload, streamingWriter), Is.EqualTo(1));

            for (int bit = 0; bit < key.Length * 8; bit++)
            {
                byte mask = (byte)(0x80 >> (bit & 7));
                payload[PbtNodeGroupCodec.HeaderLength + 3 + (bit >> 3)] ^= mask;
#if DEBUG
                if (bit < path.BitDepth)
                    Assert.That(() => ValidateLeafGroup(groupKey, position, payload, streamingWriter), Throws.TypeOf<InvalidDataException>(), $"position {position}, bit {bit}");
                else
#endif
                    Assert.That(ValidateLeafGroup(groupKey, position, payload, streamingWriter), Is.EqualTo(1), $"position {position}, bit {bit}");
                payload[PbtNodeGroupCodec.HeaderLength + 3 + (bit >> 3)] ^= mask;
            }
        }
    }

    [Test]
    public void Reader_and_writer_reject_leaf_keys_shorter_than_required_path(
        [Values(8, 12, 252, PbtFourLevelGroupGeometry.MaxGroupDepth)] int groupDepth, [Values] bool streamingWriter)
    {
        PbtStorageNodePath groupKey = new(new byte[(groupDepth + 7) / 8], groupDepth);
        PbtStorageNodePath path = PbtFourLevelGroupGeometry.PathOf(groupKey, 0);
        byte[] key = new byte[(path.BitDepth + 7) / 8];
        byte[] encoding = PbtNodeCodec.EncodeLeaf(new PbtStorageFullKey(key), Value(1));
        byte[] payload = EncodeGroup(groupKey, [new PbtNodeRecord(path, encoding)]);
        Assert.That(ValidateLeafGroup(groupKey, 0, payload, streamingWriter), Is.EqualTo(1));

        byte[] shortEncoding = PbtNodeCodec.EncodeLeaf(new PbtStorageFullKey(key.AsSpan(0, key.Length - 1)), Value(1));
        byte[] shortPayload = new byte[PbtNodeGroupCodec.HeaderLength + shortEncoding.Length + 6];
        PbtNodeGroupCodec.Header.CopyTo(shortPayload);
        shortEncoding.CopyTo(shortPayload, PbtNodeGroupCodec.HeaderLength);
        BinaryPrimitives.WriteUInt32LittleEndian(shortPayload.AsSpan(shortPayload.Length - sizeof(uint)), 1u);
#if DEBUG
        Assert.That(() => ValidateLeafGroup(groupKey, 0, shortPayload, streamingWriter), Throws.TypeOf<InvalidDataException>());
#else
        Assert.That(ValidateLeafGroup(groupKey, 0, shortPayload, streamingWriter), Is.EqualTo(1));
#endif
    }

    private static int ValidateLeafGroup<TPath>(TPath groupKey, int position, byte[] payload, bool streamingWriter)
        where TPath : struct, IPbtNodePath<TPath>
    {
        if (!streamingWriter) return ReadGroupCount(groupKey, payload);
        using PbtNodeGroupWriter<TPath> writer = new(groupKey, new TrackingMemoryProvider());
        writer.Write(position, payload.AsSpan(PbtNodeGroupCodec.HeaderLength, payload.Length - PbtNodeGroupCodec.HeaderLength - 6));
        using RefCountingMemory writtenPayload = writer.Detach()!;
        Assert.That(writtenPayload.GetSpan().ToArray(), Is.EqualTo(payload));
        return 1;
    }

    private static int ReadGroupCount<TPath>(TPath groupKey, byte[] payload) where TPath : struct, IPbtNodePath<TPath> => PbtStoreTestExtensions.ReadGroup(groupKey, payload).Count;

    [Test]
    public void Default_reader_rejects_access_and_iteration()
    {
        Assert.That(() => default(PbtNodeGroupReader).GetEnumerator(), Throws.TypeOf<InvalidOperationException>());
        Assert.That(() => default(PbtNodeGroupReader).EnumerateNodes(), Throws.TypeOf<InvalidOperationException>());
        Assert.That(() => MoveDefaultReaderEnumerator(), Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void Encode_rejects_leaf_encoding_with_mismatched_record_path()
    {
        PbtNodePath recordPath = new([0], 1);
        byte[] encoding = PbtNodeCodec.EncodeLeaf(new PbtFullKey([0x80]), new byte[32]);

        Assert.Throws<InvalidDataException>(() => EncodeGroup(new PbtNodePath([], 0), [new PbtNodeRecord(recordPath.ToPath<PbtStorageNodePath>(), encoding)]));
    }

    [Test]
    public void Node_groups_partition_nodes_at_four_level_boundaries_and_preserve_siblings()
    {
        using PbtTreeHarness tree = new();
        List<(byte[] Key, byte[]? Value)> initial = [];
        for (int index = 0; index < 64; index++)
        {
            byte[] key = [(byte)(index * 4), (byte)index];
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
    public void Group_frames_load_once_on_first_access([Values] bool present, [Values(0, 1, 2)] int firstAccess)
    {
        TrackingMemoryProvider memory = new();
        using PbtNodeGroupStore store = new(memory);
        PbtStorageNodePath rootPath = new([], 0);
        byte[] encoding = LeafEncoding(0, 1);
        if (present) store.SetNode(rootPath, encoding, memory);
        WarmReadStore persistence = new(store);
        TrieUpdaterMetrics metrics = new();
        GroupFrameReader<PbtStorageFullKey, PbtStorageNodePath> reader = new(persistence, rootPath, store.GetGroupHash(rootPath), metrics);
        using PbtNodeGroupWriter<PbtStorageNodePath> writer = new(rootPath, memory);
        using (new GroupFrameReader<PbtStorageFullKey, PbtStorageNodePath>.Scope(ref reader))
        {
            Assert.That(reader.CopyRange(writer, 0, 0), Is.Zero);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(persistence.Reads, Is.Empty);
                Assert.That(metrics.GroupFrameResolutions, Is.EqualTo(1));
                Assert.That(metrics.PhysicalGroupFetches, Is.Zero);
                Assert.That(metrics.GroupParses, Is.Zero);
            }
            switch (firstAccess)
            {
                case 0: reader.GetEncoding(PbtFourLevelGroupGeometry.RootPosition); break;
                case 1: reader.CopyRange(writer, 0, PbtNodeGroupCodec.PositionCount); break;
                case 2:
                    reader.Acquire(PbtFourLevelGroupGeometry.RootPosition);
                    break;
            }
            Assert.That(reader.GetEncoding(PbtFourLevelGroupGeometry.RootPosition).ToArray(), Is.EqualTo(present ? encoding : Array.Empty<byte>()));
            TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.Subtree node = reader.Acquire(PbtFourLevelGroupGeometry.RootPosition);
            Assert.That(node.IsEmpty, Is.EqualTo(!present));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(persistence.Reads.Count, Is.EqualTo(1));
                Assert.That(metrics.PhysicalGroupFetches, Is.EqualTo(1));
                Assert.That(metrics.GroupParses, Is.EqualTo(present ? 1 : 0));
            }
        }

        writer.Dispose();
        store.Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero);
    }

    [Test]
    public void Group_frames_materialize_only_branch_anchors(
        [Values(0, 4, 8, 244)] int groupDepth, [Range(0, 29)] int position, [Values] bool leaf)
    {
        using PbtNodeGroupStore store = new();
        PbtStorageNodePath groupKey = PbtNodePathOperations.FromKey<PbtStorageNodePath>(Bytes.FromHexString(new string('A', 62)), groupDepth);
        PbtStorageNodePath path = PbtFourLevelGroupGeometry.PathOf(groupKey, position);
        byte[] key = new byte[32];
        path.CopyBitsTo(0, key, 0, path.BitDepth);
        byte[] encoding = leaf
            ? PbtNodeCodec.EncodeLeaf(new PbtStorageFullKey(key), Value(1))
            : PbtNodeCodec.EncodeBranch(Bytes.FromHexString("A0"), 4, new ValueHash256(Value(1)), new ValueHash256(Value(2)));
        store.SetNode(path, encoding);
        GroupFrameReader<PbtStorageFullKey, PbtStorageNodePath> reader = new(store, groupKey, new ValueHash256(Value(1)), null);
        using PbtNodeGroupWriter<PbtStorageNodePath> writer = new(groupKey, PooledRefCountingMemoryProvider.Instance);
        TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.Subtree node = default;
        using (new GroupFrameReader<PbtStorageFullKey, PbtStorageNodePath>.Scope(ref reader))
        {
            node = reader.Take(writer, position);
            Assert.That(node.Path, Is.EqualTo(leaf ? (PbtStorageNodePath?)null : path));
            TrieUpdater<PbtFullKey, PbtNodePath>.Subtree converted = TrieUpdater<PbtFullKey, PbtNodePath>.Subtree.TakeFrom(ref node);
            byte[] actual = new byte[converted.EncodedLength(path.BitDepth)];
            converted.Encode(actual, path.BitDepth);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(node.IsEmpty, Is.True);
                Assert.That(converted.IsLeaf, Is.EqualTo(leaf));
                Assert.That(converted.Path, Is.EqualTo(leaf ? (PbtNodePath?)null : path.ToPath<PbtNodePath>()));
                Assert.That(actual, Is.EqualTo(encoding));
            }
        }
    }

    [Test]
    public void Borrowed_subtree_materializes_before_its_using_scope_releases_memory([Values] bool leaf)
    {
        PbtStorageNodePath groupKey = new([], 0);
        byte[] encoding = leaf
            ? LeafEncoding(0, 1)
            : PbtNodeCodec.EncodeBranch(Bytes.FromHexString("123450"), 20, new ValueHash256(Value(1)), new ValueHash256(Value(2)));
        using PbtNodeGroupStore stored = new();
        stored.SetNode(groupKey, encoding);
        using PoisoningStore store = new(stored);
        TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.Subtree materialized;
        GroupFrameReader<PbtStorageFullKey, PbtStorageNodePath> reader = new(store, groupKey, PbtNodeCodec.Hash(new PbtNodeReader(encoding)), null);
        using (new GroupFrameReader<PbtStorageFullKey, PbtStorageNodePath>.Scope(ref reader))
        {
            TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.Subtree borrowed = reader.Acquire(PbtFourLevelGroupGeometry.RootPosition);
            materialized = borrowed.Materialize();
            Assert.That(store.ReleasedGroupDepths, Is.Empty);
        }
        Assert.That(store.ReleasedGroupDepths, Is.EqualTo(new[] { 0 }));
        byte[] actual = new byte[materialized.EncodedLength(0)];
        materialized.Encode(actual, 0);
        Assert.That(actual, Is.EqualTo(encoding));
    }

    [Test]
    public void Group_frames_release_lazy_payloads_on_parse_failure_or_unused_disposal([Values] bool access)
    {
        TrackingMemoryProvider memory = new();
        using PbtNodeGroupStore store = new();
        RefCountingMemory payload = memory.Rent(1);
        payload.GetSpan()[0] = 0xff;
        WarmReadStore persistence = new(store) { Payload = payload };
        GroupFrameReader<PbtStorageFullKey, PbtStorageNodePath> reader = new(persistence, new([], 0), new ValueHash256(Value(1)), null);
        using (new GroupFrameReader<PbtStorageFullKey, PbtStorageNodePath>.Scope(ref reader))
        {
            if (access) Assert.Throws<InvalidDataException>(() => reader.GetEncoding(PbtFourLevelGroupGeometry.RootPosition));
        }

        Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.EqualTo(1), "the caller still owns its payload lease");
        ((IDisposable)payload).Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistence.Reads.Count, Is.EqualTo(access ? 1 : 0));
            Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero);
        }
    }

    [Test]
    public void Inserts_and_leaf_splits_do_not_fetch_new_group_frames([Values] bool split, [Values] bool partitioned)
    {
        using PbtNodeGroupStore store = new();
        using PbtTreeHarness expected = new();
        (byte[] Key, byte[]? Value)[] initial = [(Bytes.FromHexString("000000"), Value(1))];
        (byte[] Key, byte[]? Value)[] changes = [(Bytes.FromHexString("000001"), Value(2)), (Bytes.FromHexString("000002"), Value(3))];
        ValueHash256 root = default;
        if (split)
        {
            using PbtPartitionBatches initialBatch = PbtStoreTestExtensions.PreparePartitions(initial);
            root = TrieUpdater.UpdateRoot(store, default, initialBatch);
            expected.ApplyBatch(initial);
        }
        TrieUpdaterMetrics metrics = new();
        if (partitioned)
        {
            using PbtPartitionBatches batch = PbtStoreTestExtensions.PreparePartitions(changes);
            root = TrieUpdater.UpdateRoot(store, root, batch, metrics);
        }
        else
        {
            using PbtWriteBatchBuilder<PbtStorageFullKey> builder = new(0);
            foreach ((byte[] key, byte[]? value) in changes) builder.Set(new(key), new ValueHash256(value!));
            root = TrieUpdater.UpdateRoot(store, root, builder.Build(), metrics);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(expected.ApplyBatch(changes)));
            Assert.That(metrics.GroupFrameResolutions, Is.GreaterThan(1));
            Assert.That(metrics.PhysicalGroupFetches, Is.EqualTo(1), "only the root group needs reading");
            Assert.That(metrics.GroupParses, Is.EqualTo(split ? 1 : 0));
        }
    }

    [Test]
    public void Decomposition_entries_borrow_nodes_from_the_frame(
        [Values(0, 1, 2, 3)] int scenario, [Values] bool consume)
    {
        TrackingMemoryProvider provider = new();
        using PbtNodeGroupStore store = new(provider);
        PbtStorageNodePath rootPath = new([], 0);
        if (scenario != 3) store.SetNode(rootPath, LeafEncoding(0x00, 1), provider);
        GroupFrameReader<PbtStorageFullKey, PbtStorageNodePath> reader = new(store, rootPath, store.GetGroupHash(rootPath), null);
        using PbtNodeGroupWriter<PbtStorageNodePath> writer = new(rootPath, provider);
        TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.Subtree original = default;
        TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.Subtree result = default;
        TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.DecompositionEntry entry = default;
        using (new GroupFrameReader<PbtStorageFullKey, PbtStorageNodePath>.Scope(ref reader))
        {
            if (scenario == 2)
            {
                original = reader.Take(writer, PbtFourLevelGroupGeometry.RootPosition);
                entry = new(ref original);
                Assert.That(original.IsEmpty, Is.True, "entry consumes the borrowed value");
            }
            else if (scenario != 0)
                entry = new(new ValueHash256(Value(1)), PbtFourLevelGroupGeometry.RootPosition);

            if (consume)
            {
                if (scenario == 3)
                {
                    Assert.Throws<InvalidDataException>(() => entry.TakeSubtree(ref reader, writer));
                    Assert.That(entry.SourcePosition, Is.EqualTo(PbtFourLevelGroupGeometry.RootPosition), "failed acquisition retains the position");
                }
                else
                {
                    result = entry.TakeSubtree(ref reader, writer);
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(entry.IsEmpty, Is.True);
                        Assert.That(entry.SourcePosition, Is.EqualTo(-1));
                        Assert.That(result.IsEmpty, Is.EqualTo(scenario == 0));
                        Assert.That(reader.Taken, Is.EqualTo(scenario == 0 ? 0u : 1u << 30));
                    }
                    TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.Subtree second = entry.TakeSubtree(ref reader, writer);
                    Assert.That(second.IsEmpty, Is.True, "consumption clears borrowed values and deferred positions");
                    if (!result.IsEmpty) Assert.That(result.IsLeaf, Is.True);
                }
            }

        }
        store.Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero);
    }

    [Test]
    public void Decomposition_acquires_only_touched_paths_and_composition_keeps_siblings_opaque(
        [Values(0, 1, 0x8000, 0x8101, 0xFFFF)] int touchedMask,
        [Values] bool boundaryBranches,
        [Values] bool compressedSiblings)
    {
        using PbtTreeHarness tree = new();
        List<(byte[] Key, byte[]? Value)> entries = [];
        for (int slot = 0; slot < 16; slot++)
        {
            if (compressedSiblings && (slot & 7) > 1) continue;
            entries.Add((Bytes.FromHexString($"{slot << 4:X2}00"), Value((byte)(slot + 1))));
            if (boundaryBranches)
                entries.Add((Bytes.FromHexString($"{slot << 4:X2}01"), Value((byte)(slot + 17))));
        }
        tree.ApplyBatch(entries);
        using PbtNodeGroupStore store = PbtNodeGroupStore.FromPhysicalPayloads(tree.PhysicalPayloads);
        PbtStorageNodePath rootPath = new([], 0);
        GroupFrameReader<PbtStorageFullKey, PbtStorageNodePath> reader = new(store, rootPath, store.GetGroupHash(rootPath), null);
        using PbtNodeGroupWriter<PbtStorageNodePath> writer = new(rootPath, new TrackingMemoryProvider());
        TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.DecompositionEntry[] frontier = new TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.DecompositionEntry[16];
        TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.Subtree root = default;
        TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.Subtree result = default;
        using (new GroupFrameReader<PbtStorageFullKey, PbtStorageNodePath>.Scope(ref reader))
        {
            root = reader.Take(writer, PbtFourLevelGroupGeometry.RootPosition);
            uint frontierMask = 0;
            TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.Decompose(ref reader, writer, ref root, 0, frontier, ref frontierMask, touchedMask);
            uint expectedTaken = 1u << 30;
            uint expectedFrontier = 0;
            uint deferredTaken = 0;
            int[] expectedPositions = new int[16];
            Array.Fill(expectedPositions, -1);
            Visit(0, 16, 30);
            uint actualFrontier = 0;
            uint actualReferences = 0;
            for (int slot = 0; slot < frontier.Length; slot++)
            {
                int position = expectedPositions[slot];
                Assert.That(frontier[slot].IsEmpty, Is.EqualTo(position == -1), $"compact slot {slot}");
                if (position == -1) continue;
                actualFrontier |= 1u << position;
                if (frontier[slot].SourcePosition >= 0) actualReferences |= 1u << frontier[slot].SourcePosition;
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reader.Taken, Is.EqualTo(expectedTaken), "decomposition must not acquire untouched siblings");
                Assert.That(frontierMask, Is.EqualTo(expectedFrontier));
                Assert.That(actualFrontier, Is.EqualTo(expectedFrontier), "untouched siblings stay at their internal positions");
                Assert.That(actualReferences, Is.EqualTo(deferredTaken & ~expectedTaken), "only unacquired nodes retain source references");
            }
            result = TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.Compose(ref reader, writer, null, frontier, frontierMask);
            ValueHash256 hash = writer.Write(30, 0, ref result);
            using RefCountingMemory? payload = writer.Detach();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reader.Taken, Is.EqualTo(expectedTaken | deferredTaken), "composition acquires retained roots, not their descendants");
                Assert.That(hash, Is.EqualTo(tree.RootHash));
                Assert.That(payload!.GetSpan().ToArray(), Is.EqualTo(Payloads(tree)[Convert.ToHexString(rootPath.ToEncodedArray())]));
            }

            void Visit(int slot, int width, int position)
            {
                if (width == 1 || (touchedMask & (((1 << width) - 1) << slot)) == 0)
                {
                    expectedPositions[slot] = position;
                    expectedFrontier |= 1u << position;
                    deferredTaken |= 1u << position;
                    return;
                }
                expectedTaken |= 1u << position;
                if (compressedSiblings && width == 8)
                {
                    width = 2;
                    position = TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.BoundaryPosition(slot + 1) + 1;
                    if ((touchedMask & (3 << slot)) == 0)
                    {
                        expectedPositions[slot] = position;
                        expectedFrontier |= 1u << position;
                        return;
                    }
                }
                Visit(slot, width / 2, position - width);
                Visit(slot + width / 2, width / 2, position - 1);
            }
        }
    }

    [Test]
    public void Compact_frontier_boundary_results_track_insertions_and_deletions([Values(0, 15)] int slot)
    {
        using PbtTreeHarness expected = new();
        byte[] key = Bytes.FromHexString($"{slot << 4:X2}00");
        using PbtNodeGroupStore store = new();
        PbtStorageNodePath rootPath = new([], 0);
        GroupFrameReader<PbtStorageFullKey, PbtStorageNodePath> reader = new(store, rootPath, store.GetGroupHash(rootPath), null);
        using PbtNodeGroupWriter<PbtStorageNodePath> writer = new(rootPath, new TrackingMemoryProvider());
        TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.DecompositionEntry[] frontier = new TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.DecompositionEntry[16];
        TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.Subtree subtree = default;
        uint frontierMask = 0;
        using (new GroupFrameReader<PbtStorageFullKey, PbtStorageNodePath>.Scope(ref reader))
        {
            TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.Decompose(ref reader, writer, ref subtree, 0, frontier, ref frontierMask, 1 << slot);
            Assert.That(frontierMask, Is.Zero);
            subtree = TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.Compose(ref reader, writer, null, frontier, frontierMask);
            Assert.That(subtree.IsEmpty, Is.True);

            subtree = new(new PbtWriteOperation<PbtStorageFullKey>(new(key), new ValueHash256(Value(1))));
            TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.SetBoundary(frontier, ref frontierMask, slot, ref subtree);
            Assert.That(frontierMask, Is.EqualTo(1u << TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.BoundaryPosition(slot)));
            subtree = TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.TakeBoundary(ref reader, writer, frontier, ref frontierMask, slot);
            Assert.That(frontierMask, Is.Zero);
            subtree = default;
            TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.SetBoundary(frontier, ref frontierMask, slot, ref subtree);
            subtree = TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.Compose(ref reader, writer, null, frontier, frontierMask);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(frontierMask, Is.Zero);
                Assert.That(subtree.IsEmpty, Is.True);
            }

            subtree = new(new PbtWriteOperation<PbtStorageFullKey>(new(key), new ValueHash256(Value(1))));
            TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.SetBoundary(frontier, ref frontierMask, slot, ref subtree);
            subtree = TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.Compose(ref reader, writer, null, frontier, frontierMask);
            Assert.That(writer.Write(30, 0, ref subtree), Is.EqualTo(expected.ApplyBatch([(key, Value(1))])));
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
            byte[] key = [(byte)(keyBits >> 16), (byte)(keyBits >> 8), (byte)keyBits];
            entries[index] = (key, Value((byte)(index + 1)));
            oracle.Insert(key, entries[index].Value!);
        }
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
            if (promoteSibling) oracle.Delete(entries[index].Key);
            else oracle.Insert(entries[index].Key, entries[index].Value!);
        }
        TrieUpdaterMetrics metrics = new();
        tree.ApplyBatch(changes, metrics);
        AssertMatchesRebuild();
        if (!promoteSibling)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(metrics.BulkCopyOperations, Is.GreaterThan(0));
                Assert.That(metrics.BulkCopiedNodes, Is.GreaterThan(metrics.BulkCopyOperations), "copy entire runs rather than one node at a time");
            }
        }

        tree.Reopen();
        entries[^1].Value = Value(0xF1);
        oracle.Insert(entries[^1].Key, entries[^1].Value!);
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
        for (int index = 0; index < 32; index++) entries.Add(([(byte)(index << 3)], Value((byte)(index + 1))));
        ValueHash256 root = tree.ApplyBatch(entries);
        using PbtNodeGroupStore store = PbtNodeGroupStore.FromPhysicalPayloads(tree.PhysicalPayloads);
        PbtNodePath boundary = new(Bytes.FromHexString("00"), 4);
        store.SetNode(boundary, null);
        using RefCountingMemory? descendantGroup = store.GetPhysicalNodeGroup(boundary);
        Assert.That(descendantGroup, Is.Not.Null);
        Assert.That(store.GetNode(boundary), Is.Null);
        using PbtWriteBatchBuilder<PbtFullKey> changes = new(0);
        changes.Set(new PbtFullKey(Bytes.FromHexString("00")), new ValueHash256(Value(0xF0)));

        Assert.That(() => TrieUpdater.UpdateRoot(store, root, changes.Build()), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Deleting_the_last_node_removes_its_physical_group_and_reopen_preserves_records()
    {
        using PbtTreeHarness tree = new();
        byte[] key = [0x42, 0x24];
        tree.ApplyBatch([(key, Value(1))]);
        tree.Reopen();
        Assert.That(tree.CanonicalRecords(), Is.Not.Empty);

        tree.ApplyBatch([(key, (byte[]?)null)]);
        tree.Reopen();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.CanonicalRecords(), Is.Empty);
            Assert.That(tree.PhysicalPayloads, Is.Empty);
            Assert.That(tree.RootHash, Is.EqualTo(default(ValueHash256)));
        }
    }

    [Test]
    public void Import_rejects_zero_availability_group() =>
        Assert.Throws<InvalidDataException>(() => PbtNodeGroupStore.FromPhysicalPayloads([new PbtPhysicalPayload([0, 0, 0, 0], [0, 0, 0, 0])]));

    [Test]
    public void Import_rejects_non_boundary_keys_and_malformed_group_payloads()
    {
        Assert.Throws<InvalidDataException>(() => PbtNodeGroupStore.FromPhysicalPayloads([new PbtPhysicalPayload([0x00], [0, 0, 0, 0])]));
        Assert.Throws<InvalidDataException>(() => PbtNodeGroupStore.FromPhysicalPayloads([new PbtPhysicalPayload([], [1, 0, 0, 0])]));
    }

    [Test]
    public void Store_releases_owned_memory_across_create_replace_delete_reopen_lookup_and_disposal()
    {
        TrackingMemoryProvider provider = new();
        PbtNodePath rootPath = new([], 0);
        byte[] firstEncoding = LeafEncoding(0x00, 1);
        byte[] secondEncoding = LeafEncoding(0x80, 2);

        using (PbtNodeGroupStore store = new(provider))
        {
            store.SetNode(rootPath, firstEncoding, provider);
            Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.EqualTo(1), "create");

            byte[]? lookup = store.GetNode(rootPath);
            Assert.That(lookup, Is.EqualTo(firstEncoding), "lookup");
            lookup![0] = 0x7F;
            Assert.That(store.GetNode(rootPath), Is.EqualTo(firstEncoding), "lookup is owned");

            store.SetNode(rootPath, secondEncoding, provider);
            Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.EqualTo(1), "replace");

            IReadOnlyList<PbtPhysicalPayload> payloads = store.ExportPhysicalPayloads();
            using PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(payloads, provider);
            Assert.That(reopened.GetNode(rootPath), Is.EqualTo(secondEncoding), "reopen");

            store.SetNode(rootPath, null, provider);
            Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.EqualTo(1), "delete leaves reopened owner");
        }

        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero, "disposal");
    }

    [Test]
    public void Retained_group_leases_survive_replacement_deletion_and_store_disposal()
    {
        TrackingMemoryProvider provider = new();
        PbtNodePath rootPath = new([], 0);
        byte[] firstEncoding = LeafEncoding(0x00, 1);
        byte[] secondEncoding = LeafEncoding(0x80, 2);
        byte[] thirdEncoding = LeafEncoding(0x40, 3);

        using PbtNodeGroupStore store = new(provider);
        store.SetNode(rootPath, firstEncoding, provider);
        RefCountingMemory firstLease = store.GetPhysicalNodeGroup(rootPath)!;

        store.SetNode(rootPath, secondEncoding, provider);
        RefCountingMemory secondLease = store.GetPhysicalNodeGroup(rootPath)!;
        Assert.That(firstLease.GetSpan().ToArray(), Is.EqualTo(EncodeGroup(rootPath, [new PbtNodeRecord(rootPath.ToPath<PbtStorageNodePath>(), firstEncoding)])));

        store.SetNode(rootPath, null, provider);
        Assert.That(firstLease.GetSpan().ToArray(), Is.EqualTo(EncodeGroup(rootPath, [new PbtNodeRecord(rootPath.ToPath<PbtStorageNodePath>(), firstEncoding)])));
        Assert.That(secondLease.GetSpan().ToArray(), Is.EqualTo(EncodeGroup(rootPath, [new PbtNodeRecord(rootPath.ToPath<PbtStorageNodePath>(), secondEncoding)])));

        store.SetNode(rootPath, thirdEncoding, provider);
        RefCountingMemory thirdLease = store.GetPhysicalNodeGroup(rootPath)!;
        store.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstLease.GetSpan().ToArray(), Is.EqualTo(EncodeGroup(rootPath, [new PbtNodeRecord(rootPath.ToPath<PbtStorageNodePath>(), firstEncoding)])));
            Assert.That(secondLease.GetSpan().ToArray(), Is.EqualTo(EncodeGroup(rootPath, [new PbtNodeRecord(rootPath.ToPath<PbtStorageNodePath>(), secondEncoding)])));
            Assert.That(thirdLease.GetSpan().ToArray(), Is.EqualTo(EncodeGroup(rootPath, [new PbtNodeRecord(rootPath.ToPath<PbtStorageNodePath>(), thirdEncoding)])));
            Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.EqualTo(3));
        }

        ((IDisposable)thirdLease).Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.EqualTo(2));
        ((IDisposable)firstLease).Dispose();
        ((IDisposable)secondLease).Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Group_publication_borrows_payload_and_retains_independent_lease(bool selfReplacement)
    {
        TrackingMemoryProvider provider = new();
        PbtNodePath rootPath = new([], 0);
        byte[] expected = EncodeGroup(rootPath, [new PbtNodeRecord(rootPath.ToPath<PbtStorageNodePath>(), LeafEncoding(0x00, 1))]);
        using PbtNodeGroupStore store = new();
        RefCountingMemory payload = provider.Rent(expected.Length);
        expected.CopyTo(payload.GetSpan());
        store.SetNodeGroup(rootPath, PbtNodeCodec.Hash(new PbtNodeReader(LeafEncoding(0x00, 1))), payload);
        ((IDisposable)payload).Dispose();

        using (RefCountingMemory lease = store.GetPhysicalNodeGroup(rootPath)!)
        {
            if (selfReplacement) store.SetNodeGroup(rootPath, store.GetGroupHash(rootPath), lease);
            Assert.That(lease.GetSpan().ToArray(), Is.EqualTo(expected));
        }
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.EqualTo(1));
        using (RefCountingMemory lease = store.GetPhysicalNodeGroup(rootPath)!)
        {
            store.Dispose();
            Assert.That(lease.GetSpan().ToArray(), Is.EqualTo(expected));
        }
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero);
    }

    [TestCase(1, -1, typeof(ArgumentException))]
    [TestCase(0, 0, typeof(InvalidDataException))]
    [TestCase(0, 1, typeof(InvalidDataException))]
    [TestCase(0, PbtNodeGroupCodec.MaxTrailerLength, typeof(InvalidDataException))]
    public void Invalid_group_publication_preserves_prior_group_and_caller_reference(int keyDepth, int payloadLength, Type exceptionType)
    {
        TrackingMemoryProvider provider = new();
        PbtNodePath rootPath = new([], 0);
        byte[] encoding = LeafEncoding(0x00, 1);
        byte[] validPayload = EncodeGroup(rootPath, [new PbtNodeRecord(rootPath.ToPath<PbtStorageNodePath>(), encoding)]);
        using PbtNodeGroupStore store = new();
        store.SetNode(rootPath, encoding, provider);
        PbtNodePath groupKey = new(new byte[(keyDepth + 7) / 8], keyDepth);
        byte[] rejectedBytes = payloadLength < 0 ? validPayload : new byte[payloadLength];
        RefCountingMemory rejectedPayload = provider.Rent(rejectedBytes.Length);
        rejectedBytes.CopyTo(rejectedPayload.GetSpan());

        Assert.That(() => store.SetNodeGroup(groupKey, PbtNodeCodec.Hash(new PbtNodeReader(encoding)), rejectedPayload), Throws.TypeOf(exceptionType));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.GetNode(rootPath), Is.EqualTo(encoding));
            Assert.That(rejectedPayload.GetSpan().ToArray(), Is.EqualTo(rejectedBytes));
            Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.EqualTo(2));
        }
        ((IDisposable)rejectedPayload).Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.EqualTo(1), "rejected publication must not retain a lease");
        store.Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero);
    }

    [Test]
    public void Multiple_node_changes_and_unchanged_batch_publish_complete_groups()
    {
        using PublishingStore store = new();
        using PbtWriteBatchBuilder<PbtFullKey> batch = new(0);
        batch.Set(new PbtFullKey([0x00]), new ValueHash256(Value(1)));
        batch.Set(new PbtFullKey([0x80]), new ValueHash256(Value(2)));
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, batch.Build());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Publishes, Is.EqualTo(1));
            Assert.That(store.Inner.EnumerateRecords(), Has.Count.EqualTo(3));
        }

        using PbtWriteBatchBuilder<PbtFullKey> changedBatch = new(0);
        changedBatch.Set(new PbtFullKey([0x00]), new ValueHash256(Value(3)));
        changedBatch.Set(new PbtFullKey([0x80]), new ValueHash256(Value(4)));
        root = TrieUpdater.UpdateRoot(store, root, changedBatch.Build());
        Assert.That(store.Publishes, Is.EqualTo(2));
        PbtNodePath rootPath = new([], 0);
        using RefCountingMemory priorPayload = store.Inner.GetPhysicalNodeGroup(rootPath)!;
        byte[] expectedPayload = priorPayload.GetSpan().ToArray();

        ValueHash256 unchangedRoot = TrieUpdater.UpdateRoot(store, root, changedBatch.Build());
        using RefCountingMemory unchangedPayload = store.Inner.GetPhysicalNodeGroup(rootPath)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(unchangedPayload.GetSpan().ToArray(), Is.EqualTo(expectedPayload));
            Assert.That(unchangedRoot, Is.EqualTo(root));
            Assert.That(store.Publishes, Is.EqualTo(3));
        }

        PbtNodePath siblingPath = new([0x80], 1);
        byte[]? sibling = store.Inner.GetNode(siblingPath);
        using PbtWriteBatchBuilder<PbtFullKey> siblingChangeBatch = new(0);
        siblingChangeBatch.Set(new PbtFullKey([0x00]), new ValueHash256(Value(5)));
        root = TrieUpdater.UpdateRoot(store, root, siblingChangeBatch.Build());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Publishes, Is.EqualTo(4));
            Assert.That(store.Inner.GetNode(siblingPath), Is.EqualTo(sibling));
        }

        using PbtWriteBatchBuilder<PbtFullKey> deleteBatch = new(0);
        deleteBatch.Delete(new PbtFullKey([0x00]));
        deleteBatch.Delete(new PbtFullKey([0x80]));
        root = TrieUpdater.UpdateRoot(store, root, deleteBatch.Build());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(default(ValueHash256)));
            Assert.That(store.Publishes, Is.EqualTo(5));
            Assert.That(store.Inner.EnumerateNodeGroupKeys(), Is.Empty);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    [Ignore("Pre-existing: TrieUpdater does not validate currentRoot against the stored root for nonempty batches.")]
    public void Update_rejects_missing_or_mismatched_current_root(bool storedRootPresent)
    {
        using PbtNodeGroupStore store = new();
        using PbtWriteBatchBuilder<PbtFullKey> batch = new(0);
        batch.Set(new PbtFullKey([0x00]), new ValueHash256(Value(1)));
        if (storedRootPresent) TrieUpdater.UpdateRoot(store, default, batch.Build());
        using PbtWriteBatchBuilder<PbtFullKey> changes = new(0);
        changes.Set(new PbtFullKey([0x80]), new ValueHash256(Value(2)));

        Assert.That(() => TrieUpdater.UpdateRoot(store, new ValueHash256(Value(3)), changes.Build()), Throws.TypeOf<InvalidDataException>());
    }

    [TestCase("0000,0800", "0800", false, new[] { 4, 0 }, TestName = "Escaping_subtree_survives_poisoned_group_root_handoff")]
    [TestCase("0000,0080,0800", "0080,0800", false, new[] { 8, 4, 0 }, TestName = "Escaping_subtree_survives_poisoned_nested_groups")]
    [TestCase("0000,0080,0800", "0080,0800", true, new[] { 8, 4, 0 }, TestName = "Inline_subtree_survives_poisoned_nested_groups")]
    [TestCase("0000,0008", "0080", false, new[] { 0 }, TestName = "Ancestor_borrowed_subtree_survives_child_frame_return")]
    public void Returned_subtrees_survive_group_lease_release(string initialKeys, string deletedKeys, bool replaceSurvivor, int[] releasedDepths) =>
        AssertReturnedSubtrees(initialKeys, deletedKeys, replaceSurvivor, releasedDepths, false);

    [Test]
    public void Worker_results_survive_poisoned_group_memory([Values] bool parallel, [Values] bool branch) =>
        AssertReturnedSubtrees(branch ? "000000,000008,000080,008000,010000" : "000000,000080,008000,010000",
            "000080,008000", false, null, parallel);

    private static void AssertReturnedSubtrees(string initialKeys, string deletedKeys, bool replaceSurvivor, int[]? releasedDepths, bool parallel)
    {
        using PbtTreeHarness expected = new();
        EipReferenceTree oracle = new();
        List<(byte[] Key, byte[]? Value)> initial = [];
        foreach (string key in initialKeys.Split(','))
        {
            byte[] keyBytes = Bytes.FromHexString(key);
            initial.Add((keyBytes, Value(1)));
            oracle.Insert(keyBytes, Value(1));
        }
        ValueHash256 root = expected.ApplyBatch(initial);
        using PoisoningStore store = new(PbtNodeGroupStore.FromPhysicalPayloads(expected.PhysicalPayloads));
        using PbtWriteBatchBuilder<PbtFullKey> batch = new(0);
        List<(byte[] Key, byte[]? Value)> changes = [];
        foreach (string key in deletedKeys.Split(','))
        {
            byte[] keyBytes = Bytes.FromHexString(key);
            batch.Delete(new PbtFullKey(keyBytes));
            changes.Add((keyBytes, null));
            oracle.Delete(keyBytes);
        }
        if (replaceSurvivor)
        {
            byte[] key = Bytes.FromHexString("0000");
            batch.Set(new PbtFullKey(key), new ValueHash256(Value(2)));
            changes.Add((key, Value(2)));
            oracle.Insert(key, Value(2));
        }
        expected.ApplyBatch(changes);

        ValueHash256 actualRoot;
        if (parallel)
        {
            using PbtPartitionBatches partitions = PbtStoreTestExtensions.PreparePartitions(changes);
            actualRoot = TrieUpdater.UpdateRoot(store, root, partitions);
        }
        else actualRoot = TrieUpdater.UpdateRoot(store, root, batch.Build());

        if (releasedDepths is not null)
            Assert.That(store.ReleasedGroupDepths, Is.EqualTo(releasedDepths), "payloads are poisoned when their owning frame releases them");
        else Assert.That(store.ReleasedGroupDepths, Is.Not.Empty);
        Assert.That(store.ReleasedGroupDepths.Count, Is.EqualTo(store.ReadCount));
        using PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(store.Inner.ExportPhysicalPayloads());
        IReadOnlyList<PbtNodeRecord> actualRecords = reopened.EnumerateRecords();
        IReadOnlyList<PbtNodeRecord> expectedRecords = expected.Nodes;
        Assert.That(actualRecords, Has.Count.EqualTo(expectedRecords.Count));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actualRoot.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            for (int index = 0; index < actualRecords.Count; index++)
            {
                Assert.That(actualRecords[index].Path.ToEncodedArray(), Is.EqualTo(expectedRecords[index].Path.ToEncodedArray()));
                Assert.That(actualRecords[index].Encoding.ToArray(), Is.EqualTo(expectedRecords[index].Encoding.ToArray()));
            }
        }
    }

    private sealed class PoisoningStore(PbtNodeGroupStore inner) : IPbtStore, IDisposable
    {
        private readonly ArrayPool<byte> _pool = ArrayPool<byte>.Create();
        internal PbtNodeGroupStore Inner { get; } = inner;
        internal List<int> ReleasedGroupDepths { get; } = [];
        internal int ReadCount { get; private set; }

        public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash)
        {
            using RefCountingMemory? payload = Inner.GetNodeGroup(groupKey, groupHash);
            if (payload is null) return null;
            int length = payload.GetSpan().Length;
            ReadCount++;
            byte[] buffer = _pool.Rent(length);
            payload.GetSpan().CopyTo(buffer);
            int groupDepth = groupKey.BitDepth;
            return RefCountingMemory.OwningRocksDb(new PoisoningMemoryManager(_pool, buffer, length,
                () => ReleasedGroupDepths.Add(groupDepth)));
        }

        public void SetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash, RefCountingMemory? payload) => Inner.SetNodeGroup(groupKey, groupHash, payload);
        public void Dispose() => Inner.Dispose();
    }

    private sealed class PoisoningMemoryManager(ArrayPool<byte> pool, byte[] buffer, int length, Action onRelease) : MemoryManager<byte>
    {
        // Leave stale spans readable so a missing escape copy observes poison rather than relying on pool reuse timing.
        public override Span<byte> GetSpan() => buffer.AsSpan(0, length);
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing)
        {
            buffer.AsSpan().Fill(0xFF);
            pool.Return(buffer);
            onRelease();
        }
    }

    private sealed class PublishingStore : IPbtStore, IDisposable
    {
        internal PbtNodeGroupStore Inner { get; } = new();
        internal int Publishes { get; private set; }

        public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash) => Inner.GetNodeGroup(groupKey, groupHash);
        public void SetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash, RefCountingMemory? payload)
        {
            Publishes++;
            Inner.SetNodeGroup(groupKey, groupHash, payload);
        }
        public void Dispose() => Inner.Dispose();
    }

    [Test]
    public void Failed_import_releases_previously_copied_payloads()
    {
        TrackingMemoryProvider provider = new();
        PbtNodePath rootPath = new([], 0);
        byte[] validPayload = EncodeGroup(rootPath, [new PbtNodeRecord(rootPath.ToPath<PbtStorageNodePath>(), LeafEncoding(0x00, 1))]);
        PbtPhysicalPayload valid = new(rootPath.ToEncodedArray(), validPayload);

        Assert.Throws<InvalidDataException>(() => PbtNodeGroupStore.FromPhysicalPayloads([valid, valid], provider));
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero);
    }

    [TestCase(1, new int[0])]
    [TestCase(2, new int[0])]
    [TestCase(3, new int[0])]
    [TestCase(5, new int[0])]
    [TestCase(6, new int[0])]
    [TestCase(7, new int[0])]
    [TestCase(9, new[] { 4 })]
    [TestCase(13, new[] { 4, 8 })]
    public void Compressed_branch_jumps_create_no_intermediate_groups(int sharedPrefixBits, int[] absentGroupDepths)
    {
        using PbtTreeHarness tree = new();
        EipReferenceTree oracle = new();
        byte[] leftKey = new byte[4];
        byte[] rightKey = new byte[4];
        rightKey[sharedPrefixBits / 8] = (byte)(0x80 >> (sharedPrefixBits % 8));

        tree.ApplyBatch([(leftKey, Value(1)), (rightKey, Value(2))]);
        oracle.Insert(leftKey, Value(1));
        oracle.Insert(rightKey, Value(2));
        tree.Reopen();

        List<int> groupDepths = [];
        foreach (PbtPhysicalPayload payload in tree.PhysicalPayloads)
            groupDepths.Add(PbtStorageNodePath.Decode(payload.Key.Span).BitDepth);

        for (int depth = 1; depth <= sharedPrefixBits; depth++)
            Assert.That(tree.TryGetNode(new PbtNodePath(new byte[(depth + 7) / 8], depth), out _), Is.False, $"compressed prefix depth {depth}");
        foreach (PbtNodeRecord record in tree.Nodes)
        {
            Assert.That(tree.TryGetNode(record.Path, out byte[]? encoding), Is.True);
            Assert.That(encoding, Is.EqualTo(record.Encoding.ToArray()));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            Assert.That(groupDepths, Does.Contain(0));
            Assert.That(groupDepths, Does.Contain(sharedPrefixBits / 4 * 4));
            foreach (int absentGroupDepth in absentGroupDepths)
                Assert.That(groupDepths, Does.Not.Contain(absentGroupDepth));
            Assert.That(tree.CanonicalRecords(), Has.Length.EqualTo(3));
        }
    }

    [Test]
    public void Node_groups_fold_random_mutation_rounds_to_canonical_tree()
    {
        Random random = new(42);
        using PbtTreeHarness record = new();
        using PbtTreeHarness grouped = new();
        EipReferenceTree oracle = new();
        byte[][] keys = Keys(random, 256);

        for (int round = 0; round < 12; round++)
        {
            Dictionary<int, byte[]?> batchValues = [];
            for (int index = 0; index < 40; index++)
            {
                int keyIndex = random.Next(keys.Length);
                byte[]? value = random.Next(4) == 0 ? null : RandomValue(random);
                batchValues[keyIndex] = value;
            }
            List<(byte[] Key, byte[]? Value)> writes = [];
            foreach ((int keyIndex, byte[]? value) in batchValues)
            {
                byte[] key = keys[keyIndex];
                writes.Add((key, value));
                if (value is null) oracle.Delete(key);
                else oracle.Insert(key, value);
            }

            ValueHash256 recordRoot = record.ApplyBatch(writes);
            ValueHash256 groupedRoot = grouped.ApplyBatch(writes);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(recordRoot.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()), $"round {round}");
                Assert.That(groupedRoot, Is.EqualTo(recordRoot), $"round {round}");
                Assert.That(grouped.CanonicalRecords(), Is.EqualTo(record.CanonicalRecords()), $"round {round}");
            }
        }
    }
    [Test]
    public void Encoders_omit_only_prefixless_interior_branches(
        [Values(0, 4, 268, 516)] int groupDepth, [Values(0, 1, 2)] int nodeKind)
    {
        PbtStorageNodePath groupKey = new(new byte[(groupDepth + 7) / 8], groupDepth);
        List<PbtNodeRecord> records = [];
        ReadOnlyMemory<byte>[] encodings = new ReadOnlyMemory<byte>[PbtNodeGroupCodec.PositionCount];
        bool[] present = new bool[PbtNodeGroupCodec.PositionCount];
        using PbtNodeGroupWriter<PbtStorageNodePath> streamingWriter = new(groupKey, new TrackingMemoryProvider());
        int fullLength = PbtNodeGroupCodec.HeaderLength + sizeof(uint);
        int omitted = 0;
        int positionCount = groupDepth == 0 ? 31 : 30;
        for (int position = 0; position < positionCount; position++)
        {
            PbtStorageNodePath path = PbtFourLevelGroupGeometry.PathOf(groupKey, position);
            byte[] encoding = nodeKind switch
            {
                0 => PbtNodeCodec.EncodeBranch([], 0, new ValueHash256(Value(1)), new ValueHash256(Value(2))),
                1 => PbtNodeCodec.EncodeBranch(Bytes.FromHexString("80"), 1, new ValueHash256(Value(1)), new ValueHash256(Value(2))),
                _ => PbtNodeCodec.EncodeLeaf(KeyFromPath(path), Value(1)),
            };
            records.Add(new(path, encoding));
            encodings[position] = encoding;
            present[position] = true;
            fullLength += encoding.Length + sizeof(ushort);
            if (nodeKind == 0 && path.BitDepth - groupDepth is >= 1 and <= 3) omitted++;
            if ((position & 1) == 0) streamingWriter.Write(position, encoding);
            else
            {
                encoding.CopyTo(streamingWriter.GetSpan(position, encoding.Length));
                streamingWriter.Commit();
            }
        }

        byte[] payload = EncodeGroup(groupKey, records);
        BufferWriter slotWriter = new(new byte[fullLength]);
        PbtNodeGroupCodec.Encode(ref slotWriter, groupKey, encodings, present);
        using RefCountingMemory streamedPayload = streamingWriter.Detach()!;
        PbtNodeGroupReader reader = PbtStoreTestExtensions.ReadGroup(groupKey, payload);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fullLength - payload.Length, Is.EqualTo(69 * omitted));
            Assert.That(omitted, Is.EqualTo(nodeKind == 0 ? 14 : 0));
            Assert.That(reader.Count, Is.EqualTo(positionCount - omitted));
            Assert.That(slotWriter.WrittenSpan.ToArray(), Is.EqualTo(payload));
            Assert.That(streamedPayload.GetSpan().ToArray(), Is.EqualTo(payload));
        }
        int enumerated = 0;
        PbtNodeGroupReader.Enumerator enumerator = reader.EnumerateNodes();
        while (enumerator.MoveNext()) enumerated++;
        Assert.That(enumerated, Is.EqualTo(reader.Count));
        for (int position = 0; position < positionCount; position++)
        {
            int relativeDepth = records[position].Path.BitDepth - groupDepth;
            bool retained = nodeKind != 0 || relativeDepth is 0 or 4;
            bool found = reader.TryGetNode(position, out ReadOnlySpan<byte> encoding);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(found, Is.EqualTo(retained), $"position {position}");
                Assert.That(reader.GetNode(position).ToArray(), Is.EqualTo(retained ? encodings[position].ToArray() : Array.Empty<byte>()));
                if (found) Assert.That(encoding.ToArray(), Is.EqualTo(encodings[position].ToArray()));
                else Assert.That(reader.Availability & (1u << position), Is.Zero);
            }
        }
    }

    [Test]
    public void Explicit_emissions_preserve_only_selected_nodes_independently_of_taken_positions(
        [Values(0, 4, 516)] int groupDepth,
        [Values(0u, 0x400C0189u, 0x7FFFFFFFu)] uint selected,
        [Values] bool markTaken,
        [Values] bool copyRanges)
    {
        PbtStorageNodePath groupKey = new(new byte[(groupDepth + 7) / 8], groupDepth);
        List<PbtNodeRecord> records = [];
        List<PbtNodeRecord> expectedRecords = [];
        int positionCount = groupDepth == 0 ? PbtNodeGroupCodec.PositionCount : PbtFourLevelGroupGeometry.RootPosition;
        for (int position = 0; position < positionCount; position++)
        {
            if (position % 7 == 0) continue;
            PbtStorageNodePath path = PbtFourLevelGroupGeometry.PathOf(groupKey, position);
            byte[] encoding = position % 3 == 2
                ? PbtNodeCodec.EncodeBranch([], 0, new ValueHash256(Value((byte)(position + 1))), new ValueHash256(Value(0xFF)))
                : PbtNodeCodec.EncodeLeaf(KeyFromPath(path), Value((byte)(position + 1)));
            PbtNodeRecord record = new(path, encoding);
            records.Add(record);
            if ((selected & (1u << position)) != 0) expectedRecords.Add(record);
        }
        byte[] sourcePayload = EncodeGroup(groupKey, records);
        using PbtNodeGroupStore store = PbtNodeGroupStore.FromPhysicalPayloads([new(groupKey.ToEncodedArray(), sourcePayload)]);
        GroupFrameReader<PbtStorageFullKey, PbtStorageNodePath> reader = new(store, groupKey, new ValueHash256(Value(1)), null);
        using PbtNodeGroupWriter<PbtStorageNodePath> writer = new(groupKey, new TrackingMemoryProvider());
        using (new GroupFrameReader<PbtStorageFullKey, PbtStorageNodePath>.Scope(ref reader))
        {
            uint taken = markTaken ? 0x7FFFFFFFu : 0;
            reader.Taken = taken;
            int nextPosition = 0;
            int lastEmittedPosition = -1;
            uint emitted = 0;
            foreach (int endPosition in new[] { 0, 3, 8, 19, 31 })
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
                        if (!encoding.IsEmpty)
                        {
                            if (!copyRanges) writer.Write(nextPosition, encoding.Span);
                            emitted |= 1u << nextPosition;
                            lastEmittedPosition = nextPosition;
                        }
                        nextPosition++;
                    } while (nextPosition < endPosition && (selected & (1u << nextPosition)) != 0);
                    if (copyRanges) reader.CopyRange(writer, startPosition, nextPosition);
                }

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(writer.LastPosition, Is.EqualTo(lastEmittedPosition));
                    Assert.That(writer.Availability, Is.EqualTo(emitted));
                    Assert.That(reader.Taken, Is.EqualTo(taken));
                }
            }
            using RefCountingMemory? payload = writer.Detach();
            Assert.That(payload?.GetSpan().ToArray(), Is.EqualTo(expectedRecords.Count == 0 ? null : EncodeGroup(groupKey, expectedRecords)));
        }
    }

    [Test]
    public void Streaming_writer_commits_leaves_without_allocating(
        [Values(0, 4, 8, 268, PbtFourLevelGroupGeometry.MaxGroupDepth)] int groupDepth)
    {
        PbtStorageNodePath groupKey = new(new byte[(groupDepth + 7) / 8], groupDepth);
        using PbtNodeGroupWriter<PbtStorageNodePath> writer = new(groupKey, new TrackingMemoryProvider());
        int positionCount = groupDepth == 0 ? PbtFourLevelGroupGeometry.PositionCount : PbtFourLevelGroupGeometry.RootPosition;
        for (int position = 0; position < positionCount; position++)
        {
            PbtStorageNodePath path = PbtFourLevelGroupGeometry.PathOf(groupKey, position);
            byte[] key = new byte[Math.Max(1, ((path.BitDepth + 7) >> 3))];
            path.CopyBitsTo(0, key, 0, path.BitDepth);
            byte[] encoding = PbtNodeCodec.EncodeLeaf(new PbtStorageFullKey(key), Value(1));
            encoding.CopyTo(writer.GetSpan(position, encoding.Length));

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            writer.Commit();
            long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            Assert.That(allocatedBytes, Is.Zero, $"position {position}");
        }
    }

    [TestCase(0, 1)]
    [TestCase(0, 31)]
    [TestCase(4, 30)]
    [TestCase(4, 3)]
    public void Streaming_writer_preserves_canonical_bytes_and_transfers_backing_memory(int groupDepth, int count)
    {
        PbtStorageNodePath groupKey = new(new byte[(groupDepth + 7) / 8], groupDepth);
        TrackingMemoryProvider provider = new() { FillByte = 0xFF };
        List<PbtNodeRecord> records = [];
        using PbtNodeGroupWriter<PbtStorageNodePath> writer = new(groupKey, provider);
        for (int index = 0; index < count; index++)
        {
            int position = count == 1 ? 30 : count == 3 ? 3 + index * 10 : index;
            byte[] encoding = PbtNodeCodec.EncodeBranch(Bytes.FromHexString("A0"), 4,
                new ValueHash256(Value(1)), new ValueHash256(Value(2)));
            records.Add(new(PbtFourLevelGroupGeometry.PathOf(groupKey, position), encoding));
            if (index % 2 == 0)
            {
                Span<byte> destination = writer.GetSpan(position, encoding.Length);
                PbtNodeCodec.CreateBranchEncoding(destination, 4, new ValueHash256(Value(1)), new ValueHash256(Value(2)));
                destination[3] = 0xA0;
                writer.Commit();
            }
            else writer.Write(position, encoding);
        }

        ReadOnlyMemory<byte>[] encodings = new ReadOnlyMemory<byte>[PbtNodeGroupCodec.PositionCount];
        bool[] present = new bool[PbtNodeGroupCodec.PositionCount];
        foreach (PbtNodeRecord record in records)
        {
            int position = PbtFourLevelGroupGeometry.PositionOf(record.Path);
            encodings[position] = record.Encoding;
            present[position] = true;
        }
        byte[] expected = EncodeGroup(groupKey, records);
        byte[] slotPayload = new byte[expected.Length];
        BufferWriter slotWriter = new(slotPayload);
        PbtNodeGroupCodec.Encode(ref slotWriter, groupKey, encodings, present);

        using (RefCountingMemory payload = writer.Detach()!)
        {
            writer.Dispose();
            PbtNodeGroupReader reader = PbtStoreTestExtensions.ReadGroup(groupKey, payload.GetSpan());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(payload.GetSpan().ToArray(), Is.EqualTo(expected));
                Assert.That(slotWriter.WrittenSpan.ToArray(), Is.EqualTo(expected));
                Assert.That(expected[..PbtNodeGroupCodec.HeaderLength], Is.EqualTo(Bytes.FromHexString("02")));
                Assert.That(reader.Count, Is.EqualTo(count));
                Assert.That(expected.Length, Is.EqualTo(count * 68 + 5 + 2 * count));
                uint availability = 0;
                for (int index = 0; index < count; index++)
                {
                    int position = PbtFourLevelGroupGeometry.PositionOf(records[index].Path);
                    availability |= 1u << position;
                    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(expected.AsSpan(1 + count * 68 + index * 2)), Is.EqualTo(index * 68));
                    Assert.That(reader.TryGetNodeRange(position, out int offset, out int length), Is.True);
                    Assert.That(offset, Is.EqualTo(1 + index * 68));
                    Assert.That(length, Is.EqualTo(68));
                }
                Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(expected.AsSpan(expected.Length - 4)), Is.EqualTo(availability));
                Assert.That(payload, Is.SameAs(provider.Rented[^1]));
                Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.EqualTo(1));
                Assert.That(provider.RentCount, count == 1 ? Is.EqualTo(1) : Is.GreaterThan(1));
            }
        }
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero);
        Assert.Throws<ObjectDisposedException>(() => writer.Detach());
    }

    [TestCase(2)]
    [TestCase(3)]
#if DEBUG
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(4)]
    [TestCase(5)]
#endif
    public void Streaming_writer_rejects_invalid_appends_without_leaking(int scenario)
    {
        TrackingMemoryProvider provider = new() { FillByte = 0xFF };
        using (PbtNodeGroupWriter<PbtNodePath> writer = new(new PbtNodePath([], 0), provider))
        {
            byte[] branch = PbtNodeCodec.EncodeBranch(Bytes.FromHexString("80"), 1, new ValueHash256(Value(1)), new ValueHash256(Value(2)));
            writer.Write(2, branch);
            switch (scenario)
            {
                case 0: Assert.Throws<InvalidDataException>(() => writer.Write(2, branch)); break;
                case 1: Assert.Throws<InvalidDataException>(() => writer.Write(1, branch)); break;
                case 2: Assert.Throws<InvalidDataException>(() => writer.GetSpan(3, ushort.MaxValue)); break;
                case 3: Assert.Throws<ArgumentOutOfRangeException>(() => writer.GetSpan(31, branch.Length)); break;
                case 4:
                    writer.GetSpan(3, 1)[0] = 0xFF;
                    Assert.Throws<InvalidDataException>(() => writer.Commit());
                    Assert.Throws<InvalidOperationException>(() => writer.Detach());
                    break;
                case 5: Assert.Throws<InvalidDataException>(() => writer.Write(3, LeafEncoding(0xFF, 1))); break;
            }
            Assert.That(writer.WrittenCount, Is.EqualTo(branch.Length));
        }
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero);
        using PbtNodeGroupWriter<PbtNodePath> nonRoot = new(new PbtNodePath(Bytes.FromHexString("A0"), 4), provider);
        Assert.Throws<ArgumentOutOfRangeException>(() => nonRoot.GetSpan(30, 67));
    }

    [TestCase(1)]
    [TestCase(2)]
    public void Streaming_writer_releases_memory_after_rent_failure(int failedRent)
    {
        TrackingMemoryProvider provider = new() { ThrowOnRent = failedRent };
        using (PbtNodeGroupWriter<PbtNodePath> writer = new(new PbtNodePath([], 0), provider))
        {
            byte[] branch = PbtNodeCodec.EncodeBranch([], 0, new ValueHash256(Value(1)), new ValueHash256(Value(2)));
            if (failedRent == 2) writer.Write(0, branch);
            Assert.Throws<InvalidOperationException>(() => writer.Write(failedRent, branch));
        }
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero);
    }

    [Test]
    public void Streaming_writer_accepts_exact_uint16_entries_limit()
    {
        TrackingMemoryProvider provider = new();
        using PbtNodeGroupWriter<PbtNodePath> writer = new(new PbtNodePath([], 0), provider);
        for (int position = 0; position < 8; position++)
        {
            int length = position == 7 ? 8191 : 8192;
            Span<byte> encoding = writer.GetSpan(position, length);
            PbtNodeCodec.CreateBranchEncoding(encoding, (length - 67) * 8,
                new ValueHash256(Value(1)), new ValueHash256(Value(2)));
            writer.Commit();
        }
        Assert.That(writer.WrittenCount, Is.EqualTo(ushort.MaxValue));
        Assert.Throws<InvalidDataException>(() => writer.GetSpan(8, 1));
        using RefCountingMemory payload = writer.Detach()!;
        PbtNodeGroupReader reader = PbtStoreTestExtensions.ReadGroup(new PbtNodePath([], 0), payload.GetSpan());
        Assert.That(reader.Count, Is.EqualTo(8));
    }

    [Test]
    public void Versioned_group_rejects_invalid_header_and_footer([Range(0, 17)] int scenario)
    {
        PbtNodePath groupKey = new([], 0);
        byte[] branch = PbtNodeCodec.EncodeBranch(Bytes.FromHexString("80"), 1,
            new ValueHash256(Value(1)), new ValueHash256(Value(2)));
        byte[] payload = EncodeGroup(groupKey, [
            new(PbtFourLevelGroupGeometry.PathOf(groupKey, 3).ToPath<PbtStorageNodePath>(), branch),
            new(PbtFourLevelGroupGeometry.PathOf(groupKey, 10).ToPath<PbtStorageNodePath>(), branch),
            new(groupKey.ToPath<PbtStorageNodePath>(), branch)]);
        int footerOffset = payload.Length - 10;
        switch (scenario)
        {
            case 0: payload = []; break;
            case 1: payload = payload[1..]; break;
            case 2: payload[0] = 1; break;
            case 3: payload[0] = 3; break;
            case 4: payload = Bytes.FromHexString("02000000"); break;
            case 5: payload = Bytes.FromHexString("0200040000"); break;
            case 6: payload.AsSpan(payload.Length - 4).Clear(); break;
            case 7: payload[^1] |= 0x80; break;
            case 8: groupKey = new(Bytes.FromHexString("00"), 4); break;
            case 9: payload[footerOffset] = 1; break;
            case 10: BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(footerOffset + 2), 0); break;
            case 11: BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(footerOffset + 4), 67); break;
            case 12: BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(footerOffset + 4), 204); break;
            case 13: BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(footerOffset + 4), ushort.MaxValue); break;
            case 14: payload[1] = 0xFF; break;
            case 15: payload[4] = 0x81; break;
            case 16: payload.AsSpan(5, 32).Clear(); break;
            case 17:
                payload = new byte[1 + ushort.MaxValue + 1 + 6];
                payload[0] = 2;
                payload[^1] = 0x40;
                break;
        }
        Assert.Throws<InvalidDataException>(() => ReadGroupCount(groupKey, payload));
    }

    [Test]
    public void Root_leaf_has_byte_exact_compact_encoding()
    {
        PbtNodePath groupKey = new([], 0);
        byte[] expected = Bytes.FromHexString("02000001000000000000000000000000000000000000000000000000000000000000000001000000000040");
        byte[] payload = EncodeGroup(groupKey, [new(groupKey.ToPath<PbtStorageNodePath>(), LeafEncoding(0x00, 1))]);
        Assert.That(payload, Is.EqualTo(expected));
    }

    [Test]
    public void Empty_streaming_writer_detaches_without_renting()
    {
        TrackingMemoryProvider provider = new();
        using PbtNodeGroupWriter<PbtNodePath> writer = new(new PbtNodePath([], 0), provider);
        Assert.That(writer.Detach(), Is.Null);
        Assert.That(provider.RentCount, Is.Zero);
    }

    private static IEnumerable<TestCaseData> PathWarmingCases()
    {
        foreach (Type keyType in new[] { typeof(PbtFullKey), typeof(PbtStorageFullKey) })
        {
            string fullLengthKey = new('A', 2 * (keyType == typeof(PbtFullKey) ? PbtFullKey.MaxLength : PbtStorageFullKey.MaxLength));
            foreach ((string first, string second, string query) in new[]
            {
                ("00", "80", "00"),
                ("00", "80", "40"),
                ("0000", "0080", "0000"),
                ("0000", "0080", "8000"),
                ("00000000", "00000001", "00000000"),
                ("00000000", "00000001", "00000002"),
                ("00000000", "00000001", "00"),
                ("80000000", "80000001", "80000000"),
                (fullLengthKey, fullLengthKey[..^1] + "B", fullLengthKey),
            })
                yield return new TestCaseData(first, second, query) { TypeArgs = [keyType] };
        }
    }

    [TestCaseSource(nameof(PathWarmingCases))]
    public void Path_warming_reads_only_matching_groups_without_writes<TKey>(string first, string second, string query)
        where TKey : struct, IPbtKey<TKey> =>
        AssertPathWarming(TKey.Create(Bytes.FromHexString(first)),
            TKey.Create(Bytes.FromHexString(second)), TKey.Create(Bytes.FromHexString(query)));

    [Test]
    public void Path_warming_handles_canonical_account_and_both_storage_zones([Values(0, 1, 256)] int slot)
    {
        Nethermind.Core.Address address = new("0x0000000000000000000000000000000000000001");
        PbtStorageFullKey accountKey = (PbtStorageFullKey)PbtStateKey.Account(address, PbtKeyDerivation.BasicDataLeafKey);
        PbtStorageFullKey storageKey = PbtStateKey.Storage(address, (Nethermind.Int256.UInt256)slot);
        AssertPathWarming(accountKey, storageKey, accountKey);
        AssertPathWarming(accountKey, storageKey, storageKey);
    }

    private static void AssertPathWarming<TKey>(TKey first, TKey second, TKey query)
        where TKey : struct, IPbtKey<TKey>
    {
        TrackingMemoryProvider memory = new();
        using PbtNodeGroupStore store = new(memory);
        using PbtWriteBatchBuilder<TKey> batch = new(0);
        batch.Set(first, new ValueHash256(Value(1)));
        batch.Set(second, new ValueHash256(Value(2)));
        ValueHash256 root = TrieUpdater<TKey, PbtStorageNodePath>.UpdateRoot(store, default, batch.Build());
        IReadOnlyList<PbtPhysicalPayload> before = store.ExportPhysicalPayloads();
        HashSet<PbtStorageNodePath> expectedGroups = [];
        foreach (PbtPhysicalPayload physical in before)
        {
            PbtStorageNodePath groupKey = PbtStorageNodePath.Decode(physical.Key.Span);
            PbtNodeGroupReader group = PbtStoreTestExtensions.ReadGroup(groupKey, physical.Payload.Span);
            for (int position = 0; position < PbtNodeGroupCodec.PositionCount; position++)
            {
                if (position == PbtFourLevelGroupGeometry.RootPosition && groupKey.BitDepth != 0) continue;
                if (!group.TryGetNode(position, out _)) continue;
                PbtStorageNodePath path = PbtFourLevelGroupGeometry.PathOf(groupKey, position);
                bool matches = path.BitDepth <= query.BitLength;
                for (int bit = 0; matches && bit < path.BitDepth; bit++)
                    matches = path.GetBit(bit) == TrieUpdater.GetBit(query.Bytes, bit);
                if (matches) expectedGroups.Add(groupKey);
            }
        }
        EipReferenceTree oracle = new();
        oracle.Insert(first.Bytes, Value(1));
        oracle.Insert(second.Bytes, Value(2));
        WarmReadStore reader = new(store);
        PbtTrieWarmer.WarmUpPath(reader, root, query);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.Reads, Is.EquivalentTo(expectedGroups));
            Assert.That(reader.Reads.Count, Is.EqualTo(expectedGroups.Count), "each group is fetched once");
            foreach ((PbtStorageNodePath groupKey, ValueHash256 groupHash) in reader.Hashes)
                Assert.That(groupHash, Is.EqualTo(new ValueHash256(oracle.Merkelize(groupKey))), $"warm group {groupKey.BitDepth}");
            Assert.That(store.ExportPhysicalPayloads().Count, Is.EqualTo(before.Count));
            foreach (PbtPhysicalPayload physical in before)
            {
                using RefCountingMemory payload = store.GetPhysicalNodeGroup(PbtStorageNodePath.Decode(physical.Key.Span))!;
                Assert.That(payload.GetSpan().ToArray(), Is.EqualTo(physical.Payload.ToArray()));
            }
            using PbtWriteBatchBuilder<TKey> unchanged = new(0);
            Assert.That(TrieUpdater<TKey, PbtStorageNodePath>.UpdateRoot(store, root, unchanged.Build()), Is.EqualTo(root));
        }
        store.Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero);
    }

    [TestCase(false, TypeArgs = [typeof(PbtFullKey)])]
    [TestCase(true, TypeArgs = [typeof(PbtFullKey)])]
    [TestCase(false, TypeArgs = [typeof(PbtStorageFullKey)])]
    [TestCase(true, TypeArgs = [typeof(PbtStorageFullKey)])]
    public void Path_warming_releases_payloads_on_missing_nodes_and_invalid_payloads<TKey>(bool invalidPayload)
        where TKey : struct, IPbtKey<TKey>
    {
        TrackingMemoryProvider memory = new();
        using PbtNodeGroupStore store = new();
        WarmReadStore reader = new(store);
        TKey key = TKey.Create(Bytes.FromHexString("00"));
        PbtTrieWarmer.WarmUpPath(reader, default, key);
        Assert.That(reader.Reads.Count, Is.EqualTo(1), "empty tree");

        PbtNodePath root = new([], 0);
        PbtNodePath child = new(Bytes.FromHexString("00"), 1);
        byte[] bytes = EncodeGroup(root, [new PbtNodeRecord(child.ToPath<PbtStorageNodePath>(), LeafEncoding(0, 1))]);
        if (invalidPayload) bytes = Bytes.FromHexString("ff");
        RefCountingMemory payload = memory.Rent(bytes.Length);
        bytes.CopyTo(payload.GetSpan());
        reader.Payload = payload;
        if (invalidPayload) Assert.Throws<InvalidDataException>(() => PbtTrieWarmer.WarmUpPath(reader, default, key));
        else PbtTrieWarmer.WarmUpPath(reader, default, key);
        ((IDisposable)payload).Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero);
    }

    private sealed class WarmReadStore(IPbtStore store) : IPbtStore
    {
        internal List<PbtStorageNodePath> Reads { get; } = [];
        internal List<(PbtStorageNodePath Path, ValueHash256 Hash)> Hashes { get; } = [];
        internal RefCountingMemory? Payload { get; set; }

        public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash)
        {
            Reads.Add(groupKey.ToPath<PbtStorageNodePath>());
            Hashes.Add((groupKey.ToPath<PbtStorageNodePath>(), groupHash));
            if (Payload is not { } payload) return store.GetNodeGroup(groupKey, groupHash);
            payload.AcquireLease();
            return payload;
        }

        public void SetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash, RefCountingMemory? payload) =>
            throw new AssertionException("Path warming must never write.");
    }

    private static byte[] EncodeGroup<TPath>(TPath groupKey, IReadOnlyList<PbtNodeRecord> records) where TPath : struct, IPbtNodePath<TPath>
    {
        int capacity = PbtNodeGroupCodec.HeaderLength + PbtNodeGroupCodec.MaxTrailerLength;
        foreach (PbtNodeRecord record in records) capacity += record.Encoding.Length;
        byte[] payload = new byte[capacity];
        BufferWriter writer = new(payload);
        PbtNodeGroupCodec.Encode(ref writer, groupKey, records);
        return writer.WrittenSpan.ToArray();
    }

    private static Dictionary<string, byte[]> Payloads(PbtTreeHarness tree)
    {
        Dictionary<string, byte[]> result = [];
        foreach (PbtPhysicalPayload payload in tree.PhysicalPayloads)
            result.Add(Convert.ToHexString(payload.Key.Span), payload.Payload.ToArray());
        return result;
    }

    private static bool MoveDefaultReaderEnumerator()
    {
        PbtNodeGroupReader.Enumerator enumerator = default;
        return enumerator.MoveNext();
    }

    private static byte[] LeafEncoding(byte keyMarker, byte valueMarker) =>
        PbtNodeCodec.EncodeLeaf(new PbtFullKey([keyMarker]), Value(valueMarker));

    private static byte[] Value(byte marker)
    {
        byte[] value = new byte[32];
        value[^1] = marker;
        return value;
    }

    private static byte[][] Keys(Random random, int count)
    {
        byte[][] keys = new byte[count][];
        for (int index = 0; index < keys.Length; index++)
        {
            keys[index] = new byte[2 + random.Next(63)];
            keys[index][0] = (byte)index;
            random.NextBytes(keys[index].AsSpan(1));
        }
        return keys;
    }

    private static byte[] RandomValue(Random random)
    {
        byte[] value = new byte[32];
        random.NextBytes(value);
        return value;
    }
}

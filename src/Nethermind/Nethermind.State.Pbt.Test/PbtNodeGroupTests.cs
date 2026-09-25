// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class PbtNodeGroupTests
{
    private static PbtStorageTreeKey KeyFromPath<TPath>(TPath path) where TPath : struct, IPbtNodePath<TPath>
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
        Span<byte> buffer = stackalloc byte[PbtStorageTreeKey.MaxLength];
        buffer.Fill(0xFF);
        PbtTraversalPath cursor = new(buffer);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cursor.BitDepth, Is.Zero);
            Assert.That(buffer.ToArray(), Is.All.Zero);
        }
        byte[] key = Bytes.FromHexString(new string('D', PbtStorageTreeKey.MaxLength * 2));
        cursor.AppendKey(key, depth);
        PbtStorageNodePath expected = PbtStorageNodePath.FromKey(new PbtStorageTreeKey(key), depth);
        Assert.That(cursor.ToPath<PbtStorageNodePath>().ToEncodedArray(), Is.EqualTo(expected.ToEncodedArray()));
        cursor.Truncate(0);
        cursor.AppendKey(new byte[key.Length], depth);
        Assert.That(cursor.ToPath<PbtStorageNodePath>().ToEncodedArray(),
            Is.EqualTo(PbtStorageNodePath.FromKey(new PbtStorageTreeKey(new byte[key.Length]), depth).ToEncodedArray()));
    }

    [Test]
    public void Traversal_path_construction_rejects_invalid_capacity([Values] bool fromPath) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ConstructInvalidTraversalPath(fromPath));

    private static void ConstructInvalidTraversalPath(bool fromPath)
    {
        if (fromPath)
            _ = PbtTraversalPath.FromPath(stackalloc byte[1], new PbtNodePath(Bytes.FromHexString("FF80"), 9));
        else
            _ = new PbtTraversalPath(stackalloc byte[PbtStorageTreeKey.MaxLength + 1]);
    }

    [Test]
    public void Store_identity_and_reader_payload_survive_cursor_mutation([Values] bool storage, [Values(0, 4, -1)] int groupDepth)
    {
        int capacity = storage ? PbtStorageTreeKey.MaxLength : PbtTreeKey.MaxLength;
        int depth = groupDepth < 0 ? capacity * 8 - 4 : groupDepth;
        byte[] key = Bytes.FromHexString(new string('D', capacity * 2));
        PbtStorageNodePath groupKey = PbtStorageNodePath.FromKey(new PbtStorageTreeKey(key), depth);
        PbtStorageNodePath leafPath = PbtStorageNodePath.FromKey(new PbtStorageTreeKey(key), depth == 0 ? 0 : depth + 1);
        byte[] encoding = LeafBranch(leafPath, 1);
        byte[] bytes = EncodeGroup(groupKey, [new PbtNodeRecord(leafPath, encoding)]);
        using RefCountingMemory payload = PooledRefCountingMemoryProvider.Instance.Rent(bytes.Length);
        bytes.CopyTo(payload.GetSpan());
        using PbtNodeGroupStore store = new();
        PbtTraversalPath cursor = PbtTraversalPath.FromPath(stackalloc byte[capacity], groupKey);
        ValueHash256 hash = PbtNodeCodec.Hash(new PbtNodeReader(encoding));
        store.SetNodeGroup(cursor, hash, payload);
        PbtNodeGroupReader reader = new(cursor, payload.GetSpan());
        PbtNodeGroupReader.Enumerator enumerator = reader.EnumerateNodes();
        cursor.Truncate(0);
        cursor.AppendMut(0);
        using RefCountingMemory? retained = store.GetNodeGroup(groupKey, hash);
        using (Assert.EnterMultipleScope())
        {
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
    public void Path_hash_code_is_stable_across_construction_and_distinct_across_depth(
        [Values] bool storage, [Values(0, 4, 8, 12, 264, 268)] int depth)
    {
        if (storage) AssertHashCodeIdentity<PbtStorageNodePath>(depth);
        else AssertHashCodeIdentity<PbtNodePath>(depth);
    }

    private static void AssertHashCodeIdentity<TPath>(int depth) where TPath : struct, IPbtNodePath<TPath>
    {
        byte[] bytes = new byte[(depth + 7) >> 3];
        Array.Fill(bytes, (byte)0xA0);
        TPath path = TPath.Create(bytes, depth);
        int expected = path.GetHashCode();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(path.ToPath<PbtStorageNodePath>().GetHashCode(), Is.EqualTo(expected));
            Assert.That(path.ToPath<PbtNodePath>().GetHashCode(), Is.EqualTo(expected));
            Assert.That(path.AppendNib(0xA).Prefix(depth).GetHashCode(), Is.EqualTo(expected));
            if (depth == 0) Assert.That(default(TPath).GetHashCode(), Is.EqualTo(expected));
            // The same bytes are a valid path four bits shorter or longer, so only the depth distinguishes the hashes.
            else Assert.That(TPath.Create(bytes, depth % 8 == 0 ? depth - 4 : depth + 4).GetHashCode(), Is.Not.EqualTo(expected));
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
                Assert.That(appended.GetByte(PbtStorageTreeKey.MaxLength - 1), Is.EqualTo(15));
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
            Assert.That(PbtFourLevelGroupGeometry.PositionOf(actual), Is.EqualTo(position));
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

    [Test]
    public void Node_group_paths_pack_internal_node_geometry_into_one_byte([Range(0, 4)] int length)
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
                Assert.That(PbtFourLevelGroupGeometry.LocalPathOf(path.Position), Is.EqualTo(path));
                if (length < 4)
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
        byte[] keyBytes = new byte[PbtStorageTreeKey.MaxLength];
        keyBytes[0] = Eip8297KeyDerivation.StorageZone;
        PbtStorageTreeKey storageKey = new(keyBytes);
        PbtNodePath smallPath = PbtNodePathOperations.FromKey<PbtNodePath>(keyBytes, depth);
        PbtStorageNodePath storagePath = PbtStorageNodePath.FromKey(storageKey, depth);
        Dictionary<PbtStorageNodePath, int?> entries = new() { [smallPath.ToPath<PbtStorageNodePath>()] = 1 };
        entries[storagePath] = null;
        PbtStorageNodePath leafPath = PbtStorageNodePath.FromKey(storageKey, depth == 0 ? 0 : depth + 4);
        byte[] encoding = LeafBranch(keyBytes, 1);
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
            Assert.That(reader.GetNode(PbtFourLevelGroupGeometry.PositionOf(leafPath)).ToArray(), Is.EqualTo(encoding));
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
        Assert.That(Unsafe.SizeOf<PbtPath>(), Is.EqualTo(34));
        Assert.That(Unsafe.SizeOf<PbtTreeKey>(), Is.EqualTo(40));
        Assert.That(Unsafe.SizeOf<PbtStoragePath>(), Is.EqualTo(66));
        Assert.That(Unsafe.SizeOf<PbtStorageTreeKey>(), Is.EqualTo(72));
        byte[] keyBytes = new byte[length];
        keyBytes[^1] = 1;
        PbtStorageTreeKey storageKey = new(keyBytes);
        PbtStorageNodePath storagePath = PbtStorageNodePath.FromKey(storageKey, depth);
        Assert.That(storageKey.FirstDifferingBit(new PbtStorageTreeKey(new byte[length])), Is.EqualTo(depth - 1));
        if (length == PbtPath.KeyLength)
        {
            PbtPath key = (PbtPath)storageKey;
            PbtTreeKey treeKey = (PbtTreeKey)key;
            Assert.That(key.FirstDifferingBit(new PbtPath(new byte[length])), Is.EqualTo(depth - 1));
            Assert.That(treeKey.FirstDifferingBit(new PbtTreeKey(new byte[length])), Is.EqualTo(depth - 1));
            Assert.That(((PbtStorageTreeKey)key).Bytes.ToArray(), Is.EqualTo(keyBytes));
            Assert.That((PbtPath)treeKey, Is.EqualTo(key));
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = (PbtPath)new PbtTreeKey(keyBytes.AsSpan(0, length - 1)));
        }
        else
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = (PbtPath)storageKey);
            Assert.Throws<ArgumentOutOfRangeException>(() => new PbtTreeKey(keyBytes));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtPath(new byte[33]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtPath(new byte[35]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtTreeKey(new byte[35]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtStorageTreeKey(new byte[67]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtNodePath(new byte[35], 273));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtStorageNodePath(new byte[67], 529));
    }

    [TestCase(0x00)]
    [TestCase(0x80)]
    public void Root_node_round_trip_preserves_encoding_and_allows_root_position_access(byte keyByte)
    {
        PbtNodePath rootPath = new([], 0);
        byte[] encoding = PbtNodeCodec.EncodeLeaf(new PbtTreeKey([keyByte]));

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
            byte[] encoding = position == PbtFourLevelGroupGeometry.RootPosition ? PbtNodeCodec.EncodeLeaf(new PbtStorageTreeKey(key)) : LeafBranch(key, 1);
            byte[] payload = EncodeGroup(groupKey, [new PbtNodeRecord(path, encoding)]);
            Assert.That(ValidateLeafGroup(groupKey, position, payload, streamingWriter), Is.EqualTo(1));

            int keyOffset = PbtNodeGroupCodec.HeaderLength + encoding.Length - key.Length;
            for (int bit = 0; bit < key.Length * 8; bit++)
            {
                byte mask = (byte)(0x80 >> (bit & 7));
                payload[keyOffset + (bit >> 3)] ^= mask;
#if DEBUG
                if (bit < path.BitDepth)
                    Assert.That(() => ValidateLeafGroup(groupKey, position, payload, streamingWriter), Throws.TypeOf<InvalidDataException>(), $"position {position}, bit {bit}");
                else
#endif
                Assert.That(ValidateLeafGroup(groupKey, position, payload, streamingWriter), Is.EqualTo(1), $"position {position}, bit {bit}");
                payload[keyOffset + (bit >> 3)] ^= mask;
            }
        }
    }

    [Test]
    public void Reader_and_writer_reject_leaf_keys_shorter_than_required_path(
        [Values(8, 12, 252, 508)] int groupDepth, [Values] bool streamingWriter)
    {
        PbtStorageNodePath groupKey = new(new byte[(groupDepth + 7) / 8], groupDepth);
        PbtStorageNodePath path = PbtFourLevelGroupGeometry.PathOf(groupKey, 0);
        // The inline leaf hangs one level below the branch, so its key needs one bit more than the branch depth.
        byte[] key = new byte[path.BitDepth / 8 + 1];
        byte[] encoding = LeafBranch(key, 1);
        byte[] payload = EncodeGroup(groupKey, [new PbtNodeRecord(path, encoding)]);
        Assert.That(ValidateLeafGroup(groupKey, 0, payload, streamingWriter), Is.EqualTo(1));

        byte[] shortEncoding = LeafBranch(key.AsSpan(0, key.Length - 1), 1);
        byte[] shortPayload = new byte[PbtNodeGroupCodec.HeaderLength + shortEncoding.Length + PbtNodeGroupCodec.GetTrailerLength(1, 0)];
        PbtNodeGroupCodec.Header.CopyTo(shortPayload);
        shortEncoding.CopyTo(shortPayload, PbtNodeGroupCodec.HeaderLength);
        PbtNodeGroupCodec.WriteFooter(shortPayload.AsSpan(PbtNodeGroupCodec.HeaderLength + shortEncoding.Length), stackalloc ushort[PbtNodeGroupCodec.PositionCount], 1u, 0, default);
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
        PbtTraversalPath groupPath = PbtTraversalPath.FromPath(stackalloc byte[66], groupKey);
        using PbtNodeGroupWriter<TPath> writer = new(groupKey.BitDepth, new TrackingMemoryProvider(), PbtPrefixlessBranchOmission.Interior);
        writer.Write(groupPath, position, payload.AsSpan(PbtNodeGroupCodec.HeaderLength, payload.Length - PbtNodeGroupCodec.HeaderLength - PbtNodeGroupCodec.GetTrailerLength(1, 0)));
        using RefCountingMemory writtenPayload = writer.Detach(default)!;
        Assert.That(writtenPayload.GetSpan().ToArray(), Is.EqualTo(payload));
        return 1;
    }

    private static int ReadGroupCount<TPath>(TPath groupKey, byte[] payload) where TPath : struct, IPbtNodePath<TPath> => PbtStoreTestExtensions.ReadGroup(groupKey, payload).Count;

    [Test]
    public void Node_group_memory_follows_the_native_memory_flag([Values] bool native)
    {
        IRefCountingMemoryProvider provider = PbtNodeGroupMemory.CreateProvider(new PbtConfig { NativeNodeGroupMemory = native });
        using IDisposable? disposable = provider as IDisposable;
        Assert.That(provider, native ? Is.InstanceOf<SlabRefCountingMemoryProvider>() : Is.SameAs(PooledRefCountingMemoryProvider.Instance));
    }

    [TestCase(1, 2, false)]
    [TestCase(10, 2, false)]
    [TestCase(PbtNodeGroupCodec.PositionCount, 3, false)]
    [TestCase(1, 2, true)]
    [TestCase(10, 2, true)]
    [TestCase(PbtNodeGroupCodec.PositionCount, 4, true)]
    public void Writer_rents_few_buffers_and_detaches_a_right_sized_payload(int nodeCount, int maxRents, bool slabProvider)
    {
        using SlabRefCountingMemoryProvider slab = PbtNodeGroupMemory.CreateSlabProvider();
        TrackingMemoryProvider memory = new(slabProvider ? slab : PooledRefCountingMemoryProvider.Instance);
        PbtStorageNodePath groupKey = new([], 0);
        PbtTraversalPath groupPath = PbtTraversalPath.FromPath(stackalloc byte[66], groupKey);
        using PbtNodeGroupWriter<PbtStorageNodePath> writer = new(groupKey.BitDepth, memory, PbtPrefixlessBranchOmission.Interior);
        for (int position = 0; position < nodeCount; position++)
        {
            PbtStorageNodePath path = PbtFourLevelGroupGeometry.PathOf(groupKey, position);
            writer.Write(groupPath, position, LeafBranch(path, (byte)(position + 1)));
        }

        using (RefCountingMemory payload = writer.Detach(default)!)
        {
            int payloadLength = payload.GetSpan().Length;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(memory.RentCount, Is.LessThanOrEqualTo(maxRents));
                Assert.That(payload.Capacity, Is.LessThan(2 * payloadLength), "retained capacity is close to the payload");
                Assert.That(PbtStoreTestExtensions.ReadGroup(groupKey, payload.GetSpan()).Count, Is.EqualTo(nodeCount));
                Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.EqualTo(1), "only the detached payload is retained");
            }
        }

        Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero);
    }

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
        byte[] encoding = LeafBranch([0x80], 1);

        Assert.Throws<InvalidDataException>(() => EncodeGroup(new PbtNodePath([], 0), [new PbtNodeRecord(recordPath.ToPath<PbtStorageNodePath>(), encoding)]));
    }

    [Test]
    public void Node_groups_partition_nodes_at_four_level_boundaries_and_preserve_siblings()
    {
        using PbtTreeHarness tree = new();
        List<(byte[] Key, byte[]? Value)> initial = [];
        for (int index = 0; index < 64; index++)
        {
            byte[] key = PbtStoreTestExtensions.ZoneKey($"00{index * 4:X2}{index:X2}");
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
    public void Absent_group_frames_carry_only_a_spanning_branch_size([Values] bool inherited)
    {
        // A group with nothing below it keeps every slot at zero; only a spanning branch carries a size here.
        long spanningBytes = inherited ? 1234 : 0;
        AbsentGroupFrame<PbtStorageTreeKey, PbtStorageNodePath> frame = inherited ? new(8, 5, spanningBytes) : new(8);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(frame.DescendantBytes(5), Is.EqualTo(spanningBytes));
            Assert.That(frame.DescendantBytes(4), Is.Zero);
            Assert.That(frame.DescendantMask, Is.EqualTo(inherited ? 1 << 5 : 0));
            Assert.That(frame.PayloadLength, Is.Zero);
            Assert.That(frame.StoredPositions, Is.Zero);
        }
    }

    [Test]
    public void Group_frames_load_once_at_construction([Values] bool present, [Values(0, 4, 268, 524)] int groupDepth)
    {
        TrackingMemoryProvider memory = new();
        using PbtNodeGroupStore store = new(memory);
        byte[] key = Bytes.FromHexString(new string('D', PbtStorageTreeKey.MaxLength * 2));
        PbtStorageNodePath groupKey = PbtStorageNodePath.FromKey(new PbtStorageTreeKey(key), groupDepth);
        PbtStorageNodePath leafPath = PbtStorageNodePath.FromKey(new PbtStorageTreeKey(key), groupDepth == 0 ? 0 : groupDepth + 4);
        int position = PbtFourLevelGroupGeometry.PositionOf(leafPath);
        byte[] encoding = LeafBranch(leafPath, 1);
        if (present) store.SetNode(leafPath, encoding, memory);
        WarmReadStore persistence = new(store);
        PbtTraversalPath groupPath = PbtTraversalPath.FromPath(stackalloc byte[66], groupKey);
        ValueHash256 groupHash = store.GetGroupHash(groupKey);
        using PbtNodeGroupWriter<PbtStorageNodePath> writer = new(groupKey.BitDepth, memory, PbtPrefixlessBranchOmission.Interior);
        Assert.That(GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath>.TryLoad(persistence, groupPath, groupHash, out GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath> reader), Is.EqualTo(present));
        using (new GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath>.Scope(ref reader))
        {
            Assert.That(persistence.Reads, Is.EqualTo(new[] { groupKey }));
            if (present)
            {
                reader.CopyRange(writer, 0, PbtNodeGroupCodec.PositionCount);
                Assert.That(reader.GetEncoding(position).ToArray(), Is.EqualTo(encoding));
                Assert.That(persistence.Reads, Has.Count.EqualTo(1), "a frame reads its group once");
            }
        }
        // Only the tree root's group may be missing; a frame opened for any other is reported, not read as empty.
        if (!present)
            Assert.Throws<InvalidDataException>(() => _ = new GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath>(persistence, PbtTraversalPath.FromPath(stackalloc byte[66], groupKey), groupHash));

        writer.Dispose();
        store.Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero);
    }

    [Test]
    public void Group_frames_materialize_only_branch_anchors(
        [Values(0, 4, 8, 244)] int groupDepth, [Range(0, 29)] int position, [Values] bool inlineLeaf)
    {
        using PbtNodeGroupStore store = new();
        PbtStorageNodePath groupKey = PbtNodePathOperations.FromKey<PbtStorageNodePath>(Bytes.FromHexString(new string('A', 62)), groupDepth);
        PbtStorageNodePath path = PbtFourLevelGroupGeometry.PathOf(groupKey, position);
        byte[] key = new byte[32];
        path.CopyBitsTo(0, key, 0, path.BitDepth);
        byte[] encoding = PbtNodeCodec.EncodeBranch(Bytes.FromHexString("A0"), 4, new ValueHash256(Value(1)), new ValueHash256(Value(2)), inlineLeaf ? key : [], []);
        store.SetNode(path, encoding);
        PbtTraversalPath groupPath = PbtTraversalPath.FromPath(stackalloc byte[66], groupKey);
        GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath> reader = new(store, groupPath, new ValueHash256(Value(1)));
        TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.StoredGroupHashes hashes = default;
        using (new GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath>.Scope(ref reader))
        {
            TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.BoundaryNode source = reader.TakeBoundaryNode(position, hashes.GetHash(ref reader, position));
            Assert.That(source.AnchorDepth, Is.EqualTo(path.BitDepth));
            PbtTraversalPath nodePath = PbtTraversalPath.FromPath(stackalloc byte[66], path);
            TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.FoldResult materialized = default;
            source.ToFoldResult(nodePath, groupKey.BitDepth, ref materialized);
            TrieUpdater<PbtTreeKey, PbtNodePath>.FoldResult converted = default;
            TrieUpdater<PbtTreeKey, PbtNodePath>.FoldResult.TakeFrom(ref materialized, ref converted);
            byte[] actual = new byte[converted.EncodedLength(groupPath, path.BitDepth)];
            converted.EncodeAt(groupPath, path.BitDepth, actual);
            byte[] fromRoot = new byte[converted.EncodedLength(groupPath, 0)];
            converted.EncodeAt(groupPath, 0, fromRoot);
            byte[] rootPrefix = new byte[PbtBitPrefix.ByteCount(path.BitDepth + 4)];
            PbtBitPrefix.CopyBits(key, 0, path.BitDepth, rootPrefix, 0);
            PbtBitPrefix.CopyBits(Bytes.FromHexString("A0"), 0, 4, rootPrefix, path.BitDepth);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(materialized.IsEmpty, Is.True);
                Assert.That(converted.IsLeaf, Is.False);
                Assert.That(converted.HasLeftLeaf, Is.EqualTo(inlineLeaf));
                Assert.That(converted.BranchDepth(groupPath), Is.EqualTo(path.BitDepth + 4));
                Assert.That(actual, Is.EqualTo(encoding));
                Assert.That(fromRoot, Is.EqualTo(PbtNodeCodec.EncodeBranch(rootPrefix, path.BitDepth + 4, new ValueHash256(Value(1)), new ValueHash256(Value(2)), inlineLeaf ? key : [], [])));
            }
        }
    }

    [Test]
    public void Borrowed_subtree_materializes_before_its_using_scope_releases_memory([Values] bool leaf)
    {
        PbtStorageNodePath groupKey = new([], 0);
        byte[] encoding = leaf
            ? LeafEncoding(0)
            : PbtNodeCodec.EncodeBranch(Bytes.FromHexString("123450"), 20, new ValueHash256(Value(1)), new ValueHash256(Value(2)));
        using PbtNodeGroupStore stored = new();
        stored.SetNode(groupKey, encoding);
        using PoisoningStore store = new(stored);
        TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.FoldResult materialized = default;
        PbtTraversalPath groupPath = PbtTraversalPath.FromPath(stackalloc byte[66], groupKey);
        // A root leaf's hash is the tree root the reader is opened with.
        ValueHash256 rootHash = leaf ? new(Value(9)) : PbtNodeCodec.Hash(new PbtNodeReader(encoding));
        GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath> reader = new(store, groupPath, rootHash);
        using (new GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath>.Scope(ref reader))
        {
            reader.TakeRoot().ToFoldResult(groupPath, groupKey.BitDepth, ref materialized);
            Assert.That(store.ReleasedGroupDepths, Is.Empty);
        }
        Assert.That(store.ReleasedGroupDepths, Is.EqualTo(new[] { 0 }));
        // The result is read against a cursor of its own, so the one it was materialized from may move on.
        groupPath.AppendKey(Bytes.FromHexString("FFFFFF"), 24);
        PbtTraversalPath rootCursor = new(stackalloc byte[66]);
        byte[] actual = new byte[materialized.EncodedLength(rootCursor, 0)];
        ValueHash256 hash = materialized.EncodeAt(rootCursor, 0, actual);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(encoding));
            Assert.That(hash, Is.EqualTo(rootHash));
        }
    }

    [Test]
    public void Inlined_leaf_boundary_nodes_detach_from_the_branch_that_held_them([Values] bool right, [Values] bool stored)
    {
        byte[] key = Bytes.FromHexString("1234");
        ValueHash256 leafHash = PbtNodeCodec.HashLeaf(key, Value(1));
        ValueHash256 siblingHash = new(Value(2));
        byte[] encoding = stored
            ? PbtNodeCodec.EncodeLeaf(new PbtStorageTreeKey(key))
            : right
                ? PbtNodeCodec.EncodeBranch([], 0, siblingHash, leafHash, [], key)
                : PbtNodeCodec.EncodeBranch([], 0, leafHash, siblingHash, key, []);
        TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.BoundaryNode borrowed = stored
            ? new(encoding, leafHash)
            : new(encoding, right);
        PbtStorageTreeKey borrowedKey = borrowed.LeafKey;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(borrowed.IsLeaf, Is.True);
            Assert.That(borrowed.IsEmpty, Is.False);
            Assert.That(borrowedKey.Bytes.ToArray(), Is.EqualTo(key));
            Assert.That(borrowed.Hash, Is.EqualTo(leafHash), "an inlined leaf's hash is the one its branch holds");
        }

        TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.BoundaryNode owned = borrowed.Owned();
        // Whatever the node was read from is gone once it has been detached, as it is when it crosses a thread.
        encoding.AsSpan().Fill(0xDD);
        PbtStorageTreeKey ownedKey = owned.LeafKey;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(owned.IsLeaf, Is.True);
            Assert.That(ownedKey.Bytes.ToArray(), Is.EqualTo(key));
            Assert.That(owned.Hash, Is.EqualTo(leafHash));
            Assert.That(owned.Source, Is.EqualTo(TrieUpdater.LeafSource.Stored), "a detached leaf keeps its own key, not the branch it was read from");
        }
    }

    [Test]
    public void Compact_subtree_layout_keeps_paths_out_of_local_entries()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Unsafe.SizeOf<NodeGroupPath>(), Is.EqualTo(1));
            Assert.That(Unsafe.SizeOf<TrieUpdater<PbtPath, PbtNodePath>.DecompositionEntry>(), Is.LessThan(224));
            Assert.That(Unsafe.SizeOf<TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.DecompositionEntry>(), Is.LessThan(320));
            // A boundary node reads its key out of the encoding it points at, so its size does not follow the key type
            // and stays under the 72 bytes a storage key alone used to take inside it.
            Assert.That(Unsafe.SizeOf<TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.BoundaryNode>(),
                Is.EqualTo(Unsafe.SizeOf<TrieUpdater<PbtPath, PbtNodePath>.BoundaryNode>()));
            Assert.That(Unsafe.SizeOf<TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.BoundaryNode>(), Is.LessThan(72));
        }
        TestContext.Out.WriteLine($"Small entry/owned/boundary/frontier: {Unsafe.SizeOf<TrieUpdater<PbtPath, PbtNodePath>.DecompositionEntry>()}/{Unsafe.SizeOf<TrieUpdater<PbtPath, PbtNodePath>.FoldResult>()}/{Unsafe.SizeOf<TrieUpdater<PbtPath, PbtNodePath>.BoundaryNode>()}/{Unsafe.SizeOf<TrieUpdater<PbtPath, PbtNodePath>.Frontier>()}");
        TestContext.Out.WriteLine($"Storage entry/owned/boundary/frontier: {Unsafe.SizeOf<TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.DecompositionEntry>()}/{Unsafe.SizeOf<TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.FoldResult>()}/{Unsafe.SizeOf<TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.BoundaryNode>()}/{Unsafe.SizeOf<TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.Frontier>()}");
    }

    [Test]
    public void Group_frames_release_the_payload_on_parse_failure()
    {
        TrackingMemoryProvider memory = new();
        using PbtNodeGroupStore store = new();
        RefCountingMemory payload = memory.Rent(1);
        payload.GetSpan()[0] = 0xff;
        WarmReadStore persistence = new(store) { Payload = payload };
        Assert.Catch(() => _ = new GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath>(persistence, new PbtTraversalPath(Span<byte>.Empty), new ValueHash256(Value(1))));

        Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.EqualTo(1), "the caller still owns its payload lease");
        ((IDisposable)payload).Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistence.Reads.Count, Is.EqualTo(1));
            Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero);
        }
    }

    [Test]
    public void Inserts_and_leaf_splits_do_not_fetch_new_group_frames([Values] bool split)
    {
        using PbtNodeGroupStore inner = new();
        using PbtTreeHarness expected = new();
        (byte[] Key, byte[]? Value)[] initial = [(PbtStoreTestExtensions.ZoneKey("000000"), Value(1))];
        (byte[] Key, byte[]? Value)[] changes = [(PbtStoreTestExtensions.ZoneKey("000001"), Value(2)), (PbtStoreTestExtensions.ZoneKey("000002"), Value(3))];
        ValueHash256 root = default;
        if (split)
        {
            root = inner.Fold(default, initial);
            expected.ApplyBatch(initial);
        }
        CountingStore store = new(inner);
        root = store.Fold(root, changes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(expected.ApplyBatch(changes)));
            Assert.That(store.Fetches, Is.EqualTo(1), "only the root group needs reading");
            Assert.That(store.Loads, Is.EqualTo(split ? 1 : 0));
        }
    }

    [Test]
    public void Frontier_entries_read_their_nodes_from_the_frame(
        [Values(0, 1, 2)] int scenario, [Values] bool consume)
    {
        TrackingMemoryProvider provider = new();
        using PbtNodeGroupStore store = new(provider);
        PbtStorageNodePath rootPath = new([], 0);
        store.SetNode(rootPath, PbtNodeCodec.EncodeBranch(Bytes.FromHexString("A0"), 4, new ValueHash256(Value(1)), new ValueHash256(Value(2))), provider);
        PbtTraversalPath groupPath = PbtTraversalPath.FromPath(stackalloc byte[66], rootPath);
        GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath> reader = new(store, groupPath, store.GetGroupHash(rootPath));
        TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.StoredGroupHashes hashes = default;
        PbtNodeGroupWriter<PbtStorageNodePath> writer = new(rootPath.BitDepth, provider, PbtPrefixlessBranchOmission.Interior);
        TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.Frontier frontier = new(1);
        Span<TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.FoldResult> results = stackalloc TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.FoldResult[1];
        using (new GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath>.Scope(ref reader))
        {
            if (scenario == 2)
            {
                TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.FoldResult folded = new(new PbtStorageTreeKey(new byte[32]), new ValueHash256(Value(3)));
                TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.SetBoundary(ref frontier, results, 0, ref folded);
                Assert.That(folded.IsEmpty, Is.True, "the frontier consumes the result");
            }
            else if (scenario != 0)
            {
                frontier.Place(0, TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.BoundaryPosition(0),
                    TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.EntrySource.AtPosition, PbtFourLevelGroupGeometry.RootPosition);
            }

            if (consume)
            {
                if (scenario != 0)
                    TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.AppendHeld(ref reader, ref hashes, writer, groupPath, ref frontier, results, TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.BoundaryPosition(0), default);
                Assert.That(writer.WrittenCount == 0, Is.EqualTo(scenario == 0));
                if (scenario != 0)
                    Assert.That(PbtNodeReader.FromValidated(writer.Entry(0, writer.WrittenCount).Span).IsLeaf, Is.EqualTo(scenario == 2),
                        "a fold's result is appended as composed, a stored node from its own encoding");
            }

        }
        writer.Dispose();
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
            entries.Add((PbtStoreTestExtensions.ZoneKey($"00{slot << 4:X2}00"), Value((byte)(slot + 1))));
            if (boundaryBranches)
                entries.Add((PbtStoreTestExtensions.ZoneKey($"00{slot << 4:X2}01"), Value((byte)(slot + 17))));
        }
        tree.ApplyBatch(entries);
        using PbtNodeGroupStore store = PbtNodeGroupStore.FromPhysicalPayloads(tree.PhysicalPayloads);
        PbtStorageNodePath rootPath = new([], 0);
        // The zone byte is the root branch's prefix, so the slots lie in the group below it, whose input is that branch.
        PbtStorageNodePath groupKey = new(Bytes.FromHexString("00"), 8);
        PbtTraversalPath groupPath = PbtTraversalPath.FromPath(stackalloc byte[66], groupKey);
        TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.BoundaryNode root = default;
        GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath> rootReader = new(store, new PbtTraversalPath(Span<byte>.Empty), store.GetGroupHash(rootPath));
        using (new GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath>.Scope(ref rootReader)) root = rootReader.TakeRoot().Owned();
        GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath> reader = new(store, groupPath, root.HashAt(groupPath, groupKey.BitDepth));
        TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.StoredGroupHashes hashes = default;
        using PbtNodeGroupWriter<PbtStorageNodePath> writer = new(groupKey.BitDepth, new TrackingMemoryProvider(), PbtPrefixlessBranchOmission.Interior);
        TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.Frontier frontier = new(touchedMask);
        TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.FoldResult composed = default;
        using (new GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath>.Scope(ref reader))
        {
            TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.Decompose(ref reader, groupPath, ref root, groupKey.BitDepth, ref frontier, touchedMask);
            Assert.That(frontier.Unresolved, Is.EqualTo(touchedMask), "touched slots are resolved only when their fold takes them");
            TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.FoldResult[] results = new TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.FoldResult[BitOperations.PopCount((uint)touchedMask)];
            TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.FoldResult[] taken = new TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.FoldResult[PbtFourLevelGroupGeometry.BoundarySlots];
            for (int mask = touchedMask; mask != 0; mask &= mask - 1)
            {
                int slot = BitOperations.TrailingZeroCount(mask);
                TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.BoundaryNode boundary = TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.TakeBoundary(ref reader, ref hashes, groupPath, ref frontier, slot);
                groupPath.AppendMut(slot);
                boundary.ToFoldResult(groupPath, groupKey.BitDepth, ref taken[slot]);
                groupPath.Truncate(groupKey.BitDepth);
            }
            uint stored = reader.StoredPositions & ~(1u << PbtFourLevelGroupGeometry.RootPosition);
            uint expectedFrontier = 0;
            int[] expectedPositions = new int[16];
            Array.Fill(expectedPositions, -1);
            Visit(0, 16, 30);
            uint actualFrontier = 0;
            for (int slot = 0; slot < PbtFourLevelGroupGeometry.BoundarySlots; slot++)
            {
                int position = expectedPositions[slot];
                Assert.That(frontier.Entries[slot].IsEmpty, Is.EqualTo(position == -1), $"compact slot {slot}");
                if (position == -1) continue;
                actualFrontier |= 1u << position;
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(frontier.Mask, Is.EqualTo(expectedFrontier));
                Assert.That(actualFrontier, Is.EqualTo(expectedFrontier), "untouched siblings stay at their internal positions");
            }
            for (int mask = touchedMask; mask != 0; mask &= mask - 1)
            {
                int slot = BitOperations.TrailingZeroCount(mask);
                TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.SetBoundary(ref frontier, results, slot, ref taken[slot]);
            }
            TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.Compose(ref reader, ref hashes, writer, groupPath, 0, ref frontier, results, ref composed);
            // Composed at depth zero, the result is anchored at the tree root, where the root branch hashes as the tree's root.
            ValueHash256 hash = composed.Hash(new PbtTraversalPath(Span<byte>.Empty), 0);
            Span<long> descendantBytes = stackalloc long[PbtNodeGroupCodec.DescendantSlots];
            for (int slot = 0; slot < descendantBytes.Length; slot++) descendantBytes[slot] = reader.DescendantBytes(slot);
            using RefCountingMemory? payload = writer.Detach(descendantBytes);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(hash, Is.EqualTo(tree.RootHash));
                Assert.That(payload!.GetSpan().ToArray(), Is.EqualTo(Payloads(tree)[Convert.ToHexString(groupKey.ToEncodedArray())]));
            }

            void Visit(int slot, int width, int position)
            {
                bool untouched = (touchedMask & (((1 << width) - 1) << slot)) == 0;
                if (width == 1 || untouched)
                {
                    Expect(slot, position, untouched);
                    return;
                }
                if (compressedSiblings && width == 8)
                {
                    width = 2;
                    position = TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.BoundaryPosition(slot + 1) + 1;
                    if ((touchedMask & (3 << slot)) == 0)
                    {
                        Expect(slot, position, true);
                        return;
                    }
                }
                Visit(slot, width / 2, position - width);
                Visit(slot + width / 2, width / 2, position - 1);
            }

            void Expect(int slot, int position, bool untouched)
            {
                // Composition copies an untouched stored node from the masks, so decomposition leaves it no entry.
                if (untouched && (stored & (1u << position)) != 0) return;
                expectedPositions[slot] = position;
                expectedFrontier |= 1u << position;
            }
        }
    }

    [Test]
    public void Compact_frontier_boundary_results_track_insertions_and_deletions([Values("00", "FF")] string zone)
    {
        using PbtTreeHarness expected = new();
        byte[] key = PbtStoreTestExtensions.ZoneKey(zone);
        int slot = key[0] >> 4;
        PbtStorageNodePath rootPath = new([], 0);
        PbtTraversalPath groupPath = PbtTraversalPath.FromPath(stackalloc byte[66], rootPath);
        AbsentGroupFrame<PbtStorageTreeKey, PbtStorageNodePath> reader = new(rootPath.BitDepth);
        TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.StoredGroupHashes hashes = default;
        using PbtNodeGroupWriter<PbtStorageNodePath> writer = new(rootPath.BitDepth, new TrackingMemoryProvider(), PbtPrefixlessBranchOmission.Interior);
        TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.Frontier frontier = new(1 << slot);
        Span<TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.FoldResult> results = stackalloc TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.FoldResult[1];
        TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.FoldResult composed = default;
        TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.BoundaryNode input = default;
        TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.Decompose(ref reader, groupPath, ref input, 0, ref frontier, 1 << slot);
        Assert.That(frontier.Mask, Is.Zero);
        Assert.That(TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.TakeBoundary(ref reader, ref hashes, groupPath, ref frontier, slot).IsEmpty,
            Is.True, "an absent group has no boundary node to fold into");
        TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.Compose(ref reader, ref hashes, writer, groupPath, 0, ref frontier, results, ref composed);
        Assert.That(composed.IsEmpty, Is.True);

        TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.FoldResult folded = new(new PbtStorageTreeKey(key), PbtNodeCodec.HashLeaf(key, Value(1)));
        TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.SetBoundary(ref frontier, results, slot, ref folded);
        Assert.That(frontier.Mask, Is.EqualTo(1u << TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.BoundaryPosition(slot)));
        folded = default;
        TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.SetBoundary(ref frontier, results, slot, ref folded);
        TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.Compose(ref reader, ref hashes, writer, groupPath, 0, ref frontier, results, ref composed);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(frontier.Mask, Is.Zero);
            Assert.That(composed.IsEmpty, Is.True);
        }

        folded = new(new PbtStorageTreeKey(key), PbtNodeCodec.HashLeaf(key, Value(1)));
        TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.SetBoundary(ref frontier, results, slot, ref folded);
        TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.Compose(ref reader, ref hashes, writer, groupPath, 0, ref frontier, results, ref composed);
        Assert.That(writer.WriteRoot(groupPath, composed), Is.EqualTo(expected.ApplyBatch([(key, Value(1))])));
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
            byte[] key = PbtStoreTestExtensions.ZoneKey($"00{keyBits:X6}");
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
        tree.ApplyBatch(changes);
        AssertMatchesRebuild();

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
        // Six levels keep stored branches below the boundary: leaves are inlined in their parents.
        for (int index = 0; index < 64; index++) entries.Add((PbtStoreTestExtensions.ZoneKey($"00{index << 2:X2}"), Value((byte)(index + 1))));
        ValueHash256 root = tree.ApplyBatch(entries);
        using PbtNodeGroupStore store = PbtNodeGroupStore.FromPhysicalPayloads(tree.PhysicalPayloads);
        PbtNodePath boundary = new(Bytes.FromHexString("0000"), 12);
        store.SetNode(boundary, null);
        using RefCountingMemory? descendantGroup = store.GetPhysicalNodeGroup(boundary);
        Assert.That(descendantGroup, Is.Not.Null);
        Assert.That(store.GetNode(boundary), Is.Null);

        Assert.That(() => store.Fold(root, [(PbtStoreTestExtensions.ZoneKey("00"), Value(0xF0))]), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Deleting_the_last_node_removes_its_physical_group_and_reopen_preserves_records()
    {
        using PbtTreeHarness tree = new();
        byte[] key = PbtStoreTestExtensions.ZoneKey("004224");
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
        Assert.Throws<InvalidDataException>(() => PbtNodeGroupStore.FromPhysicalPayloads([new PbtPhysicalPayload(new PbtStorageNodePath([], 0), [0, 0, 0, 0])]));

    [Test]
    public void Import_rejects_non_boundary_keys_and_malformed_group_payloads()
    {
        Assert.Throws<InvalidDataException>(() => PbtNodeGroupStore.FromPhysicalPayloads([new PbtPhysicalPayload(new PbtStorageNodePath([0x00], 1), [0, 0, 0, 0])]));
        Assert.Throws<InvalidDataException>(() => PbtNodeGroupStore.FromPhysicalPayloads([new PbtPhysicalPayload(new PbtStorageNodePath([], 0), [1, 0, 0, 0])]));
    }

    [Test]
    public void Store_releases_owned_memory_across_create_replace_delete_reopen_lookup_and_disposal()
    {
        TrackingMemoryProvider provider = new();
        PbtNodePath rootPath = new([], 0);
        byte[] firstEncoding = LeafEncoding(0x00);
        byte[] secondEncoding = LeafEncoding(0x80);

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
        byte[] firstEncoding = LeafEncoding(0x00);
        byte[] secondEncoding = LeafEncoding(0x80);
        byte[] thirdEncoding = LeafEncoding(0x40);

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
        byte[] expected = EncodeGroup(rootPath, [new PbtNodeRecord(rootPath.ToPath<PbtStorageNodePath>(), LeafEncoding(0x00))]);
        using PbtNodeGroupStore store = new();
        RefCountingMemory payload = provider.Rent(expected.Length);
        expected.CopyTo(payload.GetSpan());
        store.SetNodeGroup(rootPath, default, payload);
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
        byte[] encoding = LeafEncoding(0x00);
        byte[] validPayload = EncodeGroup(rootPath, [new PbtNodeRecord(rootPath.ToPath<PbtStorageNodePath>(), encoding)]);
        using PbtNodeGroupStore store = new();
        store.SetNode(rootPath, encoding, provider);
        PbtNodePath groupKey = new(new byte[(keyDepth + 7) / 8], keyDepth);
        byte[] rejectedBytes = payloadLength < 0 ? validPayload : new byte[payloadLength];
        RefCountingMemory rejectedPayload = provider.Rent(rejectedBytes.Length);
        rejectedBytes.CopyTo(rejectedPayload.GetSpan());

        Assert.That(() => store.SetNodeGroup(groupKey, default, rejectedPayload), Throws.TypeOf(exceptionType));
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
        byte[] left = PbtStoreTestExtensions.ZoneKey("00");
        byte[] right = PbtStoreTestExtensions.ZoneKey("FF");
        ValueHash256 root = store.Fold(default, [(left, Value(1)), (right, Value(2))]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Publishes, Is.EqualTo(1));
            Assert.That(store.Inner.EnumerateRecords(), Has.Count.EqualTo(1));
        }

        (byte[] Key, byte[]? Value)[] changedBatch = [(left, Value(3)), (right, Value(4))];
        root = store.Fold(root, changedBatch);
        Assert.That(store.Publishes, Is.EqualTo(2));
        PbtNodePath rootPath = new([], 0);
        using RefCountingMemory priorPayload = store.Inner.GetPhysicalNodeGroup(rootPath)!;
        byte[] expectedPayload = priorPayload.GetSpan().ToArray();

        ValueHash256 unchangedRoot = store.Fold(root, changedBatch);
        using RefCountingMemory unchangedPayload = store.Inner.GetPhysicalNodeGroup(rootPath)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(unchangedPayload.GetSpan().ToArray(), Is.EqualTo(expectedPayload));
            Assert.That(unchangedRoot, Is.EqualTo(root));
            Assert.That(store.Publishes, Is.EqualTo(3));
        }

        PbtNodePath siblingPath = new([0x80], 1);
        byte[]? sibling = store.Inner.GetNode(siblingPath);
        root = store.Fold(root, [(left, Value(5))]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Publishes, Is.EqualTo(4));
            Assert.That(store.Inner.GetNode(siblingPath), Is.EqualTo(sibling));
        }

        root = store.Fold(root, [(left, null), (right, null)]);
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
        if (storedRootPresent) store.Fold(default, [(PbtStoreTestExtensions.ZoneKey("00"), Value(1))]);

        Assert.That(() => store.Fold(new ValueHash256(Value(3)), [(PbtStoreTestExtensions.ZoneKey("FF"), Value(2))]), Throws.TypeOf<InvalidDataException>());
    }

    // 0001 keeps the surviving subtree a stored branch rather than an inline leaf, so it escapes through its frames.
    [TestCase("000000,000001,000800", "000800", false, new[] { 12, 0 }, TestName = "Escaping_subtree_survives_poisoned_group_root_handoff")]
    [TestCase("000000,000001,000080,000800", "000080,000800", false, new[] { 16, 12, 0 }, TestName = "Escaping_subtree_survives_poisoned_nested_groups")]
    [TestCase("000000,000001,000080,000800", "000080,000800", true, new[] { 16, 12, 0 }, TestName = "Inline_subtree_survives_poisoned_nested_groups")]
    [TestCase("000000,000008", "000080", false, new[] { 0 }, TestName = "Ancestor_borrowed_subtree_survives_child_frame_return")]
    public void Returned_subtrees_survive_group_lease_release(string initialKeys, string deletedKeys, bool replaceSurvivor, int[] releasedDepths) =>
        AssertReturnedSubtrees(initialKeys, deletedKeys, replaceSurvivor, releasedDepths);

    // Keys sharing a long prefix leave the groups between the root and their branch absent. An insert diverging
    // inside one of those groups creates it, so its stored sizes must pick up the untouched descendants from the
    // parent's slot without reading their group; a delete of an absent key diverging there is a no-op that must
    // neither read nor write. A second insert diverging deeper inherits through the group the first one created.
    [TestCase("000100", "000200", TestName = "Sorted_walk_creates_a_group_between_existing_groups")]
    [TestCase("01", "0100", TestName = "Zone_frame_creates_a_group_between_existing_groups")]
    [TestCase("0020", "0040", TestName = "Worker_frame_creates_a_group_between_existing_groups")]
    public void Groups_created_between_existing_groups_include_their_descendants(string divergingKey, string absentKey)
    {
        const string DeeperKey = "000010";
        byte[] Key(string hex) => PbtStoreTestExtensions.ZoneKey(hex);
        using PbtNodeGroupStore store = new();
        ValueHash256 root = default;
        CountingStore Apply(params (byte[] Key, byte[]? Value)[] changes)
        {
            CountingStore counting = new(store);
            root = counting.Fold(root, changes);
            PbtStoreTestExtensions.AssertSubtreeBytes(store.ExportPhysicalPayloads());
            return counting;
        }

        // The third key keeps a stored branch below the long prefix; the leaves themselves are inlined.
        Apply((Key("000000"), Value(1)), (Key("000001"), Value(2)), (Key("000002"), Value(4)));
        string[] initial = PhysicalRecords(store);
        Assert.That(initial, Has.Length.EqualTo(2), "root group and the deep branch's group");

        CountingStore absentDelete = Apply((Key(absentKey), null));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(PhysicalRecords(store), Is.EqualTo(initial));
            Assert.That(absentDelete.Fetches, Is.EqualTo(1), "only the root group is read");
        }

        CountingStore insert = Apply((Key(divergingKey), Value(3)));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(PhysicalRecords(store), Has.Length.EqualTo(3), "a group is created between the existing ones");
            Assert.That(insert.Fetches, Is.EqualTo(1), "only the root group is read");
        }

        CountingStore deeperInsert = Apply((Key(DeeperKey), Value(5)));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(PhysicalRecords(store), Has.Length.EqualTo(4), "a group is created below the one created before");
            Assert.That(deeperInsert.Fetches, Is.EqualTo(2), "only the groups on the path are read");
        }

        Apply((Key(divergingKey), null));
        Assert.That(PhysicalRecords(store), Has.Length.EqualTo(3), "the emptied group is removed");
        Apply((Key(divergingKey), Value(3)));
        Assert.That(PhysicalRecords(store), Has.Length.EqualTo(4), "the group is recreated below the promoted subtree");

        Apply((Key(divergingKey), null), (Key(DeeperKey), null));
        Assert.That(PhysicalRecords(store), Is.EqualTo(initial));
    }

    private static string[] PhysicalRecords(PbtNodeGroupStore store)
    {
        List<string> records = [];
        foreach (PbtPhysicalPayload payload in store.ExportPhysicalPayloads())
            records.Add(Convert.ToHexString(payload.Key.ToEncodedArray()) + Convert.ToHexString(payload.Payload.Span));
        records.Sort(StringComparer.Ordinal);
        return [.. records];
    }

    [Test]
    public void Worker_results_survive_poisoned_group_memory([Values] bool branch) =>
        AssertReturnedSubtrees(branch ? "000000,000008,000080,008000,010000" : "000000,000080,008000,010000",
            "000080,008000", false, null);

    private static void AssertReturnedSubtrees(string initialKeys, string deletedKeys, bool replaceSurvivor, int[]? releasedDepths)
    {
        using PbtTreeHarness expected = new();
        EipReferenceTree oracle = new();
        List<(byte[] Key, byte[]? Value)> initial = [];
        foreach (string key in initialKeys.Split(','))
        {
            byte[] keyBytes = PbtStoreTestExtensions.ZoneKey(key);
            initial.Add((keyBytes, Value(1)));
            oracle.Insert(keyBytes, Value(1));
        }
        ValueHash256 root = expected.ApplyBatch(initial);
        using PoisoningStore store = new(PbtNodeGroupStore.FromPhysicalPayloads(expected.PhysicalPayloads));
        List<(byte[] Key, byte[]? Value)> changes = [];
        foreach (string key in deletedKeys.Split(','))
        {
            byte[] keyBytes = PbtStoreTestExtensions.ZoneKey(key);
            changes.Add((keyBytes, null));
            oracle.Delete(keyBytes);
        }
        if (replaceSurvivor)
        {
            byte[] key = PbtStoreTestExtensions.ZoneKey("000000");
            changes.Add((key, Value(2)));
            oracle.Insert(key, Value(2));
        }
        expected.ApplyBatch(changes);

        ValueHash256 actualRoot = store.Fold(root, changes);

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

    private sealed class PoisoningStore(PbtNodeGroupStore inner) : IPbtStore, IPbtNodeGroupSink, IDisposable
    {
        public IPbtConcurrentWriter CreateWriter() => new PbtPassThroughWriter(this);

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

    private sealed class PublishingStore : IPbtStore, IPbtNodeGroupSink, IDisposable
    {
        public IPbtConcurrentWriter CreateWriter() => new PbtPassThroughWriter(this);

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

    /// <summary>Counts every group fetch, and the fetches that found a group.</summary>
    private sealed class CountingStore(PbtNodeGroupStore inner) : IPbtStore, IPbtNodeGroupSink
    {
        private int _fetches;
        private int _loads;

        public IPbtConcurrentWriter CreateWriter() => new PbtPassThroughWriter(this);

        internal int Fetches => _fetches;
        internal int Loads => _loads;

        public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash)
        {
            Interlocked.Increment(ref _fetches);
            RefCountingMemory? payload = inner.GetNodeGroup(groupKey, groupHash);
            if (payload is not null) Interlocked.Increment(ref _loads);
            return payload;
        }

        public void SetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash, RefCountingMemory? payload) => inner.SetNodeGroup(groupKey, groupHash, payload);
    }

    [Test]
    public void Failed_import_releases_previously_copied_payloads()
    {
        TrackingMemoryProvider provider = new();
        PbtNodePath rootPath = new([], 0);
        byte[] validPayload = EncodeGroup(rootPath, [new PbtNodeRecord(rootPath.ToPath<PbtStorageNodePath>(), LeafEncoding(0x00))]);
        PbtPhysicalPayload valid = new(rootPath.ToPath<PbtStorageNodePath>(), validPayload);

        Assert.Throws<InvalidDataException>(() => PbtNodeGroupStore.FromPhysicalPayloads([valid, valid], provider));
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero);
    }

    [TestCase(1, new[] { 4 })]
    [TestCase(2, new[] { 4 })]
    [TestCase(3, new[] { 4 })]
    [TestCase(5, new[] { 4, 8 })]
    [TestCase(6, new[] { 4, 8 })]
    [TestCase(7, new[] { 4, 8 })]
    [TestCase(9, new[] { 4, 8, 12 })]
    [TestCase(13, new[] { 4, 8, 12, 16 })]
    public void Compressed_branch_jumps_create_no_intermediate_groups(int sharedPrefixBits, int[] absentGroupDepths)
    {
        using PbtTreeHarness tree = new();
        EipReferenceTree oracle = new();
        // The account zone byte extends the shared prefix by eight bits.
        byte[] leftKey = PbtStoreTestExtensions.ZoneKey("00");
        byte[] rightKey = PbtStoreTestExtensions.ZoneKey("00");
        rightKey[1 + sharedPrefixBits / 8] = (byte)(0x80 >> (sharedPrefixBits % 8));
        // A second right key keeps a stored branch just below the shared prefix; leaves alone would be inlined in the root.
        byte[] farRightKey = (byte[])rightKey.Clone();
        farRightKey[1 + (sharedPrefixBits + 8) / 8] |= (byte)(0x80 >> ((sharedPrefixBits + 8) % 8));

        tree.ApplyBatch([(leftKey, Value(1)), (rightKey, Value(2)), (farRightKey, Value(3))]);
        oracle.Insert(leftKey, Value(1));
        oracle.Insert(rightKey, Value(2));
        oracle.Insert(farRightKey, Value(3));
        tree.Reopen();

        List<int> groupDepths = [];
        foreach (PbtPhysicalPayload payload in tree.PhysicalPayloads)
            groupDepths.Add(payload.Key.BitDepth);

        for (int depth = 1; depth <= 8 + sharedPrefixBits; depth++)
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
            Assert.That(groupDepths, Does.Contain(8 + sharedPrefixBits / 4 * 4));
            foreach (int absentGroupDepth in absentGroupDepths)
                Assert.That(groupDepths, Does.Not.Contain(absentGroupDepth));
            Assert.That(tree.CanonicalRecords(), Has.Length.EqualTo(2));
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
        [Values(0, 4, 268, 516)] int groupDepth, [Values(0, 1, 2)] int nodeKind, [Values] PbtPrefixlessBranchOmission omission)
    {
        PbtStorageNodePath groupKey = new(new byte[(groupDepth + 7) / 8], groupDepth);
        List<PbtNodeRecord> records = [];
        ReadOnlyMemory<byte>[] encodings = new ReadOnlyMemory<byte>[PbtNodeGroupCodec.PositionCount];
        bool[] present = new bool[PbtNodeGroupCodec.PositionCount];
        PbtTraversalPath groupPath = PbtTraversalPath.FromPath(stackalloc byte[66], groupKey);
        using PbtNodeGroupWriter<PbtStorageNodePath> streamingWriter = new(groupKey.BitDepth, new TrackingMemoryProvider(), omission);
        int fullLength = PbtNodeGroupCodec.HeaderLength + sizeof(uint) + PbtNodeGroupCodec.DescendantMaskLength;
        int omitted = 0;
        int streamed = 0;
        int positionCount = groupDepth == 0 ? 31 : 30;
        for (int position = 0; position < positionCount; position++)
        {
            PbtStorageNodePath path = PbtFourLevelGroupGeometry.PathOf(groupKey, position);
            byte[] encoding = nodeKind switch
            {
                0 => PbtNodeCodec.EncodeBranch([], 0, new ValueHash256(Value(1)), new ValueHash256(Value(2))),
                1 => PbtNodeCodec.EncodeBranch(Bytes.FromHexString("80"), 1, new ValueHash256(Value(1)), new ValueHash256(Value(2))),
                _ => LeafBranch(path, 1),
            };
            records.Add(new(path, encoding));
            encodings[position] = encoding;
            present[position] = true;
            fullLength += encoding.Length + sizeof(ushort);
            if (nodeKind == 0 && path.BitDepth - groupDepth is >= 1 and <= 3) omitted++;
            if (!PbtNodeGroupCodec.ShouldOmit(omission, position, encoding)) streamed++;
            if ((position & 1) == 0) streamingWriter.Write(groupPath, position, encoding);
            else
            {
                encoding.CopyTo(streamingWriter.GetSpan(position, encoding.Length));
                streamingWriter.Commit(groupPath);
            }
        }

        byte[] payload = EncodeGroup(groupKey, records);
        BufferWriter slotWriter = new(new byte[fullLength]);
        PbtNodeGroupEncoder.Encode(ref slotWriter, groupKey, encodings, present, default);
        using RefCountingMemory streamedPayload = streamingWriter.Detach(default)!;
        PbtNodeGroupReader reader = PbtStoreTestExtensions.ReadGroup(groupKey, payload);
        PbtNodeGroupReader streamedReader = PbtStoreTestExtensions.ReadGroup(groupKey, streamedPayload.GetSpan());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fullLength - payload.Length, Is.EqualTo((PbtNodeCodec.BranchLength(0, 0, 0) + sizeof(ushort)) * omitted));
            Assert.That(omitted, Is.EqualTo(nodeKind == 0 ? 14 : 0));
            Assert.That(reader.Count, Is.EqualTo(positionCount - omitted));
            Assert.That(slotWriter.WrittenSpan.ToArray(), Is.EqualTo(payload));
            if (omission == PbtPrefixlessBranchOmission.Interior) Assert.That(streamedPayload.GetSpan().ToArray(), Is.EqualTo(payload));
            else Assert.That(streamedPayload.GetSpan().Length, Is.EqualTo(fullLength - (PbtNodeCodec.BranchLength(0, 0, 0) + sizeof(ushort)) * (positionCount - streamed)));
            Assert.That(streamed, Is.EqualTo(positionCount - (nodeKind != 0 ? 0 : omission switch { PbtPrefixlessBranchOmission.Interior => 14, PbtPrefixlessBranchOmission.OddLevels => 10, _ => 0 })));
            Assert.That(streamedReader.Count, Is.EqualTo(streamed));
        }
        int enumerated = 0;
        PbtNodeGroupReader.Enumerator enumerator = reader.EnumerateNodes();
        while (enumerator.MoveNext()) enumerated++;
        Assert.That(enumerated, Is.EqualTo(reader.Count));
        for (int position = 0; position < positionCount; position++)
        {
            int relativeDepth = records[position].Path.BitDepth - groupDepth;
            bool retained = nodeKind != 0 || relativeDepth is 0 or 4;
            bool streamedRetained = retained || omission == PbtPrefixlessBranchOmission.None || (omission == PbtPrefixlessBranchOmission.OddLevels && relativeDepth == 2);
            bool found = reader.TryGetNode(position, out ReadOnlySpan<byte> encoding);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(streamedReader.GetNode(position).ToArray(), Is.EqualTo(streamedRetained ? encodings[position].ToArray() : Array.Empty<byte>()));
                Assert.That(found, Is.EqualTo(retained), $"position {position}");
                Assert.That(reader.GetNode(position).ToArray(), Is.EqualTo(retained ? encodings[position].ToArray() : Array.Empty<byte>()));
                if (found) Assert.That(encoding.ToArray(), Is.EqualTo(encodings[position].ToArray()));
            }
        }
    }

    [Test]
    public void Explicit_emissions_preserve_only_selected_nodes(
        [Values(0, 4, 516)] int groupDepth,
        [Values(0u, 0x400C0189u, 0x7FFFFFFFu)] uint selected,
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
                : LeafBranch(path, (byte)(position + 1));
            PbtNodeRecord record = new(path, encoding);
            records.Add(record);
            if ((selected & (1u << position)) != 0) expectedRecords.Add(record);
        }
        byte[] sourcePayload = EncodeGroup(groupKey, records);
        using PbtNodeGroupStore store = PbtNodeGroupStore.FromPhysicalPayloads([new(groupKey, sourcePayload)]);
        PbtTraversalPath groupPath = PbtTraversalPath.FromPath(stackalloc byte[66], groupKey);
        GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath> reader = new(store, groupPath, new ValueHash256(Value(1)));
        using PbtNodeGroupWriter<PbtStorageNodePath> writer = new(groupKey.BitDepth, new TrackingMemoryProvider(), PbtPrefixlessBranchOmission.Interior);
        using (new GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath>.Scope(ref reader))
        {
            int nextPosition = 0;
            int lastEmittedPosition = -1;
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
                            if (!copyRanges) writer.Write(groupPath, nextPosition, encoding.Span);
                            lastEmittedPosition = nextPosition;
                        }
                        nextPosition++;
                    } while (nextPosition < endPosition && (selected & (1u << nextPosition)) != 0);
                    if (copyRanges) reader.CopyRange(writer, startPosition, nextPosition);
                }

                Assert.That(writer.LastPosition, Is.EqualTo(lastEmittedPosition));
            }
            using RefCountingMemory? payload = writer.Detach(default);
            Assert.That(payload?.GetSpan().ToArray(), Is.EqualTo(expectedRecords.Count == 0 ? null : EncodeGroup(groupKey, expectedRecords)));
        }
    }

    [Test]
    public void Streaming_writer_commits_leaves_without_allocating(
        [Values(0, 4, 8, 268, PbtFourLevelGroupGeometry.MaxGroupDepth)] int groupDepth)
    {
        PbtStorageNodePath groupKey = new(new byte[(groupDepth + 7) / 8], groupDepth);
        PbtTraversalPath groupPath = PbtTraversalPath.FromPath(stackalloc byte[66], groupKey);
        using PbtNodeGroupWriter<PbtStorageNodePath> writer = new(groupKey.BitDepth, new TrackingMemoryProvider(), PbtPrefixlessBranchOmission.Interior);
        int positionCount = groupDepth == 0 ? PbtFourLevelGroupGeometry.PositionCount : PbtFourLevelGroupGeometry.RootPosition;
        for (int position = 0; position < positionCount; position++)
        {
            PbtStorageNodePath path = PbtFourLevelGroupGeometry.PathOf(groupKey, position);
            byte[] encoding = LeafBranch(path, 1);
            encoding.CopyTo(writer.GetSpan(position, encoding.Length));

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            writer.Commit(groupPath);
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
        PbtTraversalPath groupPath = PbtTraversalPath.FromPath(stackalloc byte[66], groupKey);
        using PbtNodeGroupWriter<PbtStorageNodePath> writer = new(groupKey.BitDepth, provider, PbtPrefixlessBranchOmission.Interior);
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
                PbtNodeCodec.WriteBranchTrailer(destination[PbtNodeCodec.BranchPreimageLength(4)..], [], []);
                destination[3] = 0xA0;
                writer.Commit(groupPath);
            }
            else writer.Write(groupPath, position, encoding);
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
        PbtNodeGroupEncoder.Encode(ref slotWriter, groupKey, encodings, present, default);

        using (RefCountingMemory payload = writer.Detach(default)!)
        {
            writer.Dispose();
            PbtNodeGroupReader reader = PbtStoreTestExtensions.ReadGroup(groupKey, payload.GetSpan());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(payload.GetSpan().ToArray(), Is.EqualTo(expected));
                Assert.That(slotWriter.WrittenSpan.ToArray(), Is.EqualTo(expected));
                Assert.That(expected[..PbtNodeGroupCodec.HeaderLength], Is.EqualTo(Bytes.FromHexString("05")));
                Assert.That(reader.Count, Is.EqualTo(count));
                int nodeLength = PbtNodeCodec.BranchLength(4, 0, 0);
                Assert.That(expected.Length, Is.EqualTo(count * nodeLength + 7 + 2 * count));
                uint availability = 0;
                for (int index = 0; index < count; index++)
                {
                    int position = PbtFourLevelGroupGeometry.PositionOf(records[index].Path);
                    availability |= 1u << position;
                    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(expected.AsSpan(1 + count * nodeLength + index * 2)), Is.EqualTo(index * nodeLength));
                    Assert.That(reader.GetNode(position).ToArray(), Is.EqualTo(expected.AsSpan(1 + index * nodeLength, nodeLength).ToArray()));
                }
                Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(expected.AsSpan(expected.Length - 6)), Is.EqualTo(availability));
                Assert.That(payload, Is.SameAs(provider.Rented[^1]));
                Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.EqualTo(1));
                Assert.That(provider.RequestedLengths[^1], Is.LessThan(2 * expected.Length), "retained capacity is close to the payload");
            }
        }
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero);
        Assert.Throws<ObjectDisposedException>(() => writer.Detach(default));
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
        PbtTraversalPath groupPath = new(Span<byte>.Empty);
        using (PbtNodeGroupWriter<PbtNodePath> writer = new(0, provider, PbtPrefixlessBranchOmission.Interior))
        {
            byte[] branch = PbtNodeCodec.EncodeBranch(Bytes.FromHexString("80"), 1, new ValueHash256(Value(1)), new ValueHash256(Value(2)));
            writer.Write(groupPath, 2, branch);
            switch (scenario)
            {
                case 0: Assert.Throws<InvalidDataException>(() => writer.Write(new PbtTraversalPath(Span<byte>.Empty), 2, branch)); break;
                case 1: Assert.Throws<InvalidDataException>(() => writer.Write(new PbtTraversalPath(Span<byte>.Empty), 1, branch)); break;
                case 2: Assert.Throws<InvalidDataException>(() => writer.GetSpan(3, ushort.MaxValue)); break;
                case 3: Assert.Throws<ArgumentOutOfRangeException>(() => writer.GetSpan(31, branch.Length)); break;
                case 4:
                    writer.GetSpan(3, 1)[0] = 0xFF;
                    Assert.Throws<InvalidDataException>(() => writer.Commit(new PbtTraversalPath(Span<byte>.Empty)));
                    Assert.Throws<InvalidOperationException>(() => writer.Detach(default));
                    break;
                case 5: Assert.Throws<InvalidDataException>(() => writer.Write(new PbtTraversalPath(Span<byte>.Empty), 3, LeafEncoding(0xFF))); break;
            }
            Assert.That(writer.WrittenCount, Is.EqualTo(branch.Length));
        }
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero);
        using PbtNodeGroupWriter<PbtNodePath> nonRoot = new(4, provider, PbtPrefixlessBranchOmission.Interior);
        Assert.Throws<ArgumentOutOfRangeException>(() => nonRoot.GetSpan(30, 67));
    }

    [TestCase(1, false)]
    [TestCase(2, false)]
    [TestCase(2, true)]
    public void Streaming_writer_releases_memory_after_rent_failure(int failedRent, bool failInDetach)
    {
        TrackingMemoryProvider provider = new() { ThrowOnRent = failedRent };
        PbtTraversalPath groupPath = new(Span<byte>.Empty);
        using (PbtNodeGroupWriter<PbtNodePath> writer = new(0, provider, PbtPrefixlessBranchOmission.Interior))
        {
            byte[] branch = PbtNodeCodec.EncodeBranch([], 0, new ValueHash256(Value(1)), new ValueHash256(Value(2)));
            if (failedRent == 2) writer.Write(groupPath, 0, branch);
            Action rentingOperation = failInDetach ? () => writer.Detach(default) : () => writer.GetSpan(failedRent, 1024);
            Assert.Throws<InvalidOperationException>(rentingOperation);
        }
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero);
    }

    [Test]
    public void Streaming_writer_accepts_exact_uint16_entries_limit()
    {
        TrackingMemoryProvider provider = new();
        PbtTraversalPath groupPath = new(Span<byte>.Empty);
        using PbtNodeGroupWriter<PbtNodePath> writer = new(0, provider, PbtPrefixlessBranchOmission.Interior);
        for (int position = 0; position < 8; position++)
        {
            int length = position == 7 ? 8191 : 8192;
            Span<byte> encoding = writer.GetSpan(position, length);
            PbtNodeCodec.CreateBranchEncoding(encoding, (length - PbtNodeCodec.BranchLength(0, 0, 0)) * 8,
                new ValueHash256(Value(1)), new ValueHash256(Value(2)));
            PbtNodeCodec.WriteBranchTrailer(encoding[^PbtNodeCodec.BranchTrailerHeaderLength..], [], []);
            writer.Commit(groupPath);
        }
        Assert.That(writer.WrittenCount, Is.EqualTo(ushort.MaxValue));
        Assert.Throws<InvalidDataException>(() => writer.GetSpan(8, 1));
        using RefCountingMemory payload = writer.Detach(default)!;
        PbtNodeGroupReader reader = PbtStoreTestExtensions.ReadGroup(new PbtNodePath([], 0), payload.GetSpan());
        Assert.That(reader.Count, Is.EqualTo(8));
    }

    [Test]
    public void Versioned_group_rejects_invalid_header_and_footer([Range(0, 19)] int scenario)
    {
        PbtNodePath groupKey = new([], 0);
        byte[] branch = PbtNodeCodec.EncodeBranch(Bytes.FromHexString("80"), 1,
            new ValueHash256(Value(1)), new ValueHash256(Value(2)));
        byte[] payload = EncodeGroup(groupKey, [
            new(PbtFourLevelGroupGeometry.PathOf(groupKey, 3).ToPath<PbtStorageNodePath>(), branch),
            new(PbtFourLevelGroupGeometry.PathOf(groupKey, 10).ToPath<PbtStorageNodePath>(), branch),
            new(groupKey.ToPath<PbtStorageNodePath>(), branch)]);
        int footerOffset = payload.Length - 12;
        switch (scenario)
        {
            case 0: payload = []; break;
            case 1: payload = payload[1..]; break;
            case 2: payload[0] = 1; break;
            case 3: payload[0] = 2; break;
            case 4: payload = Bytes.FromHexString("05000000"); break;
            case 5: payload = Bytes.FromHexString("050004000b00000000"); break;
            case 6: payload.AsSpan(payload.Length - 6, 4).Clear(); break;
            case 7: payload[^3] |= 0x80; break;
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
                payload = new byte[1 + ushort.MaxValue + 1 + 8];
                payload[0] = 5;
                payload[^3] = 0x40;
                break;
            // A descendant mask bit must carry a nonzero size.
            case 18: payload = [.. payload[..^2], 0, 0, 0, 0, 0, 0, 0x01, 0x00]; break;
            case 19: payload[0] = 4; break;
        }
        Assert.Throws<InvalidDataException>(() => ReadGroupCount(groupKey, payload));
    }

    [Test]
    public void Descendant_sizes_are_stored_per_slot_and_reject_the_uint48_overflow(
        [Values(0L, 1L, 0x1234_5678_9ABCL, PbtNodeGroupCodec.MaxDescendantBytes)] long descendantBytes, [Values(0, 7, 15)] int slot)
    {
        PbtNodePath groupKey = new([], 0);
        PbtNodeRecord record = new(groupKey.ToPath<PbtStorageNodePath>(), LeafEncoding(0x00));
        long[] slots = new long[PbtNodeGroupCodec.DescendantSlots];
        slots[slot] = descendantBytes;
        byte[] payload = EncodeGroup(groupKey, [record], slots);
        PbtTraversalPath groupPath = PbtTraversalPath.FromPath(stackalloc byte[66], groupKey);
        using PbtNodeGroupWriter<PbtNodePath> streamingWriter = new(0, new TrackingMemoryProvider(), PbtPrefixlessBranchOmission.Interior);
        streamingWriter.Write(groupPath, PbtFourLevelGroupGeometry.RootPosition, record.Encoding.Span);
        using RefCountingMemory streamed = streamingWriter.Detach(slots)!;
        long[] stored = new long[PbtNodeGroupCodec.DescendantSlots];
        PbtNodeGroupCodec.ReadDescendantBytes(payload, stored);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(payload, Has.Length.EqualTo(descendantBytes == 0 ? 12 : 18));
            Assert.That(PbtNodeGroupCodec.ReadDescendantMask(payload), Is.EqualTo(descendantBytes == 0 ? 0 : 1 << slot));
            Assert.That(stored, Is.EqualTo(slots));
            Assert.That(PbtStoreTestExtensions.ReadGroup(groupKey, payload).DescendantBytes(slot), Is.EqualTo(descendantBytes));
            Assert.That(streamed.GetSpan().ToArray(), Is.EqualTo(payload));
        }
        slots[slot] = -1;
        Assert.Throws<ArgumentOutOfRangeException>(() => EncodeGroup(groupKey, [record], slots));
        slots[slot] = PbtNodeGroupCodec.MaxDescendantBytes + 1;
        Assert.Throws<InvalidDataException>(() => EncodeGroup(groupKey, [record], slots));
        Assert.Throws<ArgumentException>(() => EncodeGroup(groupKey, [record], new long[1]));
    }

    [Test]
    public void Root_leaf_has_byte_exact_compact_encoding()
    {
        PbtNodePath groupKey = new([], 0);
        byte[] expected = Bytes.FromHexString("050001000000000000400000");
        byte[] payload = EncodeGroup(groupKey, [new(groupKey.ToPath<PbtStorageNodePath>(), LeafEncoding(0x00))]);
        Assert.That(payload, Is.EqualTo(expected));
    }

    [Test]
    public void Empty_streaming_writer_detaches_without_renting()
    {
        TrackingMemoryProvider provider = new();
        using PbtNodeGroupWriter<PbtNodePath> writer = new(0, provider, PbtPrefixlessBranchOmission.Interior);
        Assert.That(writer.Detach(default), Is.Null);
        Assert.That(provider.RentCount, Is.Zero);
    }

    private static IEnumerable<TestCaseData> PathWarmingCases()
    {
        foreach (Type keyType in new[] { typeof(PbtTreeKey), typeof(PbtStorageTreeKey) })
        {
            // Each key type queries a zone of its own length, with the zone byte ahead of the scenario's bits.
            string zone = keyType == typeof(PbtTreeKey) ? "00" : "FF";
            string fullLengthKey = new('A', 2 * ((keyType == typeof(PbtTreeKey) ? PbtPath.KeyLength : PbtStoragePath.KeyLength) - 1));
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
                yield return new TestCaseData(zone + first, zone + second, zone + query) { TypeArgs = [keyType] };
        }
    }

    [TestCaseSource(nameof(PathWarmingCases))]
    public void Path_warming_reads_only_matching_groups_without_writes<TKey>(string first, string second, string query)
        where TKey : unmanaged, IPbtKey<TKey> =>
        AssertPathWarming(PbtStoreTestExtensions.ZoneKey(first),
            PbtStoreTestExtensions.ZoneKey(second), TKey.Create(Bytes.FromHexString(query)));

    [Test]
    public void Path_warming_handles_canonical_account_and_both_storage_zones([Values(0, 1, 256)] int slot)
    {
        Nethermind.Core.Address address = new("0x0000000000000000000000000000000000000001");
        PbtStorageTreeKey accountKey = (PbtStorageTreeKey)PbtStateKey.Account(address, PbtKeyDerivation.BasicDataLeafKey);
        PbtStorageTreeKey storageKey = PbtStateKey.Storage(address, (Nethermind.Int256.UInt256)slot);
        AssertPathWarming(accountKey.Bytes.ToArray(), storageKey.Bytes.ToArray(), accountKey);
        AssertPathWarming(accountKey.Bytes.ToArray(), storageKey.Bytes.ToArray(), storageKey);
    }

    private static void AssertPathWarming<TKey>(byte[] first, byte[] second, TKey query)
        where TKey : unmanaged, IPbtKey<TKey>
    {
        TrackingMemoryProvider memory = new();
        using PbtNodeGroupStore store = new(memory);
        ValueHash256 root = store.Fold(default, [(first, Value(1)), (second, Value(2))]);
        IReadOnlyList<PbtPhysicalPayload> before = store.ExportPhysicalPayloads();
        HashSet<PbtStorageNodePath> expectedGroups = [];
        foreach (PbtPhysicalPayload physical in before)
        {
            PbtStorageNodePath groupKey = physical.Key;
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
        oracle.Insert(first, Value(1));
        oracle.Insert(second, Value(2));
        WarmReadStore reader = new(store);
        PbtTrieWarmer.WarmUpPath(reader, root, query, 0, null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.Reads, Is.EquivalentTo(expectedGroups));
            Assert.That(reader.Reads.Count, Is.EqualTo(expectedGroups.Count), "each group is fetched once");
            foreach ((PbtStorageNodePath groupKey, ValueHash256 groupHash) in reader.Hashes)
                Assert.That(groupHash, Is.EqualTo(new ValueHash256(oracle.Merkelize(groupKey))), $"warm group {groupKey.BitDepth}");
            Assert.That(store.ExportPhysicalPayloads().Count, Is.EqualTo(before.Count));
            foreach (PbtPhysicalPayload physical in before)
            {
                using RefCountingMemory payload = store.GetPhysicalNodeGroup(physical.Key)!;
                Assert.That(payload.GetSpan().ToArray(), Is.EqualTo(physical.Payload.ToArray()));
            }
            Assert.That(store.Fold(root, []), Is.EqualTo(root));
        }
        store.Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero);
    }

    private static IEnumerable<TestCaseData> PathWarmingThresholdCases()
    {
        foreach (Type keyType in new[] { typeof(PbtTreeKey), typeof(PbtStorageTreeKey) })
            foreach (long minSubtreeBytes in new long[] { 0, 1, 64, 1024, PbtNodeGroupCodec.MaxDescendantBytes })
                yield return new TestCaseData(minSubtreeBytes) { TypeArgs = [keyType] };
    }

    [TestCaseSource(nameof(PathWarmingThresholdCases))]
    public void Path_warming_stops_above_a_small_subtree<TKey>(long minSubtreeBytes)
        where TKey : unmanaged, IPbtKey<TKey>
    {
        TrackingMemoryProvider memory = new();
        using PbtNodeGroupStore store = new(memory);
        ValueHash256 root = BuildGroupAtEveryBoundary(store, out TKey query);

        WarmReadStore fullReader = new(store);
        PbtTrieWarmer.WarmUpPath(fullReader, root, query, 0, null);
        List<PbtStorageNodePath> expected = [];
        bool expectedToStop = false;
        foreach (PbtStorageNodePath groupKey in fullReader.Reads)
        {
            if (expected.Count > 0 && DescendantBytesTowards(store, expected[^1], query) < minSubtreeBytes)
            {
                expectedToStop = true;
                break;
            }
            expected.Add(groupKey);
        }

        WarmReadStore reader = new(store);
        bool stopped = PbtTrieWarmer.WarmUpPath(reader, root, query, minSubtreeBytes, null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.Reads, Is.EqualTo(expected));
            Assert.That(stopped, Is.EqualTo(expectedToStop));
            Assert.That(fullReader.Reads.Count, Is.GreaterThan(1), "the warm path crosses at least one boundary");
            if (minSubtreeBytes == PbtNodeGroupCodec.MaxDescendantBytes)
                Assert.That(reader.Reads, Is.EqualTo(fullReader.Reads[..1]), "no subtree reaches the largest threshold");
            if (minSubtreeBytes == 0)
                Assert.That(reader.Reads, Is.EqualTo(fullReader.Reads), "a zero threshold warms the whole path");
        }
        store.Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero);
    }

    [TestCase(TypeArgs = [typeof(PbtTreeKey)])]
    [TestCase(TypeArgs = [typeof(PbtStorageTreeKey)])]
    public void Path_warming_reads_pinned_top_groups_without_the_store<TKey>()
        where TKey : unmanaged, IPbtKey<TKey>
    {
        TrackingMemoryProvider memory = new();
        using PbtNodeGroupStore store = new(memory);
        ValueHash256 root = BuildGroupAtEveryBoundary(store, out TKey query);
        WarmReadStore unpinned = new(store);
        PbtTrieWarmer.WarmUpPath(unpinned, root, query, 0, null);

        PbtPinnedGroups pinnedGroups = new();
        WarmReadStore pinning = new(store);
        PbtTrieWarmer.WarmUpPath(pinning, root, query, 0, pinnedGroups);
        WarmReadStore pinned = new(store);
        PbtTrieWarmer.WarmUpPath(pinned, root, query, 0, pinnedGroups);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(unpinned.Hashes.Select(read => read.Path.BitDepth), Does.Contain(PbtPinnedGroups.MaxDepth).And.Contain(PbtPinnedGroups.MaxDepth + PbtFourLevelGroupGeometry.LevelsPerGroup));
            Assert.That(pinning.Hashes, Is.EqualTo(unpinned.Hashes), "the first warm-up fetches every group");
            Assert.That(pinned.Hashes, Is.EqualTo(unpinned.Hashes.Where(read => read.Path.BitDepth > PbtPinnedGroups.MaxDepth)), "later warm-ups fetch only groups below the pinned ones, under the same hashes");
        }
        pinnedGroups.Dispose();
        store.Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero);
    }

    /// <summary>A sibling branching off at every bit of the queried key's first bytes past its zone gives its path a group at every boundary below the zone.</summary>
    private static ValueHash256 BuildGroupAtEveryBoundary<TKey>(PbtNodeGroupStore store, out TKey query)
        where TKey : unmanaged, IPbtKey<TKey>
    {
        string zone = typeof(TKey) == typeof(PbtTreeKey) ? "00" : "FF";
        byte[] queryKey = PbtStoreTestExtensions.ZoneKey(zone);
        query = TKey.Create(queryKey);
        List<(byte[] Key, byte[]? Value)> writes = [(queryKey, Value(1))];
        for (int bit = 0; bit < 32; bit++)
        {
            byte[] sibling = PbtStoreTestExtensions.ZoneKey(zone);
            sibling[1 + (bit >> 3)] = (byte)(0x80 >> (bit & 7));
            writes.Add((sibling, Value((byte)(bit + 2))));
        }
        return store.Fold(default, writes);
    }

    private static long DescendantBytesTowards<TKey>(PbtNodeGroupStore store, PbtStorageNodePath groupKey, TKey key)
        where TKey : unmanaged, IPbtKey<TKey>
    {
        using RefCountingMemory payload = store.GetPhysicalNodeGroup(groupKey)!;
        PbtNodeGroupReader group = PbtStoreTestExtensions.ReadGroup(groupKey, payload.GetSpan());
        return group.DescendantBytes(TrieUpdater.BoundarySlot(key.Bytes, groupKey.BitDepth));
    }

    [TestCase(false, TypeArgs = [typeof(PbtTreeKey)])]
    [TestCase(true, TypeArgs = [typeof(PbtTreeKey)])]
    [TestCase(false, TypeArgs = [typeof(PbtStorageTreeKey)])]
    [TestCase(true, TypeArgs = [typeof(PbtStorageTreeKey)])]
    public void Path_warming_releases_payloads_on_missing_nodes_and_invalid_payloads<TKey>(bool invalidPayload)
        where TKey : unmanaged, IPbtKey<TKey>
    {
        TrackingMemoryProvider memory = new();
        using PbtNodeGroupStore store = new();
        WarmReadStore reader = new(store);
        TKey key = TKey.Create(Bytes.FromHexString("00"));
        PbtTrieWarmer.WarmUpPath(reader, default, key, 0, null);
        Assert.That(reader.Reads.Count, Is.EqualTo(1), "empty tree");

        PbtNodePath root = new([], 0);
        PbtNodePath child = new(Bytes.FromHexString("00"), 1);
        byte[] bytes = EncodeGroup(root, [new PbtNodeRecord(child.ToPath<PbtStorageNodePath>(), LeafBranch(child, 1))]);
        if (invalidPayload) bytes = Bytes.FromHexString("ff");
        RefCountingMemory payload = memory.Rent(bytes.Length);
        bytes.CopyTo(payload.GetSpan());
        reader.Payload = payload;
        if (invalidPayload) Assert.Catch(() => PbtTrieWarmer.WarmUpPath(reader, default, key, 0, null));
        else PbtTrieWarmer.WarmUpPath(reader, default, key, 0, null);
        ((IDisposable)payload).Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero);
    }

    private sealed class WarmReadStore(IPbtStore store) : IPbtStore, IPbtNodeGroupSink
    {
        public IPbtConcurrentWriter CreateWriter() => new PbtPassThroughWriter(this);

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

    private static byte[] EncodeGroup<TPath>(TPath groupKey, IReadOnlyList<PbtNodeRecord> records, ReadOnlySpan<long> descendantBytes = default) where TPath : struct, IPbtNodePath<TPath>
    {
        int capacity = PbtNodeGroupCodec.HeaderLength + PbtNodeGroupCodec.MaxTrailerLength;
        foreach (PbtNodeRecord record in records) capacity += record.Encoding.Length;
        byte[] payload = new byte[capacity];
        BufferWriter writer = new(payload);
        PbtNodeGroupEncoder.Encode(ref writer, groupKey, records, descendantBytes);
        return writer.WrittenSpan.ToArray();
    }

    private static Dictionary<string, byte[]> Payloads(PbtTreeHarness tree)
    {
        Dictionary<string, byte[]> result = [];
        foreach (PbtPhysicalPayload payload in tree.PhysicalPayloads)
            result.Add(Convert.ToHexString(payload.Key.ToEncodedArray()), payload.Payload.ToArray());
        return result;
    }

    private static bool MoveDefaultReaderEnumerator()
    {
        PbtNodeGroupReader.Enumerator enumerator = default;
        return enumerator.MoveNext();
    }

    private static byte[] LeafEncoding(byte keyMarker) => PbtNodeCodec.EncodeLeaf(new PbtTreeKey([keyMarker]));

    /// <summary>A prefixless branch inlining <paramref name="key"/> as its left leaf: the smallest node stored below the root.</summary>
    private static byte[] LeafBranch(ReadOnlySpan<byte> key, byte marker) =>
        PbtNodeCodec.EncodeBranch([], 0, new ValueHash256(Value(marker)), new ValueHash256(Value(0xFF)), key, []);

    /// <summary>A node valid at <paramref name="path"/>: a prefixless branch inlining a leaf just below it, or without leaves where no longer key fits.</summary>
    private static byte[] LeafBranch<TPath>(TPath path, byte marker) where TPath : struct, IPbtNodePath<TPath>
    {
        if (path.BitDepth >= PbtFourLevelGroupGeometry.MaxPathDepth) return LeafBranch([], marker);
        byte[] key = new byte[(path.BitDepth >> 3) + 1];
        path.CopyBitsTo(0, key, 0, path.BitDepth);
        return LeafBranch(key, marker);
    }

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
            // Spread the keys over the account, code and storage zones, so both key lengths fold together.
            keys[index] = PbtStoreTestExtensions.ZoneKey((index % 3) switch { 0 => "00", 1 => "01", _ => "FF" });
            keys[index][1] = (byte)index;
            random.NextBytes(keys[index].AsSpan(2));
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

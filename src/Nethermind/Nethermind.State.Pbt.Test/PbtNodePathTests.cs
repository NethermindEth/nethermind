// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture(typeof(PbtNodePath))]
[TestFixture(typeof(PbtStorageNodePath))]
public class PbtNodePathTests<TPath> where TPath : struct, IPbtNodePath<TPath>
{
    [Test]
    public void Traversal_path_restores_siblings_and_preserves_snapshots([Values(0, 3, 6, -1)] int depth)
    {
        if (depth == -1) depth = (TPath.MaxBitDepth - PbtThreeLevelGroupGeometry.LevelsPerGroup) / PbtThreeLevelGroupGeometry.LevelsPerGroup * PbtThreeLevelGroupGeometry.LevelsPerGroup;
        byte[] key = Bytes.FromHexString(new string('D', TPath.MaxBitDepth / 4));
        Span<byte> buffer = stackalloc byte[TPath.MaxBitDepth / 8];
        buffer.Fill(0xFF);
        PbtTraversalPath path = new(buffer);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(path.BitDepth, Is.Zero);
            Assert.That(buffer.ToArray(), Is.All.Zero);
        }
        path.AppendKey(key, depth);
        TPath parent = PbtTestPaths.Prefix<TPath>(key, depth);
        Assert.That(path.ToPath<TPath>(), Is.EqualTo(parent));

        path.AppendMut(7);
        TPath snapshot = path.ToPath<TPath>();
        Assert.That(snapshot, Is.EqualTo(parent.AppendBits(7, 3)));
        path.AppendKey(key, TPath.MaxBitDepth);
        TPath extended = path.ToPath<TPath>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(path.BitDepth, Is.EqualTo(TPath.MaxBitDepth));
            Assert.That(extended.Prefix(depth + 3), Is.EqualTo(snapshot));
            for (int bit = depth + 3; bit < TPath.MaxBitDepth; bit++)
                Assert.That(extended.GetBit(bit), Is.EqualTo(TrieUpdater.GetBit(key, bit)), $"appended bit {bit}");
        }
        path.Truncate(depth);
        Assert.That(path.ToPath<TPath>(), Is.EqualTo(parent));
        path.AppendMut(0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(path.BitDepth, Is.EqualTo(depth + 3));
            Assert.That(path.ToPath<TPath>(), Is.EqualTo(parent.AppendBits(0, 3)));
            Assert.That(snapshot, Is.EqualTo(parent.AppendBits(7, 3)));
        }
        path.Truncate(0);
        path.AppendKey(key, TPath.MaxBitDepth);
        Assert.That(path.ToPath<TPath>(), Is.EqualTo(TPath.Create(key, TPath.MaxBitDepth)));
    }

    [Test]
    public void Path_operations_preserve_logical_bits_and_copy_boundaries([Values(0, 4, 8, -1)] int depth)
    {
        if (depth == -1) depth = TPath.MaxBitDepth;
        byte[] bytes = new byte[(depth + 7) >> 3];
        Array.Fill(bytes, (byte)0xAD);
        if ((depth & 7) != 0) bytes[^1] &= (byte)(0xFF << (8 - (depth & 7)));
        TPath path = TPath.Create(bytes, depth);
        byte[] copiedBits = new byte[bytes.Length];
        PbtNodePathOperations.CopyTo(path, copiedBits);
        byte[] changed = (byte[])bytes.Clone();
        if (depth != 0) changed[(depth - 1) >> 3] ^= (byte)(0x80 >> ((depth - 1) & 7));
        PbtStorageNodePath changedPath = new(changed, depth);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(copiedBits, Is.EqualTo(bytes));
            Assert.That(Math.Sign(path.ToPath<PbtStorageNodePath>().CompareTo(changedPath)), Is.EqualTo(Math.Sign(bytes.AsSpan().SequenceCompareTo(changed))));
            for (int index = 0; index < bytes.Length; index++)
                Assert.That(path.GetByte(index), Is.EqualTo(bytes[index]));
            Assert.Throws<IndexOutOfRangeException>(() => path.GetByte(-1));
            Assert.Throws<IndexOutOfRangeException>(() => path.GetByte(bytes.Length));
        }
    }

    [Test]
    public void Path_hash_code_is_stable_across_construction_and_distinct_across_depth([Values(0, 4, 8, 12, 264, 268)] int depth)
    {
        byte[] bytes = new byte[(depth + 7) >> 3];
        Array.Fill(bytes, (byte)0xA0);
        TPath path = TPath.Create(bytes, depth);
        int expected = path.GetHashCode();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(path.ToPath<PbtStorageNodePath>().GetHashCode(), Is.EqualTo(expected));
            Assert.That(path.ToPath<PbtNodePath>().GetHashCode(), Is.EqualTo(expected));
            if (depth == 0) Assert.That(default(TPath).GetHashCode(), Is.EqualTo(expected));
            // The same bytes are a valid path four bits shorter or longer, so only the depth distinguishes the hashes.
            else Assert.That(TPath.Create(bytes, depth % 8 == 0 ? depth - 4 : depth + 4).GetHashCode(), Is.Not.EqualTo(expected));
        }
    }

    [Test]
    public void Group_geometry_reconstructs_internal_and_boundary_paths([Values(0, 3, 6, 267, 270, 525)] int groupDepth, [Range(0, 14)] int position)
    {
        if (position == PbtThreeLevelGroupGeometry.RootPosition && groupDepth != 0
            || groupDepth + PbtThreeLevelGroupGeometry.LocalPathOf(position).Length > TPath.MaxBitDepth) return;
        byte[] groupBytes = new byte[(groupDepth + 7) >> 3];
        Array.Fill(groupBytes, (byte)0xA0);
        TPath groupKey = TPath.Create(groupBytes, groupDepth);

        List<string> paths = [];
        Visit("");
        string relativePath = paths[position];
        int depth = groupDepth + relativePath.Length;
        byte[] expectedBytes = new byte[(depth + 7) >> 3];
        groupBytes.CopyTo(expectedBytes, 0);
        for (int index = 0; index < relativePath.Length; index++)
            if (relativePath[index] == '1') expectedBytes[(groupDepth + index) >> 3] |= (byte)(0x80 >> ((groupDepth + index) & 7));
        TPath expected = TPath.Create(expectedBytes, depth);
        TPath actual = PbtTestPaths.PathOf(groupKey, position);
        PbtNodeGroupLocation<TPath> location = PbtTestPaths.Locate(actual);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(location.GroupKey, Is.EqualTo(groupKey));
            Assert.That(location.Position, Is.EqualTo(position));
            Assert.That(PbtThreeLevelGroupGeometry.WidthOf(position), Is.EqualTo(PbtThreeLevelGroupGeometry.BoundarySlots >> relativePath.Length));
        }

        void Visit(string path)
        {
            if (path.Length < PbtThreeLevelGroupGeometry.LevelsPerGroup)
            {
                Visit(path + "0");
                Visit(path + "1");
            }
            paths.Add(path);
        }
    }
}

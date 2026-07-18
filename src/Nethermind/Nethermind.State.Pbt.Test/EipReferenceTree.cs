// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.State.Pbt.Test;

/// <summary>
/// Direct port of the EIP-8297 reference python (<c>BinaryTree.insert</c> / <c>merkelize</c>),
/// kept structurally independent of the production code to act as its oracle.
/// </summary>
public sealed class EipReferenceTree
{
    private object? _root;

    private sealed class RefStemNode(byte[] stem)
    {
        public byte[] Stem { get; } = stem;
        public byte[]?[] Values { get; } = new byte[]?[256];
    }

    private sealed class RefInternalNode
    {
        public object? Left { get; set; }
        public object? Right { get; set; }
    }

    public void Insert(ReadOnlySpan<byte> key, byte[] value)
    {
        if (key.Length != 32 || value.Length != 32) throw new ArgumentException("key and value must be 32 bytes");
        byte[] stem = key[..31].ToArray();
        byte subIndex = key[31];

        if (_root is null)
        {
            RefStemNode stemNode = new(stem);
            stemNode.Values[subIndex] = value;
            _root = stemNode;
            return;
        }

        _root = Insert(_root, stem, subIndex, value, 0);
    }

    private static object Insert(object? node, byte[] stem, byte subIndex, byte[] value, int depth)
    {
        if (node is null)
        {
            RefStemNode created = new(stem);
            created.Values[subIndex] = value;
            return created;
        }

        int[] stemBits = ToBits(stem);
        if (node is RefStemNode stemNode)
        {
            if (stemNode.Stem.AsSpan().SequenceEqual(stem))
            {
                stemNode.Values[subIndex] = value;
                return stemNode;
            }

            return SplitLeaf(stemNode, stemBits, ToBits(stemNode.Stem), subIndex, value, depth);
        }

        // Two stems parting only at the last bit split at depth 247, leaving their stem nodes at 248 —
        // legal, and handled above. Only an internal node there would be, every bit being spent.
        if (depth >= 248) throw new InvalidOperationException("depth must be less than 248");

        RefInternalNode internalNode = (RefInternalNode)node;
        if (stemBits[depth] == 0)
        {
            internalNode.Left = Insert(internalNode.Left, stem, subIndex, value, depth + 1);
        }
        else
        {
            internalNode.Right = Insert(internalNode.Right, stem, subIndex, value, depth + 1);
        }

        return internalNode;
    }

    private static object SplitLeaf(RefStemNode leaf, int[] stemBits, int[] existingStemBits, byte subIndex, byte[] value, int depth)
    {
        RefInternalNode newInternal = new();
        if (stemBits[depth] == existingStemBits[depth])
        {
            object split = SplitLeaf(leaf, stemBits, existingStemBits, subIndex, value, depth + 1);
            if (stemBits[depth] == 0)
            {
                newInternal.Left = split;
            }
            else
            {
                newInternal.Right = split;
            }
        }
        else
        {
            RefStemNode created = new(ToBytes(stemBits));
            created.Values[subIndex] = value;
            if (stemBits[depth] == 0)
            {
                newInternal.Left = created;
                newInternal.Right = leaf;
            }
            else
            {
                newInternal.Right = created;
                newInternal.Left = leaf;
            }
        }

        return newInternal;
    }

    public byte[] Merkelize() => Merkelize(_root);

    private static byte[] Merkelize(object? node)
    {
        if (node is null) return new byte[32];
        if (node is RefInternalNode internalNode)
        {
            return Hash([.. Merkelize(internalNode.Left), .. Merkelize(internalNode.Right)]);
        }

        RefStemNode stemNode = (RefStemNode)node;
        byte[][] level = new byte[256][];
        for (int i = 0; i < 256; i++)
        {
            level[i] = Hash(stemNode.Values[i]);
        }

        while (level.Length > 1)
        {
            byte[][] newLevel = new byte[level.Length / 2][];
            for (int i = 0; i < newLevel.Length; i++)
            {
                newLevel[i] = Hash([.. level[2 * i], .. level[2 * i + 1]]);
            }

            level = newLevel;
        }

        return Hash([.. stemNode.Stem, 0, .. level[0]]);
    }

    private static byte[] Hash(byte[]? data)
    {
        if (data is null || (data.Length == 64 && !data.AsSpan().ContainsAnyExcept((byte)0))) return new byte[32];
        if (data.Length is not (32 or 64)) throw new ArgumentException("data must be 32 or 64 bytes");
        byte[] output = new byte[32];
        Blake3.Hasher.Hash(data, output);
        return output;
    }

    private static int[] ToBits(byte[] data)
    {
        int[] bits = new int[data.Length * 8];
        for (int i = 0; i < bits.Length; i++)
        {
            bits[i] = (data[i >> 3] >> (7 - (i & 7))) & 1;
        }

        return bits;
    }

    private static byte[] ToBytes(int[] bits)
    {
        byte[] data = new byte[bits.Length / 8];
        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i] != 0) data[i >> 3] |= (byte)(1 << (7 - (i & 7)));
        }

        return data;
    }
}

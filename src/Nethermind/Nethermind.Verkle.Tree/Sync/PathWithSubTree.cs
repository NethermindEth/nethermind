// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Verkle.Tree.Utils;

namespace Nethermind.Verkle.Tree.Sync;

public class PathWithSubTree
{
    public PathWithSubTree(Stem stem, LeafInSubTree[] subTree)
    {
        Path = stem;
        SubTree = subTree;
    }

    public Stem  Path { get; set; }
    public LeafInSubTree[]  SubTree { get; set; }
}

public readonly struct LeafInSubTree
{
    public readonly byte SuffixByte;
    public readonly byte[]? Leaf;

    public LeafInSubTree(byte suffixByte, byte[]? leaf)
    {
        SuffixByte = suffixByte;
        Leaf = leaf;
    }

    public static implicit operator LeafInSubTree((byte, byte[]) leafWithSubIndex)
    {
        return new LeafInSubTree(leafWithSubIndex.Item1, leafWithSubIndex.Item2);
    }

    public static implicit operator LeafInSubTree(KeyValuePair<byte, byte[]> leafWithSubIndex)
    {
        return new LeafInSubTree(leafWithSubIndex.Key, leafWithSubIndex.Value);
    }

    public override string ToString()
    {
        return $"{SuffixByte}:{Leaf?.ToHexString()}";
    }
}

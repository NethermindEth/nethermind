// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core.Extensions;

namespace Nethermind.Verkle.Tree.Sync;

public readonly struct LeafInSubTree(byte suffixByte, byte[]? leaf)
    : IEquatable<LeafInSubTree>, IEqualityOperators<LeafInSubTree, LeafInSubTree, bool>
{
    public readonly byte SuffixByte = suffixByte;
    public readonly byte[]? Leaf = leaf;

    public static implicit operator LeafInSubTree((byte, byte[]) leafWithSubIndex)
    {
        return new LeafInSubTree(leafWithSubIndex.Item1, leafWithSubIndex.Item2);
    }

    public static implicit operator LeafInSubTree(KeyValuePair<byte, byte[]> leafWithSubIndex)
    {
        return new LeafInSubTree(leafWithSubIndex.Key, leafWithSubIndex.Value);
    }

    public bool Equals(in LeafInSubTree other)
    {
        return SuffixByte == other.SuffixByte && Leaf.AsSpan().SequenceEqual(other.Leaf);
    }

    public bool Equals(LeafInSubTree other) => Equals(in other);

    public override string ToString()
    {
        return $"{SuffixByte}:{Leaf?.ToHexString()}";
    }

    public override bool Equals(object obj)
    {
        return obj is LeafInSubTree tree && Equals(tree);
    }

    public override int GetHashCode() => throw new NotImplementedException();
    public static bool operator ==(LeafInSubTree left, LeafInSubTree right) => left.Equals(in right);

    public static bool operator !=(LeafInSubTree left, LeafInSubTree right) => !left.Equals(in right);
}

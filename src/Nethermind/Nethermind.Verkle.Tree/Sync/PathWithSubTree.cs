// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Numerics;
using Nethermind.Core.Verkle;

namespace Nethermind.Verkle.Tree.Sync;

public readonly struct PathWithSubTree(Stem stem, LeafInSubTree[] subTree)
    : IEquatable<PathWithSubTree>, IEqualityOperators<PathWithSubTree, PathWithSubTree, bool>
{
    public Stem Path { get; } = stem;
    public LeafInSubTree[] SubTree { get; } = subTree;

    public bool Equals(in PathWithSubTree other)
    {
        return Path == other.Path && SubTree.SequenceEqual(other.SubTree);
    }

    public bool Equals(PathWithSubTree other) => Equals(in other);
    public static bool operator ==(PathWithSubTree left, PathWithSubTree right) => left.Equals(in right);
    public static bool operator !=(PathWithSubTree left, PathWithSubTree right) => !left.Equals(in right);

    public override bool Equals(object obj) => obj is PathWithSubTree pws && Equals(in pws);
    public override int GetHashCode() => throw new NotImplementedException();
}


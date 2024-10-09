// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

[StructLayout(LayoutKind.Auto)]
internal readonly struct HashAndTinyPath : IEquatable<HashAndTinyPath>
{
    public readonly ValueHash256 addr;
    public readonly TinyTreePath path;

    public HashAndTinyPath(Hash256? hash, in TinyTreePath path)
    {
        addr = hash ?? default;
        this.path = path;
    }
    public HashAndTinyPath(in ValueHash256 hash, in TinyTreePath path)
    {
        addr = hash;
        this.path = path;
    }

    public bool Equals(HashAndTinyPath other) => addr == other.addr && path.Equals(in other.path);
    public override bool Equals(object? obj) => obj is HashAndTinyPath other && Equals(other);
    public override int GetHashCode()
    {
        var addressHash = addr != default ? addr.GetHashCode() : 1;
        return path.GetHashCode() ^ addressHash;
    }
}
